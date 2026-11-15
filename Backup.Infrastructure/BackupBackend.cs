using System.IO.Compression;
using Backup.Engine;

namespace Backup.Infrastructure;

public sealed class BackupBackend(string sevenZipPath, ISourceScanner scanner) : IBackupBackend
{
    public async Task ExecuteAsync(BackupTask task, BackupUnit unit, Snapshot snapshot, string logPath, CancellationToken ct)
    {
        var target = Path.Combine(task.Destination, unit.Output);
        SafePaths.CheckTarget(task.Destination, target);
        SafePaths.NoReparseAncestors(unit.Source);
        if ((unit.Mode ?? task.Mode) == BackupMode.Copy)
        {
            Directory.CreateDirectory(target);
            List<string> arguments = [unit.Source, target, "*", "/COPY:DAT", "/DCOPY:DAT", $"/MT:{task.CopyThreads}",
                "/ZB", $"/R:{task.FileRetries}", $"/W:{task.FileWaitSeconds}", "/XJ", "/NP", "/FP", "/BYTES",
                "/NFL", "/NDL", "/UNICODE", $"/UNILOG:{logPath}.robocopy.log"];
            if (unit.Recursive) arguments.Add("/E");
            var exitCode = await ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "robocopy.exe"), arguments, unit.Source, logPath, ct, System.Text.Encoding.Unicode);
            if (exitCode == 16)
            {
                // /ZB can fail at startup when the account lacks SeBackupPrivilege/SeRestorePrivilege.
                await File.AppendAllTextAsync(logPath, "Robocopy /ZB が終了コード16で停止しました。通常権限の /Z で再試行します。\n", ct);
                arguments[arguments.IndexOf("/ZB")] = "/Z";
                var logIndex = arguments.FindIndex(a => a.StartsWith("/UNILOG:", StringComparison.Ordinal));
                arguments[logIndex] = $"/UNILOG+:{logPath}.robocopy.log";
                exitCode = await ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "robocopy.exe"), arguments, unit.Source, logPath, ct, System.Text.Encoding.Unicode);
            }
            if (exitCode >= 8) throw new IOException($"Robocopyが失敗しました (終了コード {exitCode})。詳細: {logPath}.robocopy.log");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp.zip";
        try
        {
            var hasEntries = new FileInfo(snapshot.ListPath).Length != 0;
            if (!hasEntries)
            {
                using var empty = ZipFile.Open(temporary, ZipArchiveMode.Create);
            }
            else
            {
                // New archive via update action set: reuse unchanged compressed entries; omit removed entries.
                List<string> arguments = ["u", "-tzip", "-mm=Deflate", $"-mx={task.CompressionLevel}", "-scsUTF-8", "-sccUTF-8", "-spd", "-r-", "-y", "-bd"];
                if (File.Exists(target))
                {
                    arguments.Add("-u-"); arguments.Add("-up0q0r2x2y2z1w2!" + temporary); arguments.Add(target);
                }
                else arguments.Add(temporary);
                arguments.Add("@" + snapshot.ListPath);
                var code = await ProcessRunner.RunAsync(sevenZipPath, arguments, unit.Source, logPath, ct);
                if (code != 0) throw new IOException($"7-Zip作成失敗 (終了コード {code})。旧ZIPを保持します。");
            }
            var test = await ProcessRunner.RunAsync(sevenZipPath, ["t", "-tzip", "-sccUTF-8", "-bd", "--", temporary], unit.Source, logPath, ct);
            if (test != 0) throw new IOException($"ZIP検証失敗 (終了コード {test})。");
            var after = scanner.Scan(unit, Path.GetDirectoryName(snapshot.ListPath)!, ct);
            try
            {
                if (snapshot.Fingerprint != after.Fingerprint) throw new IOException("ZIP作成中に元ファイルが変更されました。旧ZIPを保持します。");
            }
            finally { File.Delete(after.ListPath); }
            ct.ThrowIfCancellationRequested();
            SafePaths.CheckTarget(task.Destination, target);
            // Same-directory rename/replace; never delete the old archive as a fallback.
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
            await File.AppendAllTextAsync(logPath, $"{DateTimeOffset.Now:O} ZIP置換: {target}\n", CancellationToken.None);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
