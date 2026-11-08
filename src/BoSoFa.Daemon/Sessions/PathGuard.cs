namespace BoSoFa.Daemon.Sessions;

/// <summary>Keeps file access under allowed roots (plan §5.6): rejects traversal, and symlinks that lead outside.</summary>
public static class PathGuard
{
    /// <summary>The full path when it lies under one of the roots and no link on the way leaves them, else null.</summary>
    public static string? Resolve(IReadOnlyCollection<string> roots, string path, string? baseDirectory = null)
    {
        var full = Path.GetFullPath(path, baseDirectory ?? roots.First());
        var root = roots.Select(Path.GetFullPath).FirstOrDefault(candidate => Inside(candidate, full));
        if (root == null) return null;
        var current = root;
        foreach (var part in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget == null) continue;
            var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (target == null || !roots.Select(Path.GetFullPath).Any(candidate => Inside(candidate, target))) return null;
        }
        return full;
    }

    static bool Inside(string root, string path) =>
        path == root || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
