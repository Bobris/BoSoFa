using System.Diagnostics;
using System.Text.Json;
using BoSoFa.Desktop;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

public class DesktopUpdaterTest
{
    static string Rid => DesktopUpdater.CurrentRid;
    static bool Zip => OperatingSystem.IsWindows();

    /// <summary>An installed app of the given version, as the install script leaves it.</summary>
    static InstalledApp Install(string folder, string version)
    {
        var root = Path.Combine(folder, "BoSoFa");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "release.json"), $$"""{"version":"{{version}}","rid":"{{Rid}}"}""");
        File.WriteAllText(Path.Combine(root, InstalledApp.ExecutableName), version);
        return InstalledApp.FromRoot(root)!;
    }

    sealed class FakeApp(bool exits) : IStartedApp
    {
        public bool HasExited => exits;
        public bool Killed { get; private set; }
        public void Kill() => Killed = true;
        public void Dispose() { }
    }

    [Fact]
    public void ABundleIsFoundFromTheFolderOfItsExecutable()
    {
        var folder = Directory.CreateTempSubdirectory("bosofa-bundle-").FullName;
        var code = Path.Combine(folder, "BoSoFa.app", "Contents", "MacOS");
        Directory.CreateDirectory(code);
        File.WriteAllText(Path.Combine(code, "release.json"), """{"version":"3.0","rid":"osx-arm64"}""");
        var app = InstalledApp.FromBaseDirectory(code + Path.DirectorySeparatorChar)!;
        Assert.Equal((Path.Combine(folder, "BoSoFa.app"), Path.Combine(code, InstalledApp.ExecutableName)), (app.Root, app.Executable));
        Assert.Null(InstalledApp.FromBaseDirectory(folder)); // a build without release.json does not update itself
        Directory.Delete(folder, true);
    }

    [Fact]
    public async Task ANewerBuildOfTheServerIsStagedAndSwappedInWhenItStarts()
    {
        var folder = Directory.CreateTempSubdirectory("bosofa-desktop-update-").FullName;
        var data = Path.Combine(folder, "data");
        var installed = Install(Path.Combine(folder, "apps"), "1.0");
        using var http = new HttpClient();
        await using (var same = await BundleFixture.StartAsync("1.0", (Releases.Desktop, Rid, BundleFixture.AppArchive("1.0", Rid, "BoSoFa", Zip))))
            Assert.False((await new DesktopUpdater(installed, data, () => same.BaseAddress.ToString(), http).CheckAsync(default)).Ready); // not newer

        await using var server = await BundleFixture.StartAsync("2.1", (Releases.Desktop, Rid, BundleFixture.AppArchive("2.1", Rid, "BoSoFa", Zip)));
        var started = new List<ProcessStartInfo>();
        var quit = 0;
        var updater = new DesktopUpdater(installed, data, () => server.BaseAddress.ToString(), http, info =>
        {
            started.Add(info);
            return null;
        }) { Quit = () => Interlocked.Increment(ref quit) };

        // A file that does not match what the server says it is, is not taken.
        var archive = BundleFixture.FileOf(server, Releases.Desktop, Rid);
        var good = File.ReadAllBytes(archive);
        File.WriteAllBytes(archive, BundleFixture.AppArchive("6.6", Rid, "BoSoFa", Zip));
        var refused = await updater.CheckAsync(default);
        Assert.False(refused.Ready);
        Assert.Contains("does not match", refused.Problem);
        File.WriteAllBytes(archive, good);

        var state = await updater.CheckAsync(default);
        Assert.Equal(new UpdateState("1.0", true, "2.1", true, null), state);

        Assert.Null(updater.Install());
        var helper = Assert.Single(started);
        Assert.Equal([UpdateHelper.Argument, Path.Combine(data, "updates", "plan.json")], helper.ArgumentList);
        var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(helper.ArgumentList[1]), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(("2.1", installed.Root, Environment.ProcessId), (plan.Version, plan.Target, plan.ProcessId));
        await WaitUntilAsync(() => Volatile.Read(ref quit) == 1);

        // The new version shows its page in time.
        var launches = new List<(string Executable, string? Version)>();
        var helperRun = new UpdateHelper(plan, (executable, version) =>
        {
            launches.Add((executable, version));
            File.WriteAllText(DesktopUpdater.ConfirmedPath(data), version);
            return new FakeApp(exits: false);
        }, TextWriter.Null, waitForExit: (_, _) => Task.FromResult(true));
        Assert.Equal(0, await helperRun.ApplyAsync());
        Assert.Equal([(installed.Executable, (string?)"2.1")], launches);
        Assert.Equal("2.1", InstalledApp.FromRoot(installed.Root)!.Version);
        Assert.False(Directory.Exists(installed.Root + ".previous"));
        Assert.False(Directory.Exists(installed.Root + ".new"));

        var after = new DesktopUpdater(InstalledApp.FromRoot(installed.Root), data, () => server.BaseAddress.ToString(), http);
        Assert.False((await after.CheckAsync(default)).Ready);
        Assert.Empty(Directory.GetDirectories(Path.Combine(data, "updates"))); // the staged copy is cleaned up
        Directory.Delete(folder, true);
    }

    [Fact]
    public async Task AVersionThatDoesNotStartIsTakenBackAndNotOfferedAgain()
    {
        await using var server = await BundleFixture.StartAsync("2.0", (Releases.Desktop, Rid, BundleFixture.AppArchive("2.0", Rid, "BoSoFa", Zip)));
        var folder = Directory.CreateTempSubdirectory("bosofa-desktop-update-").FullName;
        var data = Path.Combine(folder, "data");
        var installed = Install(Path.Combine(folder, "apps"), "1.0");
        using var http = new HttpClient();
        var updater = new DesktopUpdater(installed, data, () => server.BaseAddress.ToString(), http, _ => null);
        Assert.True((await updater.CheckAsync(default)).Ready);
        Assert.Null(updater.Install());
        var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(Path.Combine(data, "updates", "plan.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var launches = new List<string?>();
        var broken = new FakeApp(exits: true); // dies before it shows anything
        var helper = new UpdateHelper(plan, (_, version) =>
        {
            launches.Add(version);
            return version == null ? new FakeApp(exits: false) : broken;
        }, TextWriter.Null, confirmLimit: TimeSpan.FromSeconds(5), poll: TimeSpan.FromMilliseconds(20), waitForExit: (_, _) => Task.FromResult(true));
        Assert.Equal(1, await helper.ApplyAsync());
        Assert.Equal(["2.0", null], launches); // the new one, then the old one again
        Assert.Equal("1.0", InstalledApp.FromRoot(installed.Root)!.Version);
        Assert.False(Directory.Exists(installed.Root + ".previous"));

        var again = new DesktopUpdater(InstalledApp.FromRoot(installed.Root), data, () => server.BaseAddress.ToString(), http);
        Assert.False((await again.CheckAsync(default)).Ready);

        // An app that will not close changes nothing.
        var stuck = new UpdateHelper(plan, (_, _) => throw new InvalidOperationException("must not start"), TextWriter.Null,
            waitForExit: (_, _) => Task.FromResult(false));
        Assert.Equal(1, await stuck.ApplyAsync());
        Assert.Equal("1.0", InstalledApp.FromRoot(installed.Root)!.Version);
        Directory.Delete(folder, true);
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition());
    }
}
