using BoSoFa.Daemon.Service;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

public class ServicePlanTest
{
    static ServiceTarget Target(ServiceOs os, bool system, string? user = null) =>
        new(os, system, "/data dir/bosofad", "/data dir", "/home/me", user, 501);

    [Fact]
    public void SystemdUnitsRunTheLauncherAndStayInTheirScope()
    {
        var user = ServicePlans.Install(Target(ServiceOs.Linux, false));
        var file = Assert.Single(user.Files);
        Assert.Equal($"/home/me/.config/systemd/user/{Brand.SystemdUnit}", file.Path);
        Assert.Contains("ExecStart=\"/data dir/bosofad\" launcher --data-dir \"/data dir\"\n", file.Content);
        Assert.Contains("WantedBy=default.target", file.Content);
        Assert.Contains(user.Commands, command => command is ["loginctl", "enable-linger"]);
        Assert.Contains(user.Commands, command => command is ["systemctl", "--user", "enable", "--now", Brand.SystemdUnit]);

        var system = ServicePlans.Install(Target(ServiceOs.Linux, true, "alice"));
        Assert.Equal($"/etc/systemd/system/{Brand.SystemdUnit}", system.Files[0].Path);
        Assert.Contains("User=alice\n", system.Files[0].Content);
        Assert.Equal(["chown", "-R", "alice", "/data dir"], system.Commands[0]);
        Assert.DoesNotContain(system.Commands, command => command.Contains("--user") || command[0] == "loginctl");
        Assert.Contains("%%", ServicePlans.Install(Target(ServiceOs.Linux, false) with { DataDirectory = "/a%b" }).Files[0].Content);
    }

    [Fact]
    public void LaunchdPlistsAreEscapedAndBootstrappedInTheRightDomain()
    {
        var agent = ServicePlans.Install(Target(ServiceOs.MacOs, false) with { DataDirectory = "/a&b" });
        Assert.Equal($"/home/me/Library/LaunchAgents/{Brand.LaunchdLabel}.plist", agent.Files[0].Path);
        Assert.Contains("<string>/a&amp;b</string>", agent.Files[0].Content);
        System.Xml.Linq.XDocument.Parse(agent.Files[0].Content, System.Xml.Linq.LoadOptions.None); // well-formed
        Assert.Equal(["launchctl", "bootstrap", "gui/501", agent.Files[0].Path], agent.Commands.Single());

        var daemon = ServicePlans.Install(Target(ServiceOs.MacOs, true, "alice"));
        Assert.Contains("<key>UserName</key><string>alice</string>", daemon.Files[0].Content);
        Assert.Equal(["chown", "-R", "alice", "/data dir"], daemon.Commands[0]); // it is installed by root, and runs as alice
        Assert.Equal("system", daemon.Commands[1][2]);
    }

    [Fact]
    public void WindowsUsesAServiceForTheMachineAndALogonTaskForAUser()
    {
        var service = ServicePlans.Install(Target(ServiceOs.Windows, true));
        Assert.Contains("--windows-service true", string.Join(' ', service.Commands[0]));
        Assert.Equal("sc.exe", service.Commands[0][0]);
        Assert.DoesNotContain("obj=", service.Commands[0]);
        Assert.Contains("obj=", ServicePlans.Install(Target(ServiceOs.Windows, true, @"DOM\alice")).Commands[0]);
        Assert.Equal("schtasks.exe", ServicePlans.Install(Target(ServiceOs.Windows, false)).Commands[0][0]);
    }

    [Theory]
    [InlineData(ServiceOs.Linux)]
    [InlineData(ServiceOs.MacOs)]
    [InlineData(ServiceOs.Windows)]
    public void UninstallStopsAndRemovesWhatInstallAdded(ServiceOs os)
    {
        var plan = ServicePlans.Uninstall(Target(os, false));
        var installed = ServicePlans.Install(Target(os, false));
        Assert.Equal(installed.Files.Select(file => file.Path), plan.RemovedFiles);
        Assert.NotEmpty(plan.Commands);
    }
}

public class LauncherTest
{
    sealed class FakeChild(int? exitCode = null) : ILauncherChild
    {
        readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Stopped { get; private set; }

        public FakeChild Start()
        {
            if (exitCode is { } code) _exit.TrySetResult(code);
            return this;
        }

        public void Exit(int code) => _exit.TrySetResult(code);
        public Task<int> WaitAsync() => _exit.Task;

        public Task StopAsync()
        {
            Stopped = true;
            _exit.TrySetResult(0);
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    static async Task<(VersionStore Store, List<string> Started)> RunUntilAsync(string directory, VersionState state, Func<string, int, FakeChild> children,
        Func<VersionStore, List<string>, bool> done)
    {
        var store = new VersionStore(directory);
        store.Save(state);
        var started = new List<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var launcher = new Launcher(store, executable =>
        {
            var version = Path.GetFileName(Path.GetDirectoryName(executable)!);
            lock (started) started.Add(version);
            return children(version, started.Count).Start();
        }, TextWriter.Null);
        var running = launcher.RunAsync(stop.Token);
        while (true)
        {
            bool finished;
            lock (started) finished = done(store, started);
            if (finished) break;
            Assert.False(stop.IsCancellationRequested, "Timed out.");
            await Task.Delay(10);
        }
        await stop.CancelAsync();
        await running;
        return (store, started);
    }

    static string Directory_() => Directory.CreateTempSubdirectory("bosofa-launcher-").FullName;

    [Fact]
    public async Task ACrashingVersionIsStartedAgain()
    {
        var (store, started) = await RunUntilAsync(Directory_(), new("2.0"), (_, count) => count == 1 ? new FakeChild(1) : new FakeChild(),
            (_, list) => list.Count == 2);
        Assert.Equal(["2.0", "2.0"], started); // there is nothing to go back to
        Assert.Equal("2.0", store.Load().Current);
    }

    [Fact]
    public async Task ExitCodeSeventyFiveSwitchesToTheNewCurrentVersionAndDropsTheOldOne()
    {
        var directory = Directory_();
        var store = new VersionStore(directory);
        foreach (var version in new[] { "0.9", "1.0" }) Directory.CreateDirectory(Path.Combine(store.Root, version));
        var (_, started) = await RunUntilAsync(directory, new("1.0"), (version, count) =>
        {
            if (count == 1)
            {
                // What the updater does while 1.0 runs, before it exits.
                Directory.CreateDirectory(Path.Combine(store.Root, "2.0"));
                new VersionStore(directory).Save(new("2.0"));
            }
            return version == "1.0" ? new FakeChild(Launcher.SwitchVersionExitCode) : new FakeChild();
        }, (_, list) => list.Count == 2);
        Assert.Equal(["1.0", "2.0"], started);
        Assert.Equal(["2.0"], Directory.GetDirectories(store.Root).Select(Path.GetFileName));
    }

    [Fact]
    public void AnOlderStateFileStillNamesTheCurrentVersion()
    {
        var directory = Directory_();
        var store = new VersionStore(directory);
        Directory.CreateDirectory(store.Root);
        File.WriteAllText(Path.Combine(store.Root, "state.json"), """{"current":"2.0","previous":"1.0","trialStartedAt":null,"failed":["1.5"]}""");
        Assert.Equal("2.0", store.Load().Current);
    }
}
