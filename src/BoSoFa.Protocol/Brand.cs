namespace BoSoFa.Protocol;

/// <summary>The names of this build's brand; the constants are generated from <c>brands/&lt;Brand&gt;.props</c>.</summary>
public static partial class Brand
{
    /// <summary>The desktop app's executable name, without .exe.</summary>
    public const string Desktop = Id + "-desktop";

    /// <summary>The Windows service of the daemon.</summary>
    public const string WindowsService = Name + "Daemon";

    /// <summary>The launchd label of the daemon.</summary>
    public const string LaunchdLabel = "com." + Id + ".daemon";

    /// <summary>The systemd unit of the daemon.</summary>
    public const string SystemdUnit = Daemon + ".service";

    /// <summary>The per-user folder that holds the daemon's and the desktop app's data: <c>%LOCALAPPDATA%\Name</c>,
    /// <c>~/Library/Application Support/Name</c>, or <c>$XDG_DATA_HOME/id</c>.</summary>
    public static string DataFolder()
    {
        if (OperatingSystem.IsWindows()) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Application Support", Name);
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".local", "share") : xdg, Id);
    }
}
