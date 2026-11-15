namespace Backup.Engine;

public static class UnitPlanner
{
    // Shallow direct files have their own unit; no source file is omitted.
    public static IReadOnlyList<BackupUnit> Plan(BackupTask task)
    {
        task = task.Validate();
        List<BackupUnit> units = [];
        Visit(task.Source, "", 0);
        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in units)
            if (!outputs.Add(unit.Output))
                throw new IOException($"ZIP出力名が衝突します。元フォルダの _files を改名してください: {unit.Output}");
        var zipOutputs = units.Where(u => u.Mode == BackupMode.Zip).Select(u => u.Output).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var copyOutputs = units.Where(u => u.Mode == BackupMode.Copy && u.Recursive).Select(u => u.Output).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in units)
            for (var parent = Path.GetDirectoryName(unit.Output); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                if (zipOutputs.Contains(parent) || unit.Mode == BackupMode.Zip && copyOutputs.Contains(parent))
                    throw new IOException($"ZIPと非圧縮フォルダの出力先が衝突します: {parent}, {unit.Output}");
        return units;

        void Visit(string directory, string relative, int depth)
        {
            if (task.Mode == BackupMode.Zip && task.UncompressedFolders.Contains(relative, StringComparer.OrdinalIgnoreCase))
            {
                units.Add(new(relative + "|copy", directory, relative, true, BackupMode.Copy));
                return;
            }
            var hasException = task.UncompressedFolders.Any(f => relative.Length == 0 || SafePaths.Contains(relative, f));
            if (depth >= task.UnitDepth && (task.Mode == BackupMode.Copy || !hasException))
            {
                units.Add(new(relative, directory, task.Mode == BackupMode.Zip ? relative + ".zip" : relative, true, task.Mode));
                return;
            }
            units.Add(new(relative + "|files", directory,
                task.Mode == BackupMode.Zip ? Path.Combine(relative, "_files.zip") : relative, false, task.Mode));
            foreach (var child in new DirectoryInfo(directory).EnumerateDirectories())
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                Visit(child.FullName, Path.Combine(relative, child.Name), depth + 1);
            }
        }
    }

    public static List<BackupUnit> Order(IReadOnlyList<BackupUnit> units, IReadOnlyList<UnitState> states)
    {
        var pending = states.Where(x => x.Status is not (ResultStatus.Success or ResultStatus.Skipped))
            .Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = units.ToArray();
        Random.Shared.Shuffle(result);
        return result.OrderByDescending(x => pending.Contains(x.Key)).ToList();
    }
}
