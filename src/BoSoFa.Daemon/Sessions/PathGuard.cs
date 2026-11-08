namespace BoSoFa.Daemon.Sessions;

/// <summary>Keeps file access under allowed roots (plan §5.6): rejects traversal, and symlinks that lead outside.</summary>
public static class PathGuard
{
    const int MaximumLinks = 32;

    /// <summary>The full path when it lies under one of the roots, also after following every link on the way, else
    /// null. A relative path starts at <paramref name="baseDirectory"/>. The path need not exist yet.</summary>
    public static string? Resolve(IReadOnlyCollection<string> roots, string path, string? baseDirectory = null)
    {
        if (roots.Count == 0 || path.Contains('\0')) return null;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, baseDirectory ?? roots.First()));
        var links = 0;
        if (!roots.Any(root => Inside(Normalize(root), full)) || Real(full, ref links) is not { } real) return null;
        foreach (var root in roots)
        {
            links = 0;
            if (Real(Normalize(root), ref links) is { } realRoot && Inside(realRoot, real)) return full;
        }
        return null;
    }

    static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>The path with every link resolved, link targets included; null when links form a cycle.</summary>
    static string? Real(string path, ref int links)
    {
        var start = Path.GetPathRoot(path)!;
        var current = start;
        foreach (var part in path[start.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not { } target) continue;
            if (++links > MaximumLinks) return null;
            var next = Real(Path.TrimEndingDirectorySeparator(Path.GetFullPath(target, Path.GetDirectoryName(current)!)), ref links);
            if (next == null) return null;
            current = next;
        }
        return current;
    }

    static bool Inside(string root, string path) =>
        path == root || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, PathComparison);

    static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
