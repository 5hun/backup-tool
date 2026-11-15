using System.Security.Cryptography;
using System.Text;
using Backup.Engine;
using Microsoft.Data.Sqlite;

namespace Backup.Infrastructure;

public sealed class SourceScanner : ISourceScanner
{
    public static IEnumerable<Entry> Enumerate(string root, bool recursive, CancellationToken ct)
    {
        SafePaths.NoReparseAncestors(root);
        return Visit(root);
        IEnumerable<Entry> Visit(string directory)
        {
            foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var relative = Path.GetRelativePath(root, item.FullName);
                if (relative.IndexOfAny(['\r', '\n']) >= 0) throw new IOException("改行を含むファイル名には対応していません。");
                if (item is DirectoryInfo)
                {
                    if (!recursive) continue;
                    yield return new(relative, true, 0, 0);
                    foreach (var child in Visit(item.FullName)) yield return child;
                }
                else if (item is FileInfo file) yield return new(relative, false, file.Length, file.LastWriteTimeUtc.Ticks);
            }
        }
    }

    public Snapshot Scan(BackupUnit unit, string workingDirectory, CancellationToken ct)
    {
        Directory.CreateDirectory(workingDirectory);
        var listPath = Path.Combine(workingDirectory, Guid.NewGuid().ToString("N") + ".list");
        var database = listPath + ".db";
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        db.Open();
        using (var setup = db.CreateCommand())
        {
            setup.CommandText = "PRAGMA temp_store=FILE; CREATE TABLE Entries(Path TEXT PRIMARY KEY, IsDirectory INTEGER, Size INTEGER, Ticks INTEGER, Empty INTEGER);";
            setup.ExecuteNonQuery();
        }
        using (var transaction = db.BeginTransaction())
        using (var insert = db.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO Entries VALUES ($path,$dir,$size,$ticks,$empty)";
            var path = insert.Parameters.Add("$path", SqliteType.Text);
            var dir = insert.Parameters.Add("$dir", SqliteType.Integer);
            var size = insert.Parameters.Add("$size", SqliteType.Integer);
            var ticks = insert.Parameters.Add("$ticks", SqliteType.Integer);
            var empty = insert.Parameters.Add("$empty", SqliteType.Integer);
            foreach (var entry in Enumerate(unit.Source, unit.Recursive, ct))
            {
                path.Value = entry.Path; dir.Value = entry.IsDirectory ? 1 : 0; size.Value = entry.Size; ticks.Value = entry.Ticks;
                empty.Value = entry.IsDirectory && !Directory.EnumerateFileSystemEntries(Path.Combine(unit.Source, entry.Path)).Any() ? 1 : 0;
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        using var list = new StreamWriter(listPath, false, new UTF8Encoding(false));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long files = 0, bytes = 0;
        using (var query = db.CreateCommand())
        {
            query.CommandText = "SELECT Path,IsDirectory,Size,Ticks,Empty FROM Entries ORDER BY Path";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var entry = new Entry(reader.GetString(0), reader.GetInt32(1) == 1, reader.GetInt64(2), reader.GetInt64(3));
                hash.AppendData(Encoding.UTF8.GetBytes($"{entry.Path}\0{entry.IsDirectory}\0{entry.Size}\0{entry.Ticks}\n"));
                if (!entry.IsDirectory) { files++; bytes += entry.Size; list.WriteLine(entry.Path); }
                // Passing nonempty directories would recursively include excluded links.
                else if (reader.GetInt32(4) == 1) list.WriteLine(entry.Path + Path.DirectorySeparatorChar);
            }
        }
        db.Close();
        File.Delete(database);
        return new(Convert.ToHexString(hash.GetHashAndReset()), files, bytes, listPath);
    }

    public void SynchronizeDeletions(BackupTask task, string workingDirectory, Action<string> log, CancellationToken ct)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(workingDirectory, "deletions.db"), Pooling = false }.ToString());
        db.Open();
        using (var setup = db.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE Source(Path TEXT PRIMARY KEY COLLATE NOCASE); CREATE TABLE Candidates(Path TEXT, IsDirectory INTEGER);";
            setup.ExecuteNonQuery();
        }
        using (var transaction = db.BeginTransaction())
        using (var insert = db.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO Source VALUES ($path)";
            var parameter = insert.Parameters.Add("$path", SqliteType.Text);
            foreach (var entry in Enumerate(task.Source, true, ct)) { parameter.Value = entry.Path; insert.ExecuteNonQuery(); }
            transaction.Commit();
        }
        // Finish BOTH inventories before deleting anything. Failed enumeration never means absence.
        using (var transaction = db.BeginTransaction())
        using (var candidate = db.CreateCommand())
        {
            candidate.Transaction = transaction;
            candidate.CommandText = "INSERT INTO Candidates SELECT $path,$dir WHERE NOT EXISTS (SELECT 1 FROM Source WHERE Path=$path)";
            var path = candidate.Parameters.Add("$path", SqliteType.Text);
            var dir = candidate.Parameters.Add("$dir", SqliteType.Integer);
            foreach (var entry in Enumerate(task.Destination, true, ct))
            {
                if (task.Mode == BackupMode.Copy && entry.Path is ".backup-tool.lock" or ".backup-tool.owner.json") continue;
                path.Value = entry.Path; dir.Value = entry.IsDirectory ? 1 : 0; candidate.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        using var query = db.CreateCommand();
        query.CommandText = "SELECT Path, IsDirectory FROM Candidates ORDER BY length(Path) DESC";
        using var reader = query.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            var relative = reader.GetString(0);
            ConfirmAbsent(task.Source, relative);
            var target = Path.Combine(task.Destination, relative);
            SafePaths.CheckTarget(task.Destination, target);
            if (reader.GetInt32(1) == 1) Directory.Delete(target, false);
            else File.Delete(target);
            log($"削除: {target}");
        }
    }

    public static void ConfirmAbsent(string root, string relative)
    {
        var current = root;
        SafePaths.NoReparseAncestors(root);
        foreach (var part in relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar))
        {
            // Enumeration throws on disconnect / denial; File.Exists would hide those errors.
            var found = new DirectoryInfo(current).EnumerateFileSystemInfos()
                .FirstOrDefault(x => x.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (found is null) return;
            if (found is not DirectoryInfo || (found.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"元データが再出現したか種類が変わりました。削除を停止します: {found.FullName}");
            current = found.FullName;
        }
        throw new IOException($"元フォルダが再出現しました。削除を停止します: {current}");
    }
}
