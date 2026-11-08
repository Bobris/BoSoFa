using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoSoFa.Daemon;
using BoSoFa.Daemon.Service;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

public class DaemonUpdaterTest
{
    sealed class Blobs(byte[] content) : IBlobDownloader
    {
        public Task DownloadBlobAsync(string sha256, string file, CancellationToken cancellation) => File.WriteAllBytesAsync(file, content, cancellation);
    }

    static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    static string PublicKey => Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());

    static UpdateDaemonCommand Signed(string version, ECDsa? key = null, string? rid = null)
    {
        var manifest = JsonSerializer.Serialize(new DaemonManifest(version, [new(rid ?? DaemonUpdater.CurrentRid, new string('a', 64), 3)]), ProtocolJsonContext.Default.DaemonManifest);
        return new(version, manifest, Convert.ToBase64String((key ?? Key).SignData(Encoding.UTF8.GetBytes(manifest), HashAlgorithmName.SHA256)));
    }

    static (DaemonUpdater Updater, VersionStore Versions, TaskCompletionSource Switched) Create(bool launched = true, string? publicKey = null, Func<bool>? busy = null)
    {
        var directory = Directory.CreateTempSubdirectory("bosofa-update-").FullName;
        var versions = new VersionStore(directory);
        versions.Save(new("1.0", null, null, ["broken"]));
        var updater = new DaemonUpdater(directory, new Blobs([1, 2, 3]), publicKey ?? PublicKey, launched, busy ?? (() => false), TextWriter.Null, TimeSpan.FromMilliseconds(10));
        var switched = new TaskCompletionSource();
        updater.SwitchRequested += switched.SetResult;
        return (updater, versions, switched);
    }

    [Fact]
    public async Task ASignedReleaseIsPutInPlaceAndGoesOnTrial()
    {
        var (updater, versions, switched) = Create();
        await updater.ApplyAsync(Signed("2.0"), default);
        await switched.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(versions.ExecutableOf("2.0")));
        var state = versions.Load();
        Assert.Equal(("2.0", "1.0"), (state.Current, state.Previous));
        Assert.NotNull(state.TrialStartedAt);
    }

    [Fact]
    public async Task ItWaitsUntilNoSessionIsBusy()
    {
        var busy = true;
        var (updater, versions, switched) = Create(busy: () => Volatile.Read(ref busy));
        await updater.ApplyAsync(Signed("2.0"), default);
        await Task.Delay(100);
        Assert.False(switched.Task.IsCompleted);
        Assert.Equal("1.0", versions.Load().Current);
        Volatile.Write(ref busy, false);
        await switched.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AnythingNotProperlySignedOrUnusableIsRefused()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var tampered = Signed("2.0") with { Manifest = Signed("3.0").Manifest };
        foreach (var (command, reason) in new[]
                 {
                     (Signed("2.0", other), "signature"), (tampered, "signature"), (Signed("broken"), "already failed"), (Signed("2.0", rid: "plan9-mips"), "no build"),
                     (Signed("../evil"), "does not describe")
                 })
        {
            var (updater, versions, switched) = Create();
            await updater.ApplyAsync(command, default);
            Assert.Contains(reason, updater.LastRefusal);
            Assert.False(switched.Task.IsCompleted);
            Assert.Equal("1.0", versions.Load().Current);
        }
        var (unlaunched, _, _) = Create(launched: false);
        await unlaunched.ApplyAsync(Signed("2.0"), default);
        Assert.Contains("launcher", unlaunched.LastRefusal);
    }
}

public class DaemonRolloutTest
{
    /// <summary>A daemon reduced to what a rollout sees: which version it says it is, and the commands it is sent.</summary>
    sealed class FakeDaemon(TestServer server, DaemonState state, ECDsa key) : IAsyncDisposable
    {
        ClientWebSocket? _socket;
        Task? _listening;
        public ConcurrentQueue<UpdateDaemonCommand> Updates { get; } = new();
        public ulong MachineId => state.MachineId;

        public async Task ConnectAsync(string version)
        {
            await DisconnectAsync();
            using var client = new ServerClient(server.BaseAddress);
            var token = await client.GetTokenAsync(state.MachineId, key, default);
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token.AccessToken);
            await socket.ConnectAsync(DaemonSocket.ConnectUri(server.BaseAddress), default);
            await DaemonSocket.SendAsync(socket, new HelloMessage(version, ProtocolVersion.Current, DaemonStore.OsName(), DaemonStore.ArchName(), "fake"), default);
            _socket = socket;
            _listening = Task.Run(async () =>
            {
                try
                {
                    while (await DaemonSocket.ReceiveAsync(socket, default) is { } message)
                        if (message is CommandMessage { Command: UpdateDaemonCommand update } command)
                        {
                            Updates.Enqueue(update);
                            await DaemonSocket.SendAsync(socket, new CommandAckMessage([command.Id]), default);
                        }
                }
                catch (Exception exception) when (exception is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException) { }
            });
        }

        public async Task DisconnectAsync()
        {
            _socket?.Abort();
            if (_listening != null) await _listening;
            _socket?.Dispose();
            _socket = null;
        }

