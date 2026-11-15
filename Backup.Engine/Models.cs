namespace Backup.Engine;

public enum BackupMode { Zip, Copy }
public enum ResultStatus { Running, Success, Warning, PartialFailure, Failed, Cancelled, Interrupted, Skipped }

public sealed record BackupTask
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "新しいタスク";
    public string Source { get; init; } = "";
    public string Destination { get; init; } = "";
    public BackupMode Mode { get; init; } = BackupMode.Zip;
    public int UnitDepth { get; init; } = 1;
    public int ParallelUnits { get; init; } = 2;
    public int CopyThreads { get; init; } = 8;
    public int FileRetries { get; init; } = 3;
    public int FileWaitSeconds { get; init; } = 10;
    public int NetworkRetries { get; init; } = 4;
    public int NetworkWaitSeconds { get; init; } = 15;
    public int RetentionDays { get; init; } = 90;
    public int CompressionLevel { get; init; } = 1;
    public string? DailyAt { get; init; }
    public BackupSchedule? Schedule { get; init; }
    public string[] UncompressedFolders { get; init; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public BackupSchedule? EffectiveSchedule => Schedule ?? (DailyAt is { } time ? new BackupSchedule { Times = [time] } : null);

    public BackupTask Validate()
    {
        if (!Guid.TryParseExact(Id, "N", out _) || string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("タスクIDまたは名前が不正です。");
        if (!Enum.IsDefined(Mode) || UnitDepth is < 1 or > 32 || ParallelUnits is < 1 or > 16 ||
            CopyThreads is < 1 or > 128 || FileRetries is < 0 or > 100 || FileWaitSeconds is < 0 or > 3600 ||
            NetworkRetries is < 0 or > 20 || NetworkWaitSeconds is < 0 or > 3600 || RetentionDays < 1 ||
            CompressionLevel is < 0 or > 9)
            throw new ArgumentException("階層・並列数・リトライ等の設定値が範囲外です。");
        if (DailyAt is not null && !TimeOnly.TryParseExact(DailyAt, "HH:mm", out _))
            throw new ArgumentException("実行時刻は HH:mm 形式で指定してください。");
        var schedule = EffectiveSchedule?.Validate();
        var source = SafePaths.Normalize(Source);
        var destination = SafePaths.Normalize(Destination);
        if (SafePaths.Overlaps(source, destination))
            throw new ArgumentException("バックアップ元と保存先には、同一または包含するパスを指定できません。");
        if (Path.GetPathRoot(destination)!.TrimEnd('\\', '/').Equals(destination.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("保存先にはドライブ/共有のルートではなく専用フォルダを指定してください。");
        if (UncompressedFolders is null) throw new ArgumentException("非圧縮フォルダの設定が不正です。");
        var folders = UncompressedFolders.Select(folder =>
        {
            var value = folder.Replace('/', '\\').TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Split('\\').Any(p => p is "." or ".." or "" || p.EndsWith('.') || p.EndsWith(' ')))
                throw new ArgumentException("非圧縮フォルダはバックアップ元からの相対パスで指定してください。");
            var full = SafePaths.Normalize(Path.Combine(source, value));
            if (!SafePaths.Contains(source, full)) throw new ArgumentException("非圧縮フォルダがバックアップ元の範囲外です。");
            return Path.GetRelativePath(source, full);
        }).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Length).ToArray();
        folders = folders.Where(f => !folders.Any(parent => !parent.Equals(f, StringComparison.OrdinalIgnoreCase) && SafePaths.Contains(parent, f))).ToArray();
        return this with { Source = source, Destination = destination, Schedule = schedule, DailyAt = null, UncompressedFolders = folders };
    }
}

public sealed record BackupUnit(string Key, string Source, string Output, bool Recursive, BackupMode? Mode = null);
public sealed record Entry(string Path, bool IsDirectory, long Size, long Ticks);
public sealed record Snapshot(string Fingerprint, long Files, long Bytes, string ListPath);
public sealed record UnitState(string Key, string Output, string Fingerprint, ResultStatus Status, BackupMode? Mode = null, bool Recursive = true);
public sealed record UnitResult(string Key, ResultStatus Status, DateTimeOffset Started, DateTimeOffset Finished,
    string LogPath, string? Error = null, long Files = 0, long Bytes = 0);
public sealed record RunRecord(string Id, string TaskId, DateTimeOffset Started, DateTimeOffset? Finished,
    ResultStatus Status, string LogPath, string? Error, int Completed, int Failed);
public sealed record RunProgress(string RunId, string Message, int Completed, int Total);

public interface IBackupStore
{
    IReadOnlyList<BackupTask> GetTasks();
    BackupTask GetTask(string id);
    void SaveTask(BackupTask task);
    IReadOnlyList<RunRecord> GetRuns(string? taskId = null);
    IReadOnlyList<UnitResult> GetUnits(string runId);
    void StartRun(RunRecord run);
    void FinishRun(RunRecord run);
    void SaveUnit(string runId, UnitResult result);
    IReadOnlyList<UnitState> GetStates(string taskId);
    void SaveState(string taskId, UnitState state);
    void RemoveState(string taskId, string key);
    void RecoverInterrupted(string taskId);
    void Prune(string taskId, DateTimeOffset before);
}

public interface IBackupBackend
{
    Task ExecuteAsync(BackupTask task, BackupUnit unit, Snapshot snapshot, string logPath, CancellationToken ct);
}

public interface ISourceScanner
{
    Snapshot Scan(BackupUnit unit, string workingDirectory, CancellationToken ct);
    void SynchronizeDeletions(BackupTask task, string workingDirectory, Action<string> log, CancellationToken ct);
}
