using System.Security;
using System.Text;

namespace BoSoFa.Daemon.Service;

public enum ServiceOs
{
    Linux,
    MacOs,
    Windows
}

/// <summary>What to install. <c>User</c> runs in the signed-in user's session; a system service starts with the machine.</summary>
public sealed record ServiceTarget(ServiceOs Os, bool System, string Launcher, string DataDirectory, string Home, string? ServiceUser, int UserId);

public sealed record ServiceFile(string Path, string Content);

/// <summary>The steps of an install or uninstall, as data: <c>bosofad install --dry-run</c> prints them and tests read them.</summary>
public sealed record ServicePlan(ServiceFile[] Files, string[] RemovedFiles, string[][] Commands);

/// <summary>The service definitions of plan §5.7. They point at the stable launcher, so an update never rewrites them.</summary>
public static class ServicePlans
{
    public const string SystemdUnit = "bosofad.service";
    public const string LaunchdLabel = "com.bosofa.daemon";
    public const string WindowsName = "BoSoFaDaemon";

    public static ServicePlan Install(ServiceTarget target) => target.Os switch
    {
        ServiceOs.Linux => InstallSystemd(target),
        ServiceOs.MacOs => InstallLaunchd(target),
        _ => InstallWindows(target)
    };

    public static ServicePlan Uninstall(ServiceTarget target) => target.Os switch
    {
        ServiceOs.Linux => new([], [SystemdPath(target)],
            [[.. Systemctl(target), "disable", "--now", SystemdUnit], [.. Systemctl(target), "daemon-reload"]]),
        ServiceOs.MacOs => new([], [PlistPath(target)], [["launchctl", "bootout", $"{LaunchdDomain(target)}/{LaunchdLabel}"]]),
        _ => target.System
            ? new([], [], [["sc.exe", "stop", WindowsName], ["sc.exe", "delete", WindowsName]])
            : new([], [], [["schtasks.exe", "/End", "/TN", WindowsName], ["schtasks.exe", "/Delete", "/TN", WindowsName, "/F"]])
    };

    static string SystemdPath(ServiceTarget target) => target.System
        ? "/etc/systemd/system/" + SystemdUnit
        : $"{target.Home}/.config/systemd/user/{SystemdUnit}";

    static string[] Systemctl(ServiceTarget target) => target.System ? ["systemctl"] : ["systemctl", "--user"];

    /// <summary>Quoted for an ExecStart line, where a percent sign starts a specifier.</summary>
    static string SystemdQuote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("%", "%%") + "\"";

    static ServicePlan InstallSystemd(ServiceTarget target)
    {
        var unit = new StringBuilder();
        unit.Append("[Unit]\nDescription=BoSoFa daemon\nAfter=network-online.target\nWants=network-online.target\n\n[Service]\n");
        unit.Append($"ExecStart={SystemdQuote(target.Launcher)} launcher --data-dir {SystemdQuote(target.DataDirectory)}\n");
        if (target.System && target.ServiceUser != null) unit.Append($"User={target.ServiceUser}\n");
        unit.Append("Restart=always\nRestartSec=5\nKillMode=mixed\nTimeoutStopSec=30\n\n[Install]\n");
        unit.Append($"WantedBy={(target.System ? "multi-user.target" : "default.target")}\n");
        List<string[]> commands = [[.. Systemctl(target), "daemon-reload"], [.. Systemctl(target), "enable", "--now", SystemdUnit]];
        // Without lingering a user service stops at the last logout.
        if (!target.System) commands.Insert(0, ["loginctl", "enable-linger"]);
        return new([new(SystemdPath(target), unit.ToString())], [], commands.ToArray());
    }

    static string PlistPath(ServiceTarget target) => target.System
        ? $"/Library/LaunchDaemons/{LaunchdLabel}.plist"
        : $"{target.Home}/Library/LaunchAgents/{LaunchdLabel}.plist";

    static string LaunchdDomain(ServiceTarget target) => target.System ? "system" : $"gui/{target.UserId}";

    static ServicePlan InstallLaunchd(ServiceTarget target)
    {
        static string Xml(string value) => SecurityElement.Escape(value);
        var log = Xml($"{target.DataDirectory}/logs/bosofad.log");
        var plist = new StringBuilder();
        plist.Append("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>

            """);
        plist.Append($"  <key>Label</key><string>{LaunchdLabel}</string>\n  <key>ProgramArguments</key>\n  <array>\n");
        foreach (var argument in new[] { target.Launcher, "launcher", "--data-dir", target.DataDirectory })
            plist.Append($"    <string>{Xml(argument)}</string>\n");
        plist.Append("  </array>\n  <key>RunAtLoad</key><true/>\n  <key>KeepAlive</key><true/>\n");
        // launchd starts with a minimal PATH, and the harness CLIs are usually in Homebrew's folders.
        plist.Append("  <key>EnvironmentVariables</key>\n  <dict>\n    <key>PATH</key><string>/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin</string>\n  </dict>\n");
        if (target.System && target.ServiceUser != null) plist.Append($"  <key>UserName</key><string>{Xml(target.ServiceUser)}</string>\n");
        plist.Append($"  <key>StandardOutPath</key><string>{log}</string>\n  <key>StandardErrorPath</key><string>{log}</string>\n</dict>\n</plist>\n");
        return new([new(PlistPath(target), plist.ToString())], [], [["launchctl", "bootstrap", LaunchdDomain(target), PlistPath(target)]]);
    }

    static ServicePlan InstallWindows(ServiceTarget target)
    {
        var command = $"\"{target.Launcher}\" launcher --data-dir \"{target.DataDirectory}\"";
        if (!target.System)
            return new([], [],
            [
                ["schtasks.exe", "/Create", "/TN", WindowsName, "/TR", command, "/SC", "ONLOGON", "/RL", "LIMITED", "/F"],
                ["schtasks.exe", "/Run", "/TN", WindowsName]
            ]);
        List<string> create = ["sc.exe", "create", WindowsName, "binPath=", command + " --windows-service true", "start=", "auto", "DisplayName=", "BoSoFa daemon"];
        if (target.ServiceUser != null) create.AddRange(["obj=", target.ServiceUser]);
        return new([], [],
        [
            create.ToArray(),
            ["sc.exe", "failure", WindowsName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000"],
            ["sc.exe", "start", WindowsName]
        ]);
    }
}
