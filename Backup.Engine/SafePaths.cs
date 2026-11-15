namespace Backup.Engine;

public static class SafePaths
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("通常の絶対パスまたはUNCパスを指定してください。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.IndexOf(':', 2) >= 0 || full.IndexOfAny(['*', '?', '"', '\r', '\n']) >= 0)
            throw new ArgumentException("パスに使用できない文字が含まれています。");
        return full;
    }

    public static bool Contains(string parent, string child) =>
        string.Equals(parent, child, StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static bool Overlaps(string left, string right) => Contains(left, right) || Contains(right, left);

    public static void NoReparseAncestors(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"再解析ポイントを含むパスは使用できません: {current.FullName}");
    }

    public static void CheckTarget(string root, string path)
    {
        if (!Contains(root, Path.GetFullPath(path))) throw new IOException("保存先の範囲外です。");
        NoReparseAncestors(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"再解析ポイントは更新・削除しません: {path}");
    }
}
