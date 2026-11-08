using System.Net;
using System.Net.Http.Json;
using BoSoFa.Protocol;
using BTDB.Replication;
using Microsoft.Extensions.DependencyInjection;

namespace BoSoFa.Server.Tests;

/// <summary>Several nodes in one process over shared file-system storage (plan M2).</summary>
public class ClusterTest
{
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

    [Fact]
    public async Task FollowerRunsTheApplicationAndItsWritesReachTheLeader()
    {
        var root = Path.Combine(Path.GetTempPath(), "bosofa-cluster-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var first = await TestServer.StartClusterNodeAsync(root, "node-1");
            using var boris = first.Client();
            (await boris.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", "Boris")))
                .EnsureSuccessStatusCode();
            Assert.Equal(ReplicationNodeRole.Leader, first.Node.Role);

            // The second node restores the leader's published history, schema included, and runs the application.
            await using var second = await TestServer.StartClusterNodeAsync(root, "node-2");
            var annaCookies = new CookieContainer(); // cookies belong to the host, not the port: valid on both nodes
            using var anna = second.Client(annaCookies);
            (await anna.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("anna@example.com", "Anna")))
                .EnsureSuccessStatusCode();
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

            // The shared key ring makes a sign-in on one node valid on the other.
            using var annaOnFirst = first.Client(annaCookies);
            Assert.Equal("Anna", (await annaOnFirst.GetFromJsonAsync<UserDto>("/api/v1/me"))!.Name);
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (IOException) { }
        }
    }
}
