namespace BoSoFa.Daemon.Harnesses;

/// <summary>The environment of a harness child: empty except for what a CLI needs to run, so nothing of a parent agent
/// session leaks in and no API key is billed by accident (spike ④, pitfall 2).</summary>
public static class HarnessEnvironment
{
    static readonly string[] Common =
    [
        "HOME", "USER", "LOGNAME", "SHELL", "TMPDIR", "PATH", "LANG", "LC_ALL", "TERM",
        "USERPROFILE", "APPDATA", "LOCALAPPDATA", "SystemRoot", "SystemDrive", "ComSpec", "PATHEXT", "TEMP", "TMP",
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY"
    ];

    /// <summary>The common variables that are set in the daemon, plus the named extra ones.</summary>
    public static Dictionary<string, string> Clean(params string[] extra)
    {
        var environment = new Dictionary<string, string>();
        foreach (var name in Common.Concat(extra))
            if (Environment.GetEnvironmentVariable(name) is { } value)
                environment[name] = value;
        return environment;
    }

    /// <summary>The configured executable, else the first of <paramref name="names"/> on the PATH or in the extra
    /// folders (a daemon started by a service manager has a short PATH).</summary>
    public static string? FindExecutable(string? configured, string[] names, params string[] extraDirectories)
    {
        if (configured != null) return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Concat(extraDirectories)
            .Where(directory => directory.Length > 0)
            .SelectMany(directory => names.Select(name => Path.Combine(directory, name)))
            .FirstOrDefault(File.Exists);
    }
}
