using System.Globalization;
using System.Text.Json;
using Backup.Engine;
using Microsoft.Data.Sqlite;

namespace Backup.Infrastructure;

public sealed class SqliteStore : IBackupStore
{
    private readonly string connectionString;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string DataDirectory { get; }

    public SqliteStore(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        if (DataDirectory.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("ログ・DBはローカルに保存してください。");
        Directory.CreateDirectory(DataDirectory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(DataDirectory, "backup.db"), DefaultTimeout = 30 }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Tasks (Id TEXT PRIMARY KEY, Json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Runs (Id TEXT PRIMARY KEY, TaskId TEXT NOT NULL, Started TEXT NOT NULL, Json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Runs_Task ON Runs(TaskId, Started);
            CREATE TABLE IF NOT EXISTS Units (RunId TEXT NOT NULL, UnitKey TEXT NOT NULL, Json TEXT NOT NULL, PRIMARY KEY(RunId, UnitKey));
            CREATE TABLE IF NOT EXISTS States (TaskId TEXT NOT NULL, UnitKey TEXT NOT NULL, Json TEXT NOT NULL, PRIMARY KEY(TaskId, UnitKey));
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    private List<T> Query<T>(string sql, params (string Key, object Value)[] args)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value);
        using var reader = cmd.ExecuteReader(); List<T> result = [];
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0))!);
        return result;
    }
    private void Execute(string sql, params (string Key, object Value)[] args)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value);
        cmd.ExecuteNonQuery();
    }
    public IReadOnlyList<BackupTask> GetTasks() => Query<BackupTask>("SELECT Json FROM Tasks ORDER BY Id");
    public BackupTask GetTask(string id) => GetTasks().SingleOrDefault(x => x.Id == id) ?? throw new ArgumentException("タスクが見つかりません。");
    public void SaveTask(BackupTask task)
    {
        task = task.Validate();
        if (SafePaths.Overlaps(task.Source, DataDirectory) || SafePaths.Overlaps(task.Destination, DataDirectory))
            throw new ArgumentException("データディレクトリとバックアップ元・保存先を重複させないでください。");
        foreach (var other in GetTasks().Where(x => x.Id != task.Id))
            if (SafePaths.Overlaps(task.Destination, other.Destination) || SafePaths.Overlaps(task.Destination, other.Source) || SafePaths.Overlaps(task.Source, other.Destination))
                throw new ArgumentException("他のタスクの保存先またはバックアップ元と重複しています。");
        var previous = GetTasks().SingleOrDefault(x => x.Id == task.Id);
        if (previous is not null && GetStates(task.Id).Count > 0 &&
            (previous.Source != task.Source || previous.Destination != task.Destination || previous.Mode != task.Mode || previous.UnitDepth != task.UnitDepth))
            throw new ArgumentException("実行済みタスクの元・保存先・方式・階層は変更できません。新しいタスクとして作成してください。");
        Execute("INSERT OR REPLACE INTO Tasks VALUES ($id,$json)", ("$id", task.Id), ("$json", JsonSerializer.Serialize(task, Json)));
    }
    public IReadOnlyList<RunRecord> GetRuns(string? taskId = null) => taskId is null
        ? Query<RunRecord>("SELECT Json FROM Runs ORDER BY Started DESC LIMIT 500")
        : Query<RunRecord>("SELECT Json FROM Runs WHERE TaskId=$id ORDER BY Started DESC LIMIT 500", ("$id", taskId));
    public IReadOnlyList<UnitResult> GetUnits(string runId) => Query<UnitResult>("SELECT Json FROM Units WHERE RunId=$id ORDER BY rowid", ("$id", runId));
    public void StartRun(RunRecord run) => FinishRun(run);
    public void FinishRun(RunRecord run) => Execute("INSERT OR REPLACE INTO Runs VALUES ($id,$task,$start,$json)",
        ("$id", run.Id), ("$task", run.TaskId), ("$start", run.Started.ToString("O", CultureInfo.InvariantCulture)), ("$json", JsonSerializer.Serialize(run)));
    public void SaveUnit(string runId, UnitResult result) => Execute("INSERT OR REPLACE INTO Units VALUES ($run,$key,$json)",
        ("$run", runId), ("$key", result.Key), ("$json", JsonSerializer.Serialize(result)));
    public IReadOnlyList<UnitState> GetStates(string taskId) => Query<UnitState>("SELECT Json FROM States WHERE TaskId=$id", ("$id", taskId));
    public void SaveState(string taskId, UnitState state) => Execute("INSERT OR REPLACE INTO States VALUES ($task,$key,$json)",
        ("$task", taskId), ("$key", state.Key), ("$json", JsonSerializer.Serialize(state)));
    public void RemoveState(string taskId, string key) => Execute("DELETE FROM States WHERE TaskId=$task AND UnitKey=$key", ("$task", taskId), ("$key", key));
    public void RecoverInterrupted(string taskId)
    {
        foreach (var run in GetRuns(taskId).Where(x => x.Status == ResultStatus.Running))
            FinishRun(run with { Status = ResultStatus.Interrupted, Finished = DateTimeOffset.Now, Error = "前回の実行に正常な終了記録がありません。" });
    }
    public void Prune(string taskId, DateTimeOffset before)
    {
        // Keep at most 500 visible records; pruning itself has no UI limit.
        foreach (var run in Query<RunRecord>("SELECT Json FROM Runs WHERE TaskId=$id AND Started<$before", ("$id", taskId), ("$before", before.ToString("O"))))
        {
            if (run.Status == ResultStatus.Running) continue;
            var path = Path.GetDirectoryName(run.LogPath)!;
            if (!SafePaths.Contains(Path.Combine(DataDirectory, "logs"), path)) continue;
            SafePaths.NoReparseAncestors(path);
            if (Directory.Exists(path)) Directory.Delete(path, true);
            Execute("DELETE FROM Units WHERE RunId=$id; DELETE FROM Runs WHERE Id=$id", ("$id", run.Id));
        }
    }
}
