using Backup.Engine;

namespace Backup.Infrastructure;

public sealed class BackupApplication
{
    public SqliteStore Store { get; }
    public string SevenZipPath { get; }
    public BackupApplication(string? dataDirectory = null, string? sevenZipPath = null)
    {
        Store = new(dataDirectory ?? Environment.GetEnvironmentVariable("BACKUP_TOOL_DATA") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BackupTool"));
        try
        {
            using var recoveryLock = new FileStream(Path.Combine(Store.DataDirectory, "execution.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            foreach (var task in Store.GetTasks()) Store.RecoverInterrupted(task.Id);
        }
        catch (IOException) { /* Another live runner owns this store; do not mark its run interrupted. */ }
        SevenZipPath = sevenZipPath ?? Environment.GetEnvironmentVariable("BACKUP_TOOL_7ZIP") ?? FindSevenZip();
    }
    private static string FindSevenZip()
    {
        foreach (var name in new[] { "7za.exe", "7z.exe" })
        {
            var packaged = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", name);
            if (File.Exists(packaged)) return packaged;
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var development = Path.Combine(dir.FullName, ".tools", "7zip", "x64", "7za.exe");
            if (File.Exists(development)) return development;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe");
    }
    public BackupRunner CreateRunner()
    {
        var scanner = new SourceScanner();
        return new(Store, scanner, new BackupBackend(SevenZipPath, scanner), Store.DataDirectory);
    }
}
