using System.Diagnostics;
using BoSoFa.Daemon.Harnesses;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon.Service;

/// <summary><c>bosofad install</c>, <c>uninstall</c>, <c>launcher</c> and <c>doctor</c>.</summary>
public static class ServiceCommands
{
    static ServiceOs CurrentOs => OperatingSystem.IsWindows() ? ServiceOs.Windows : OperatingSystem.IsMacOS() ? ServiceOs.MacOs : ServiceOs.Linux;

    static string Launcher(string dataDirectory) => Path.Combine(dataDirectory, VersionStore.ExecutableName);

    static int CurrentUserId() => OperatingSystem.IsWindows() ? 0 : int.Parse(Run(["id", "-u"], TextWriter.Null).Output.Trim());

    static ServiceTarget Target(DaemonStore store, Dictionary<string, string> options) => new(CurrentOs,
        options.GetValueOrDefault("scope") == "system", Launcher(Path.GetFullPath(store.Directory)), Path.GetFullPath(store.Directory),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), options.GetValueOrDefault("service-user"), CurrentUserId());

    static (int Code, string Output) Run(string[] command, TextWriter output)
    {
        var info = new ProcessStartInfo(command[0]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in command.Skip(1)) info.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(info)!;
            var error = process.StandardError.ReadToEndAsync();
            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            var all = text + error.GetAwaiter().GetResult();
            if (all.Length > 0) output.WriteLine(all.TrimEnd());
            return (process.ExitCode, all);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return (127, exception.Message);
        }
    }

    static bool Apply(ServicePlan plan, TextWriter output, TextWriter error, bool stopOnFailure)
    {
        foreach (var file in plan.Files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
            File.WriteAllText(file.Path, file.Content);
            output.WriteLine($"Wrote {file.Path}");
        }
        foreach (var file in plan.RemovedFiles)
            if (File.Exists(file))
            {
                File.Delete(file);
                output.WriteLine($"Removed {file}");
            }
        foreach (var command in plan.Commands)
        {
            output.WriteLine("> " + string.Join(' ', command));
            var (code, text) = Run(command, output);
            if (code == 0) continue;
            error.WriteLine($"That failed with exit code {code}.{(text.Length == 0 ? "" : " " + text.Trim())}");
            if (stopOnFailure) return false;
        }
        return true;
    }

    static void Print(ServicePlan plan, TextWriter output)
    {
        foreach (var file in plan.Files) output.WriteLine($"Would write {file.Path}:\n{file.Content}");
        foreach (var file in plan.RemovedFiles) output.WriteLine($"Would remove {file}");
        foreach (var command in plan.Commands) output.WriteLine("Would run: " + string.Join(' ', command));
    }

    public static int Install(DaemonStore store, Dictionary<string, string> options, TextWriter output, TextWriter error)
    {
        var target = Target(store, options);
        var plan = ServicePlans.Install(target);
        if (options.ContainsKey("dry-run"))
        {
            Print(plan, output);
            return 0;
        }
        var executable = Environment.ProcessPath!;
        var versions = new VersionStore(target.DataDirectory);
        Directory.CreateDirectory(Path.Combine(target.DataDirectory, "logs"));
        versions.Add(Cli.Version, executable);
        var state = versions.Load();
        versions.Save(state with { Current = Cli.Version, Previous = state.Current != Cli.Version ? state.Current : state.Previous, TrialStartedAt = null });
        // Replaced by moving: a launcher that is running keeps its old file.
        var launcher = target.Launcher;
        var temporary = launcher + ".new";
        File.Copy(executable, temporary, true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
        File.Move(temporary, launcher, true);
        if (!Apply(plan, output, error, stopOnFailure: true)) return 1;
        output.WriteLine(store.LoadState() == null
            ? $"Installed. It waits to be enrolled: {Brand.Daemon} enroll --server <url> --data-dir \"{target.DataDirectory}\""
            : "Installed and started.");
        return 0;
    }

    public static int Uninstall(DaemonStore store, Dictionary<string, string> options, TextWriter output, TextWriter error)
    {
        var plan = ServicePlans.Uninstall(Target(store, options));
        if (options.ContainsKey("dry-run"))
        {
            Print(plan, output);
            return 0;
        }
        Apply(plan, output, error, stopOnFailure: false); // whatever is left of it should still go
        output.WriteLine($"Removed the service. The machine's identity stays in {store.Directory}.");
        return 0;
    }

    public static async Task<int> LauncherAsync(DaemonStore store, Dictionary<string, string> options, TextWriter output, CancellationToken cancellation)
    {
        string[] extra = options.TryGetValue("transport", out var transport) ? ["--transport", transport] : [];
        var directory = Path.GetFullPath(store.Directory);
        var launcher = new Launcher(new VersionStore(directory), executable => new ProcessChild(executable, directory, extra), output);
        if (OperatingSystem.IsWindows() && options.ContainsKey("windows-service")) return WindowsServiceHost.Run(launcher.RunAsync);
        return await launcher.RunAsync(cancellation);
    }

    public static int Doctor(DaemonStore store, Dictionary<string, string> options, TextWriter output)
    {
        void Line(bool ok, string text) => output.WriteLine($"{(ok ? "ok  " : "warn")}  {text}");
        Line(true, $"{Brand.Daemon} {Cli.Version}, data in {store.Directory}, running as {Environment.UserName}");
        var enrolled = store.LoadState();
        Line(enrolled != null, enrolled != null ? $"enrolled as machine {enrolled.MachineId} on {enrolled.Server}" : $"not enrolled: {Brand.Daemon} enroll --server <url>");
        var versions = new VersionStore(Path.GetFullPath(store.Directory)).Load();
        Line(versions.Current != null, versions.Current != null ? $"launcher runs version {versions.Current}" : $"not installed as a service: {Brand.Daemon} install");
        if (versions.Failed.Length > 0) Line(false, $"versions rolled back: {string.Join(", ", versions.Failed)}");
        Line(HarnessEnvironment.FindExecutable(null, ["git", "git.exe"]) != null, "git is on the PATH");
        foreach (var (name, executables) in new[] { ("Claude Code", new[] { "claude", "claude.exe", "claude.cmd" }), ("Codex", new[] { "codex", "codex.exe", "codex.cmd" }) })
            Line(HarnessEnvironment.FindExecutable(null, executables) != null, $"{name} on the PATH");
        output.WriteLine("Harness and git sign-ins live in the profile of the user the daemon runs as, so a service must run as that user.");
        return 0;
    }
}
