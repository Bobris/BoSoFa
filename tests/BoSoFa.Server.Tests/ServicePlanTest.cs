using BoSoFa.Daemon.Service;

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
        Assert.Equal("/home/me/.config/systemd/user/bosofad.service", file.Path);
        Assert.Contains("ExecStart=\"/data dir/bosofad\" launcher --data-dir \"/data dir\"\n", file.Content);
        Assert.Contains("WantedBy=default.target", file.Content);
        Assert.Contains(user.Commands, command => command is ["loginctl", "enable-linger"]);
        Assert.Contains(user.Commands, command => command is ["systemctl", "--user", "enable", "--now", "bosofad.service"]);

        var system = ServicePlans.Install(Target(ServiceOs.Linux, true, "alice"));
        Assert.Equal("/etc/systemd/system/bosofad.service", system.Files[0].Path);
        Assert.Contains("User=alice\n", system.Files[0].Content);
        Assert.DoesNotContain(system.Commands, command => command.Contains("--user") || command[0] == "loginctl");
        Assert.Contains("%%", ServicePlans.Install(Target(ServiceOs.Linux, false) with { DataDirectory = "/a%b" }).Files[0].Content);
    }

    [Fact]
    public void LaunchdPlistsAreEscapedAndBootstrappedInTheRightDomain()
    {
        var agent = ServicePlans.Install(Target(ServiceOs.MacOs, false) with { DataDirectory = "/a&b" });
        Assert.Equal("/home/me/Library/LaunchAgents/com.bosofa.daemon.plist", agent.Files[0].Path);
        Assert.Contains("<string>/a&amp;b</string>", agent.Files[0].Content);
        System.Xml.Linq.XDocument.Parse(agent.Files[0].Content, System.Xml.Linq.LoadOptions.None); // well-formed
        Assert.Equal(["launchctl", "bootstrap", "gui/501", agent.Files[0].Path], agent.Commands.Single());

        var daemon = ServicePlans.Install(Target(ServiceOs.MacOs, true, "alice"));
        Assert.Contains("<key>UserName</key><string>alice</string>", daemon.Files[0].Content);
        Assert.Equal("system", daemon.Commands.Single()[2]);
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
        Func<VersionStore, List<string>, bool> done, TimeSpan? trial = null)
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
        }, TextWriter.Null, trial, TimeSpan.FromMilliseconds(10));
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
    public async Task ANewVersionThatDiesBeforeReachingTheServerIsTakenBack()
    {
        var (store, started) = await RunUntilAsync(Directory_(), new("2.0", "1.0", DateTime.UtcNow, []),
            (version, _) => version == "2.0" ? new FakeChild(1) : new FakeChild(), (_, list) => list.Contains("1.0"));
        Assert.Equal(["2.0", "1.0"], started);
        var state = store.Load();
        Assert.Equal(("1.0", (string?)null, (DateTime?)null), (state.Current, state.Previous, state.TrialStartedAt));
        Assert.Equal(["2.0"], state.Failed);
    }

    [Fact]
    public async Task ANewVersionThatHangsIsTakenBackAfterTheLimit()
    {
        FakeChild? hanging = null;
        var (store, _) = await RunUntilAsync(Directory_(), new("2.0", "1.0", DateTime.UtcNow, []), (version, _) =>
        {
            var child = new FakeChild();
            if (version == "2.0") hanging = child;
            return child;
        }, (_, list) => list.Contains("1.0"), TimeSpan.FromMilliseconds(100));
        Assert.True(hanging!.Stopped);
        Assert.Equal("1.0", store.Load().Current);
    }

    [Fact]
    public async Task AConfirmedVersionStaysEvenIfItCrashesLater()
    {
        var directory = Directory_();
        FakeChild? first = null;
        var (store, started) = await RunUntilAsync(directory, new("2.0", "1.0", DateTime.UtcNow, []), (_, count) =>
        {
            var child = new FakeChild();
            if (count == 1)
            {
                first = child;
                new VersionStore(directory).Confirm("2.0");
            }
            return child;
        }, (versions, list) =>
        {
            if (versions.Load().TrialStartedAt == null) first?.Exit(1);
            return list.Count == 2;
        });
        Assert.Equal(["2.0", "2.0"], started);
        Assert.Equal("2.0", store.Load().Current);
        Assert.Empty(store.Load().Failed);
    }

    [Fact]
    public async Task ExitCodeSeventyFiveSwitchesToTheNewCurrentVersion()
    {
        var directory = Directory_();
        var (_, started) = await RunUntilAsync(directory, new("1.0", null, null, []), (version, count) =>
        {
            if (count == 1) new VersionStore(directory).Save(new("2.0", "1.0", DateTime.UtcNow, [])); // what the updater does before it exits
            return version == "1.0" ? new FakeChild(Launcher.SwitchVersionExitCode) : new FakeChild();
        }, (_, list) => list.Count == 2);
        Assert.Equal(["1.0", "2.0"], started);
    }
}
