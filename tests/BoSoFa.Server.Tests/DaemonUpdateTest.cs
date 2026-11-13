using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using BoSoFa.Daemon;
using BoSoFa.Daemon.Service;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

public class DaemonUpdaterTest
{
    /// <summary>Serves one daemon binary, as the server's downloads would.</summary>
    sealed class Downloads(byte[] content) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Asked) Asked.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }

    static readonly byte[] Binary = [1, 2, 3];
    static DaemonUpdate Offer(string version, byte[]? content = null) => new(version, Convert.ToHexStringLower(SHA256.HashData(content ?? Binary)), 3);

    static (DaemonUpdater Updater, VersionStore Versions, TaskCompletionSource Switched, Downloads Downloads) Create(bool launched = true, Func<bool>? busy = null)
    {
        var directory = Directory.CreateTempSubdirectory("bosofa-update-").FullName;
        var versions = new VersionStore(directory);
        versions.Save(new("1.0"));
        var downloads = new Downloads(Binary);
        var updater = new DaemonUpdater(directory, new Uri("https://server.example"), new HttpClient(downloads), launched, busy ?? (() => false), TextWriter.Null,
            TimeSpan.FromMilliseconds(10));
        var switched = new TaskCompletionSource();
        updater.SwitchRequested += switched.SetResult;
        return (updater, versions, switched, downloads);
    }

    [Fact]
    public async Task TheOfferedBuildIsFetchedPutInPlaceAndSwitchedTo()
    {
        var (updater, versions, switched, downloads) = Create();
        await updater.ApplyAsync(Offer("2.0"), default);
        await switched.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Binary, await File.ReadAllBytesAsync(versions.ExecutableOf("2.0")));
        Assert.Equal("2.0", versions.Load().Current);
        Assert.Equal([$"/api/v1/downloads/daemon/{DaemonUpdater.CurrentRid}"], downloads.Asked);
    }

    [Fact]
    public async Task ItWaitsUntilNoSessionIsBusy()
    {
        var busy = true;
        var (updater, versions, switched, _) = Create(busy: () => Volatile.Read(ref busy));
        await updater.ApplyAsync(Offer("2.0"), default);
        await Task.Delay(100);
        Assert.False(switched.Task.IsCompleted);
        Assert.Equal("1.0", versions.Load().Current);
        Volatile.Write(ref busy, false);
        await switched.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ADownloadThatDoesNotMatchOrAnOddVersionIsRefused()
    {
        foreach (var (offer, reason) in new[] { (Offer("2.0", [9, 9]), "does not match"), (Offer("../evil"), "not a plain version") })
        {
            var (updater, versions, switched, _) = Create();
            await updater.ApplyAsync(offer, default);
            Assert.Contains(reason, updater.LastRefusal);
            Assert.False(switched.Task.IsCompleted);
            Assert.Equal("1.0", versions.Load().Current);
            Assert.False(File.Exists(versions.ExecutableOf("2.0")));
        }
        var (unlaunched, _, _, nothing) = Create(launched: false);
        await unlaunched.ApplyAsync(Offer("2.0"), default);
        Assert.Contains("launcher", unlaunched.LastRefusal);
        Assert.Empty(nothing.Asked);
    }
}

public class DaemonUpdateOfferTest
{
    /// <summary>Says hello as a daemon of <paramref name="version"/> and returns the server's welcome.</summary>
    static async Task<WelcomeMessage> HelloAsync(TestServer server, DaemonState state, ECDsa key, string version, string os, string arch)
    {
        using var client = new ServerClient(server.BaseAddress);
        var token = await client.GetTokenAsync(state.MachineId, key, default);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token.AccessToken);
        await socket.ConnectAsync(DaemonSocket.ConnectUri(server.BaseAddress), default);
        await DaemonSocket.SendAsync(socket, new HelloMessage(version, ProtocolVersion.Current, os, arch, "fake"), default);
        var welcome = Assert.IsType<WelcomeMessage>(await DaemonSocket.ReceiveAsync(socket, default));
        socket.Abort();
        return welcome;
    }

    [Fact]
    public async Task AnOlderDaemonIsOfferedTheServersBuildAndNoOtherIs()
    {
        await using var server = await BundleFixture.StartAsync("2.0", (Releases.Daemon, "linux-x64", "linux two"u8.ToArray()));
        using var boris = server.Client(new CookieContainer());
        (await boris.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", null))).EnsureSuccessStatusCode();
        var directory = Path.Combine(server.DataDirectory, "daemon");
        var state = await SessionTest.EnrollAsync(server.BaseAddress, boris, directory);
        var key = new DaemonStore(directory).LoadOrCreateKey();

        var offered = (await HelloAsync(server, state, key, "1.0+abc", "linux", "x64")).Update;
        Assert.Equal(new DaemonUpdate("2.0", Convert.ToHexStringLower(SHA256.HashData("linux two"u8)), 9), offered);
        Assert.Null((await HelloAsync(server, state, key, "2.0", "linux", "x64")).Update);
        Assert.Null((await HelloAsync(server, state, key, "2.1", "linux", "x64")).Update); // never back
        Assert.Null((await HelloAsync(server, state, key, "1.0", "windows", "x64")).Update); // no build for it
    }
}
