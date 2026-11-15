using System.Text.Json;

namespace Backup.Engine;

public sealed class BackupRunner(IBackupStore store, ISourceScanner scanner, IBackupBackend backend, string dataDirectory)
{
    public async Task<RunRecord> RunAsync(string taskId, IProgress<RunProgress>? progress = null, CancellationToken ct = default)
    {
        // Serializes tasks sharing this local store. Unit-level work remains parallel.
        using var executionLock = new FileStream(Path.Combine(dataDirectory, "execution.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var task = store.GetTask(taskId).Validate();
        SafePaths.NoReparseAncestors(task.Source); SafePaths.NoReparseAncestors(task.Destination);
        store.RecoverInterrupted(task.Id);
        var id = Guid.NewGuid().ToString("N");
        var work = Path.Combine(dataDirectory, "logs", task.Id, id);
        Directory.CreateDirectory(work);
        var run = new RunRecord(id, task.Id, DateTimeOffset.Now, null, ResultStatus.Running, Path.Combine(work, "run.log"), null, 0, 0);
        store.StartRun(run);
        var logGate = new object();
        void Log(string message) { lock (logGate) File.AppendAllText(run.LogPath, $"{DateTimeOffset.Now:O} {message}\n"); }
        int completed = 0, failed = 0;
        FileStream? destinationLock = null;
        try
        {
            Log($"開始: {task.Name}");
            await RetryAccess(task, Log, ct);
            SafePaths.CheckTarget(task.Destination, Path.Combine(task.Destination, ".backup-tool.lock"));
            destinationLock = new FileStream(Path.Combine(task.Destination, ".backup-tool.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            ClaimDestination(task);
            CheckDestinationTree(task.Destination);
            var units = UnitPlanner.Plan(task);
            var oldStates = store.GetStates(task.Id);
            var states = oldStates.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
            var ordered = UnitPlanner.Order(units, oldStates);
            foreach (var unit in ordered)
            {
                var previous = states.GetValueOrDefault(unit.Key);
                store.SaveState(task.Id, new(unit.Key, unit.Output, previous?.Fingerprint ?? "", ResultStatus.Running, unit.Mode, unit.Recursive));
            }
            progress?.Report(new(id, "列挙完了", 0, ordered.Count));
            await Parallel.ForEachAsync(ordered, new ParallelOptions { MaxDegreeOfParallelism = task.ParallelUnits, CancellationToken = ct }, async (unit, token) =>
            {
                var started = DateTimeOffset.Now;
                var unitLog = Path.Combine(work, $"unit-{Guid.NewGuid():N}.log");
                Snapshot? snapshot = null;
                UnitResult result;
                progress?.Report(new(id, unit.Key, Volatile.Read(ref completed), ordered.Count));
                try
                {
                    snapshot = scanner.Scan(unit, work, token);
                    var previous = states.GetValueOrDefault(unit.Key);
                    var skipped = (unit.Mode ?? task.Mode) == BackupMode.Zip && previous?.Fingerprint == snapshot.Fingerprint && File.Exists(Path.Combine(task.Destination, unit.Output));
                    if (!skipped) await backend.ExecuteAsync(task, unit, snapshot, unitLog, token);
                    else await File.WriteAllTextAsync(unitLog, "変更なし\n", token);
                    var status = skipped ? ResultStatus.Skipped : ResultStatus.Success;
                    store.SaveState(task.Id, new(unit.Key, unit.Output, snapshot.Fingerprint, ResultStatus.Success, unit.Mode, unit.Recursive));
                    result = new(unit.Key, status, started, DateTimeOffset.Now, unitLog, Files: snapshot.Files, Bytes: snapshot.Bytes);
                    Interlocked.Increment(ref completed);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    result = new(unit.Key, ResultStatus.Cancelled, started, DateTimeOffset.Now, unitLog, "中断されました。");
                    store.SaveUnit(id, result);
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Interlocked.Increment(ref failed);
                    File.AppendAllText(unitLog, ex + "\n"); Log($"ユニット失敗 {unit.Key}: {ex.Message}");
                    store.SaveState(task.Id, new(unit.Key, unit.Output, states.GetValueOrDefault(unit.Key)?.Fingerprint ?? "", ResultStatus.Failed, unit.Mode, unit.Recursive));
                    result = new(unit.Key, ResultStatus.Failed, started, DateTimeOffset.Now, unitLog, ex.Message);
                }
                finally { if (snapshot is not null) File.Delete(snapshot.ListPath); }
                store.SaveUnit(id, result);
                progress?.Report(new(id, unit.Key, Volatile.Read(ref completed) + Volatile.Read(ref failed), ordered.Count));
            });
            if (failed == 0)
            {
                Log("削除フェーズ開始");
                if (task.Mode == BackupMode.Copy)
                {
                    scanner.SynchronizeDeletions(task, work, Log, ct);
                    var keys = units.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var obsolete in oldStates.Where(x => !keys.Contains(x.Key))) store.RemoveState(task.Id, obsolete.Key);
                }
                else
                {
                    // Validate the full source before any mixed-mode deletions.
                    var sourceRoot = new BackupUnit("pre-delete", task.Source, "", true);
                    var check = scanner.Scan(sourceRoot, work, ct);
                    File.Delete(check.ListPath);
                    foreach (var copy in units.Where(u => u.Mode == BackupMode.Copy))
                    {
                        var deletionWork = Path.Combine(work, "delete-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(deletionWork);
                        scanner.SynchronizeDeletions(task with { Source = copy.Source, Destination = Path.Combine(task.Destination, copy.Output) }, deletionWork, Log, ct);
                    }
                    var currentKeys = units.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var obsolete in oldStates.Where(x => !currentKeys.Contains(x.Key)))
                    {
                        ct.ThrowIfCancellationRequested();
                        var target = Path.Combine(task.Destination, obsolete.Output);
                        SafePaths.CheckTarget(task.Destination, target);
                        var relative = obsolete.Key.EndsWith("|files", StringComparison.Ordinal) ? obsolete.Key[..^6] : obsolete.Key.EndsWith("|copy", StringComparison.Ordinal) ? obsolete.Key[..^5] : obsolete.Key;
                        // A live folder may have changed representation after editing the exception list.
                        // Only output owned by the previous layout is removed, after all replacements succeed.
                        PruneOldOutput(target, task.Destination, units, Log, ct, () =>
                        {
                            if (!HasSourceDirectory(task.Source, relative)) ConfirmAbsent(task.Source, relative);
                        });
                        for (var parent = Path.GetDirectoryName(target); parent is not null && !parent.Equals(task.Destination, StringComparison.OrdinalIgnoreCase); parent = Path.GetDirectoryName(parent))
                        {
                            var relativeParent = Path.GetRelativePath(task.Destination, parent);
                            if (units.Any(u => u.Mode == BackupMode.Copy && SafePaths.Contains(u.Output, relativeParent)) || !Directory.Exists(parent)) break;
                            SafePaths.CheckTarget(task.Destination, parent);
                            if (Directory.EnumerateFileSystemEntries(parent).Any()) break;
                            Directory.Delete(parent, false); Log($"空フォルダ削除: {parent}");
                        }
                        store.RemoveState(task.Id, obsolete.Key);
                    }
                }
            }
            else Log("エラーがあるため、この実行の削除フェーズ全体をスキップします。");
            run = run with { Status = failed == 0 ? ResultStatus.Success : completed == 0 ? ResultStatus.Failed : ResultStatus.PartialFailure };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { run = run with { Status = ResultStatus.Cancelled, Error = "ユーザー操作により中断しました。" }; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log(ex.ToString());
            run = run with { Status = completed > 0 ? ResultStatus.PartialFailure : ResultStatus.Failed, Error = ex.Message };
            failed++;
        }
        finally { destinationLock?.Dispose(); }
        run = run with { Finished = DateTimeOffset.Now, Completed = completed, Failed = failed };
        Log($"終了: {run.Status} 完了={completed} 失敗={failed}");
        store.FinishRun(run);
        File.WriteAllText(Path.Combine(work, "summary.json"), JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true }));
        try { store.Prune(task.Id, DateTimeOffset.Now.AddDays(-task.RetentionDays)); }
        catch (IOException ex) { Log($"ログ保持期間処理の警告: {ex.Message}"); }
        progress?.Report(new(id, run.Status.ToString(), completed + failed, completed + failed));
        return run;
    }

    private static void ConfirmAbsent(string root, string relative)
    {
        var current = root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var found = new DirectoryInfo(current).EnumerateFileSystemInfos().FirstOrDefault(x => x.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (found is null) return;
            if (found is not DirectoryInfo || (found.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("元データの不在を確認できません。");
            current = found.FullName;
        }
        throw new IOException("元フォルダが再出現したためZIP削除を中止しました。");
    }

    private static bool HasSourceDirectory(string root, string relative)
    {
        var current = root;
        SafePaths.NoReparseAncestors(root);
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var found = new DirectoryInfo(current).EnumerateFileSystemInfos().FirstOrDefault(x => x.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (found is null) return false;
            if (found is not DirectoryInfo || (found.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("元フォルダの状態が変更されています。削除を停止します。");
            current = found.FullName;
        }
        return true;
    }

    private static void PruneOldOutput(string path, string destination, IReadOnlyList<BackupUnit> current, Action<string> log, CancellationToken ct, Action verifySource)
    {
        ct.ThrowIfCancellationRequested();
        var relative = Path.GetRelativePath(destination, path);
        if (current.Any(u => u.Output.Equals(relative, StringComparison.OrdinalIgnoreCase) || u.Mode == BackupMode.Copy && SafePaths.Contains(u.Output, relative))) return;
        SafePaths.CheckTarget(destination, path);
        verifySource();
        // Do not recursively delete new ZIPs or copied folders nested in an old copy unit.
        if (Directory.Exists(path))
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path)) PruneOldOutput(child, destination, current, log, ct, verifySource);
            if (current.Any(u => SafePaths.Contains(relative, u.Output))) return;
            verifySource(); Directory.Delete(path, false);
        }
        else File.Delete(path);
        log($"旧出力削除: {path}");
    }

    private static void CheckDestinationTree(string root)
    {
        foreach (var item in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"保存先に再解析ポイントがあります。処理を中止します: {item.FullName}");
            if (item is DirectoryInfo) CheckDestinationTree(item.FullName);
        }
    }

    private static void ClaimDestination(BackupTask task)
    {
        var marker = Path.Combine(task.Destination, ".backup-tool.owner.json");
        var owner = JsonSerializer.Serialize(new { task.Id, task.Source, task.Mode, task.UnitDepth });
        if (File.Exists(marker))
        {
            SafePaths.CheckTarget(task.Destination, marker);
            if (File.ReadAllText(marker) != owner) throw new IOException("この保存先は別タスクが管理しています。");
        }
        else
        {
            if (Directory.EnumerateFileSystemEntries(task.Destination).Any(x => Path.GetFileName(x) != ".backup-tool.lock"))
                throw new IOException("初回の保存先には空の専用フォルダを指定してください。");
            File.WriteAllText(marker, owner);
        }
        foreach (var reserved in new[] { ".backup-tool.lock", ".backup-tool.owner.json" })
            if (Directory.EnumerateFileSystemEntries(task.Source).Any(x => Path.GetFileName(x).Equals(reserved, StringComparison.OrdinalIgnoreCase)))
                throw new IOException($"元フォルダに予約名があります: {reserved}");
    }

    private static async Task RetryAccess(BackupTask task, Action<string> log, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                SafePaths.NoReparseAncestors(task.Source); SafePaths.NoReparseAncestors(task.Destination);
                using (var enumerator = Directory.EnumerateFileSystemEntries(task.Source).GetEnumerator()) enumerator.MoveNext();
                Directory.CreateDirectory(task.Destination);
                var probe = Path.Combine(task.Destination, $".probe-{Guid.NewGuid():N}");
                using (File.Create(probe)) { }
                File.Delete(probe);
                return;
            }
            catch (IOException ex) when (attempt < task.NetworkRetries)
            {
                var seconds = Math.Min(120, task.NetworkWaitSeconds * Math.Pow(2, attempt));
                log($"アクセス再試行 {attempt + 1}/{task.NetworkRetries}: {seconds}秒後 ({ex.Message})");
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
            }
        }
    }
}