        public async ValueTask DisposeAsync() => await DisconnectAsync();
    }

    static async Task<(TestServer Server, HttpClient Boris, FakeDaemon[] Daemons)> StartAsync(int count)
    {
        var server = await TestServer.StartAsync(extraArgs: ["--BoSoFa:RolloutTickSeconds=1"]);
        var boris = server.Client(new CookieContainer());
        (await boris.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", null))).EnsureSuccessStatusCode();
        var daemons = new List<FakeDaemon>();
        for (var i = 0; i < count; i++)
        {
            var directory = Path.Combine(server.DataDirectory, "daemon" + i);
            var state = await SessionTest.EnrollAsync(server.BaseAddress, boris, directory);
            var key = new DaemonStore(directory).LoadOrCreateKey();
            var daemon = new FakeDaemon(server, state, key);
            await daemon.ConnectAsync("1.0");
            daemons.Add(daemon);
        }
        await Eventually(async () => (await boris.GetFromJsonAsync<MachineDto[]>("/api/v1/machines"))!.Count(machine => machine.Online) == count);
        return (server, boris, daemons.ToArray());
    }

    static async Task PublishAsync(HttpClient boris, string version)
    {
        var blob = await (await boris.PostAsync("/api/v1/blobs", new ByteArrayContent([1, 2, 3]))).Content.ReadFromJsonAsync<BlobRef>();
        var manifest = JsonSerializer.Serialize(new DaemonManifest(version, [new("linux-x64", blob!.Sha256, blob.Size)]), ProtocolJsonContext.Default.DaemonManifest);
        (await boris.PostAsJsonAsync("/api/v1/daemon-releases", new PublishDaemonRelease(manifest, "c2lnbmVk", DaemonReleases.Stable))).EnsureSuccessStatusCode();
    }

    static async Task Eventually(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(50);
        }
    }

    static Task<DaemonRolloutDto> Rollout(HttpClient boris) => boris.GetFromJsonAsync<DaemonRolloutDto[]>("/api/v1/daemon-rollouts").ContinueWith(task => task.Result!.Single());

    [Fact]
    public async Task MachinesUpdateOneWaveAtATime()
    {
        var (server, boris, daemons) = await StartAsync(2);
        await using (server)
        using (boris)
        {
            await PublishAsync(boris, "2.0");
            (await boris.PostAsJsonAsync("/api/v1/daemon-rollouts", new StartDaemonRollout("2.0", 1, 60))).EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.BadRequest, (await boris.PostAsJsonAsync("/api/v1/daemon-rollouts", new StartDaemonRollout("2.0"))).StatusCode); // one at a time

            await Eventually(() => Task.FromResult(daemons.Sum(daemon => daemon.Updates.Count) == 1));
            var first = daemons.Single(daemon => daemon.Updates.Count == 1);
            var second = daemons.Single(daemon => daemon != first);
            await Task.Delay(2500); // more ticks: the second one still has to wait
            Assert.Empty(second.Updates);
            Assert.Equal("2.0", first.Updates.Single().Version);

            await first.ConnectAsync("2.0");
            await Eventually(() => Task.FromResult(second.Updates.Count == 1));
            await second.ConnectAsync("2.0");
            await Eventually(async () => (await Rollout(boris)).Status == DaemonRolloutStatus.Done);
            Assert.Equal(2, (await Rollout(boris)).Updated);
            foreach (var daemon in daemons) await daemon.DisposeAsync();
        }
    }

    [Fact]
    public async Task AMachineThatDoesNotComeBackHaltsTheRollout()
    {
        var (server, boris, daemons) = await StartAsync(2);
        await using (server)
        using (boris)
        {
            await PublishAsync(boris, "2.0");
            (await boris.PostAsJsonAsync("/api/v1/daemon-rollouts", new StartDaemonRollout("2.0", 1, 2))).EnsureSuccessStatusCode();
            await Eventually(async () => (await Rollout(boris)).Status == DaemonRolloutStatus.Halted);
            Assert.Contains("did not come back", (await Rollout(boris)).Detail);
            Assert.Equal(1, daemons.Sum(daemon => daemon.Updates.Count)); // the other machine was left alone
            foreach (var daemon in daemons) await daemon.DisposeAsync();
        }
    }

    [Fact]
    public async Task OnlyAdminsPublishAndPinnedMachinesStay()
    {
        var (server, boris, daemons) = await StartAsync(1);
        await using (server)
        using (boris)
        {
            using var anna = server.Client(new CookieContainer());
            (await anna.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("anna@example.com", null))).EnsureSuccessStatusCode();
            var refused = new PublishDaemonRelease(JsonSerializer.Serialize(new DaemonManifest("2.0", [new("linux-x64", new string('b', 64), 1)]), ProtocolJsonContext.Default.DaemonManifest), "x", "stable");
            Assert.Equal(HttpStatusCode.BadRequest, (await boris.PostAsJsonAsync("/api/v1/daemon-releases", refused)).StatusCode); // that blob does not exist

            await PublishAsync(boris, "2.0");
            var body = (await boris.GetFromJsonAsync<DaemonReleaseDto[]>("/api/v1/daemon-releases"))!;
            Assert.Equal("2.0", Assert.Single(body).Version);
            Assert.Equal(HttpStatusCode.Forbidden, (await anna.PostAsJsonAsync("/api/v1/daemon-rollouts", new StartDaemonRollout("2.0"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await anna.PutAsJsonAsync($"/api/v1/machines/{daemons[0].MachineId}/update-policy", new SetMachineUpdatePolicy("stable", "1.0"))).StatusCode);

            (await boris.PutAsJsonAsync($"/api/v1/machines/{daemons[0].MachineId}/update-policy", new SetMachineUpdatePolicy("stable", "1.0"))).EnsureSuccessStatusCode();
            (await boris.PostAsJsonAsync("/api/v1/daemon-rollouts", new StartDaemonRollout("2.0", 1, 60))).EnsureSuccessStatusCode();
            await Eventually(async () => (await Rollout(boris)).Status == DaemonRolloutStatus.Done); // nothing to do
            Assert.Empty(daemons[0].Updates);
            await daemons[0].DisposeAsync();
        }
    }
}
