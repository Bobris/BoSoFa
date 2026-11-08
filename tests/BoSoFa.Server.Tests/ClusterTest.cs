using System.Net;
using System.Net.Http.Json;
using BoSoFa.Protocol;
using BTDB.Replication;
using Xunit.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace BoSoFa.Server.Tests;

/// <summary>Several nodes in one process (plan M2), over shared file-system storage or Azurite.</summary>
public class ClusterTest(Azurite azurite, ITestOutputHelper output) : IClassFixture<Azurite>
{
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    void Step(string name) => output.WriteLine($"{_clock.Elapsed.TotalSeconds,6:0.0} s  {name}");

    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    static async Task<T> Eventually<T>(Func<Task<T>> probe, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            var value = await probe();
            if (done(value) || DateTime.UtcNow > deadline) return value;
            await Task.Delay(50);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FollowerRunsTheApplicationAndItsWritesReachTheLeader(bool onAzure)
    {
        var root = Path.Combine(Path.GetTempPath(), "bosofa-cluster-" + Guid.NewGuid().ToString("N"));
        var azure = onAzure ? azurite.ConnectionString : null;
        var container = "cluster-" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await using var first = await TestServer.StartClusterNodeAsync(root, "node-1", azure, container);
            Step("first node started");
            using var boris = first.Client();
            (await boris.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", "Boris")))
                .EnsureSuccessStatusCode();
            Step("boris signed in on the leader");
            Assert.Equal(ReplicationNodeRole.Leader, first.Node.Role);

            // The second node restores the leader's published history, schema included, and runs the application.
            await using var second = await TestServer.StartClusterNodeAsync(root, "node-2", azure, container);
            Step("second node started");
            var annaCookies = new CookieContainer(); // cookies belong to the host, not the port: valid on both nodes
            using var anna = second.Client(annaCookies);
            (await anna.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("anna@example.com", "Anna")))
                .EnsureSuccessStatusCode();
            Step("anna signed in on the follower");
            Assert.Equal(ReplicationNodeRole.Follower, second.Node.Role);

            // Anna signed in on the follower: the event went to the leader's log and both nodes applied it.
            var expected = new[] { "Anna", "Boris" };
            var onSecond = await anna.GetFromJsonAsync<UserDto[]>("/api/v1/users");
            Assert.Equal(expected, onSecond!.Select(user => user.Name));
            var onFirst = await Eventually(() => boris.GetFromJsonAsync<UserDto[]>("/api/v1/users")!, users => users!.Length == 2);
            Assert.Equal(expected, onFirst!.Select(user => user.Name));
            Assert.Equal(first.Node.Publisher!.LastAppliedEventId,
                (await Eventually(() => Task.FromResult(second.Node.Publisher!.LastAppliedEventId),
                    id => id == first.Node.Publisher!.LastAppliedEventId)));

            Step("both nodes caught up");
            // The shared key ring makes a sign-in on one node valid on the other.
            using var annaOnFirst = first.Client(annaCookies);
            Assert.Equal("Anna", (await annaOnFirst.GetFromJsonAsync<UserDto>("/api/v1/me"))!.Name);
            Step("checked");
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (IOException) { }
        }
    }

    /// <summary>A daemon connected to one node is online on every node; when a node vanishes without saying goodbye
    /// (here: a connection recorded for a node that is not in the cluster), the leader expires its connections.</summary>
    [Fact]
    public async Task MachinePresenceIsSharedByNodesAndDeadNodesExpire()
    {
        var root = Path.Combine(Path.GetTempPath(), "bosofa-cluster-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var first = await TestServer.StartClusterNodeAsync(root, "node-1");
            var cookies = new CookieContainer();
            using var boris = first.Client(cookies);
            (await boris.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", "Boris")))
                .EnsureSuccessStatusCode();
            // Only after the leader served a request is the new cluster's genesis published; a node restoring
            // before that is asked to restart (by its supervisor, which a test does not have).
            await using var second = await TestServer.StartClusterNodeAsync(root, "node-2");
            using var onSecond = second.Client(cookies);

            // Enrolled through the leader, connected to the follower.
            using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            using var daemon = second.Client();
            var started = await (await daemon.PostAsJsonAsync("/api/daemon/v1/enroll/start", new EnrollStartRequest("m",
                "linux", "x64", "1.0", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())))).Content
                .ReadFromJsonAsync<EnrollStartResponse>();
            (await boris.PostAsync($"/api/v1/enrollments/{started!.UserCode}/approve", null)).EnsureSuccessStatusCode();
            var machineId = (await (await daemon.PostAsJsonAsync("/api/daemon/v1/enroll/poll",
                new EnrollPollRequest(started.EnrollmentId))).Content.ReadFromJsonAsync<EnrollPollResponse>())!.MachineId!.Value;
            var link = new BoSoFa.Daemon.DaemonLink(new(second.BaseAddress, machineId), key, TextWriter.Null);
            using var stop = new CancellationTokenSource();
            var running = link.RunAsync(stop.Token);
            Step("daemon linked to the follower");

            bool Online(MachineDto[]? machines) => machines!.Single().Online;
            Assert.True(Online(await Eventually(() => boris.GetFromJsonAsync<MachineDto[]>("/api/v1/machines")!, Online)));
            Assert.True(Online(await onSecond.GetFromJsonAsync<MachineDto[]>("/api/v1/machines")));
            await stop.CancelAsync();
            await running.WaitAsync(Timeout);
            Assert.False(Online(await Eventually(() => boris.GetFromJsonAsync<MachineDto[]>("/api/v1/machines")!,
                machines => !Online(machines))));
            Step("online on both nodes, then offline");

            // A connection held by a node the cluster does not know: the leader's sweep expires it.
            await first.Node.Publisher!.PublishAsync(new BoSoFa.Server.Domain.MachineConnected
            {
                MachineId = machineId, NodeId = "node-ghost", ConnectionId = "ghost", DaemonVersion = "1.0", HostName = "ghost"
            });
            Assert.True(Online(await boris.GetFromJsonAsync<MachineDto[]>("/api/v1/machines")));
            Assert.False(Online(await Eventually(() => onSecond.GetFromJsonAsync<MachineDto[]>("/api/v1/machines")!,
                machines => !Online(machines))));
            Step("a dead node's connection expired");
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (IOException) { }
        }
    }
}
