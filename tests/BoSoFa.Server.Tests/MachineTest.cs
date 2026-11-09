using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BoSoFa.Daemon;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

public partial class MachineTest
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [GeneratedRegex(@"approve the code ([A-Z0-9]{4}-[A-Z0-9]{4})")]
    private static partial Regex CodePattern();

    static async Task<HttpClient> SignIn(TestServer server, string email)
    {
        var client = server.Client();
        (await client.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest(email, null))).EnsureSuccessStatusCode();
        return client;
    }

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
    public async Task DaemonEnrollsWithAnApprovedCodeAndConnects()
    {
        await using var server = await TestServer.StartAsync();
        using var boris = await SignIn(server, "boris@example.com");
        var dataDir = Path.Combine(server.DataDirectory, "daemon");
        var output = new StringWriter();
        var synchronizedOutput = TextWriter.Synchronized(output);
        var enrolling = Cli.RunAsync(["enroll", "--server", server.BaseAddress.ToString(), "--name", "test-mac",
            "--data-dir", dataDir], synchronizedOutput, synchronizedOutput);

        var code = await Eventually(() => Task.FromResult(CodePattern().Match(output.ToString())), m => m.Success);
        Assert.True(code.Success, output.ToString());
        var userCode = code.Groups[1].Value;
        var enrollment = await boris.GetFromJsonAsync<EnrollmentDto>($"/api/v1/enrollments/{userCode}");
        Assert.Equal(("test-mac", EnrollStatus.Pending), (enrollment!.Name, enrollment.Status));
        // The code is accepted without its dash and in lower case, as people type it.
        var approval = await boris.PostAsync($"/api/v1/enrollments/{userCode.Replace("-", "").ToLowerInvariant()}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        var machine = await approval.Content.ReadFromJsonAsync<MachineDto>();
        Assert.Equal(0, await enrolling.WaitAsync(Timeout));
        Assert.Equal(("test-mac", "boris@example.com", false), (machine!.Name, machine.OwnerName, machine.Online));
        // Approving again (a double click) returns the same machine instead of creating another one.
        var again = await boris.PostAsync($"/api/v1/enrollments/{userCode}/approve", null);
        Assert.Equal(machine.Id, (await again.Content.ReadFromJsonAsync<MachineDto>())!.Id);

        var state = new DaemonStore(dataDir).LoadState()!;
        Assert.Equal(machine.Id, state.MachineId);
        using var key = new DaemonStore(dataDir).LoadOrCreateKey();
        var link = new DaemonLink(state, key, TextWriter.Null);
        var connected = new TaskCompletionSource<WelcomeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.Connected += welcome => connected.TrySetResult(welcome);
        using var stop = new CancellationTokenSource();
        var running = link.RunAsync(stop.Token);
        Assert.Equal(machine.Id, (await connected.Task.WaitAsync(Timeout)).MachineId);

        var online = await Eventually(() => boris.GetFromJsonAsync<MachineDto[]>("/api/v1/machines")!, m => m![0].Online);
        Assert.True(online![0].Online);
        Assert.Equal(Cli.Version, online[0].DaemonVersion);

        await stop.CancelAsync();
        await running.WaitAsync(Timeout);
        var offline = await Eventually(() => boris.GetFromJsonAsync<MachineDto[]>("/api/v1/machines")!, m => !m![0].Online);
        Assert.False(offline![0].Online);
    }

    [Fact]
    public async Task TokensNeedTheMachineKeyAFreshTimestampAndAnUnusedNonce()
    {
        await using var server = await TestServer.StartAsync();
        using var boris = await SignIn(server, "boris@example.com");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var daemon = server.Client();
        var started = await (await daemon.PostAsJsonAsync("/api/daemon/v1/enroll/start", new EnrollStartRequest("m", "linux",
            "x64", "1.0", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())))).Content.ReadFromJsonAsync<EnrollStartResponse>();
        (await boris.PostAsync($"/api/v1/enrollments/{started!.UserCode}/approve", null)).EnsureSuccessStatusCode();
        var poll = await (await daemon.PostAsJsonAsync("/api/daemon/v1/enroll/poll", new EnrollPollRequest(started.EnrollmentId)))
            .Content.ReadFromJsonAsync<EnrollPollResponse>();
        var machineId = poll!.MachineId!.Value;

        DaemonTokenRequest Request(ECDsa signer, long timestamp, string nonce) => new(machineId, timestamp, nonce,
            Convert.ToBase64String(signer.SignData(Encoding.UTF8.GetBytes(DaemonToken.SignedText(machineId, timestamp, nonce)),
                HashAlgorithmName.SHA256)));
        Task<HttpResponseMessage> Token(DaemonTokenRequest request) => daemon.PostAsJsonAsync("/api/daemon/v1/token", request);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var good = Request(key, now, "0123456789abcdef0123");
        var issued = await Token(good);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Token(good)).StatusCode); // replayed nonce
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Token(Request(stranger, now, "fedcba9876543210fedc"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Token(Request(key, now - (long)TimeSpan.FromMinutes(10).TotalMilliseconds, "aaaabbbbccccddddeeee"))).StatusCode);

        var token = (await issued.Content.ReadFromJsonAsync<DaemonTokenResponse>())!.AccessToken;
        using var authorized = server.Client();
        authorized.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(machineId, (await authorized.GetFromJsonAsync<MachineDto>("/api/daemon/v1/machine"))!.Id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await daemon.GetAsync("/api/daemon/v1/machine")).StatusCode);
        // A daemon token is not a person's session.
        Assert.Equal(HttpStatusCode.Unauthorized, (await authorized.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task ADaemonKeepsItsIdentityShowsWhatItHasAndItsOwnerNamesIt()
    {
        await using var server = await TestServer.StartAsync();
        using var boris = await SignIn(server, "boris@example.com");
        using var alice = await SignIn(server, "alice@example.com");
        var dataDir = Path.Combine(server.DataDirectory, "daemon");
        var store = new DaemonStore(dataDir);
        var instanceId = store.InstanceId();
        Assert.Equal(instanceId, new DaemonStore(dataDir).InstanceId()); // kept in the data folder

        async Task<(string UserCode, Task<int> Enrolling)> StartAsync()
        {
            var output = new StringWriter();
            var synchronized = TextWriter.Synchronized(output);
            File.Delete(Path.Combine(dataDir, "daemon.json")); // enrolled from scratch, as after a reset; the identity stays
            var enrolling = Cli.RunAsync(["enroll", "--server", server.BaseAddress.ToString(), "--data-dir", dataDir], synchronized, synchronized);
            var code = await Eventually(() => Task.FromResult(CodePattern().Match(output.ToString())), m => m.Success || enrolling.IsCompleted);
            Assert.True(code.Success, output.ToString());
            return (code.Groups[1].Value, enrolling);
        }

        // The approver sees the daemon's identity and what the computer has, and names the machine.
        var (first, enrolling) = await StartAsync();
        var enrollment = (await boris.GetFromJsonAsync<EnrollmentDto>($"/api/v1/enrollments/{first}"))!;
        Assert.Equal(instanceId, enrollment.InstanceId);
        Assert.True(enrollment.Resources is { Processors: > 0, MemoryBytes: > 0, DiskBytes: > 0, Platform.Length: > 0 }, enrollment.Resources?.ToString());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await boris.PostAsync($"/api/v1/enrollments/{first}/approve?name=%20", null)).StatusCode);
        var machine = (await (await boris.PostAsync($"/api/v1/enrollments/{first}/approve?name={Uri.EscapeDataString(" Build box ")}", null))
            .Content.ReadFromJsonAsync<MachineDto>())!;
        Assert.Equal(0, await enrolling.WaitAsync(Timeout));
        Assert.Equal(("Build box", instanceId), (machine.Name, machine.InstanceId));
        Assert.NotNull(machine.Resources);

        // Only the owner renames it.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await alice.PutAsJsonAsync($"/api/v1/machines/{machine.Id}/name", new RenameMachineRequest("Mine now"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await boris.PutAsJsonAsync($"/api/v1/machines/{machine.Id}/name", new RenameMachineRequest(new string('x', 101)))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await boris.PutAsJsonAsync($"/api/v1/machines/{machine.Id}/name", new RenameMachineRequest("Studio"))).StatusCode);

        // Enrolled again by the same owner, the daemon gets its machine back, name and all.
        var (second, enrollingAgain) = await StartAsync();
        var back = (await (await boris.PostAsync($"/api/v1/enrollments/{second}/approve", null)).Content.ReadFromJsonAsync<MachineDto>())!;
        Assert.Equal(0, await enrollingAgain.WaitAsync(Timeout));
        Assert.Equal((machine.Id, "Studio"), (back.Id, back.Name));
        Assert.Single((await boris.GetFromJsonAsync<MachineDto[]>("/api/v1/machines"))!);

        // Online, its owner measures it now; nobody else may.
        var state = store.LoadState()!;
        using var key = store.LoadOrCreateKey();
        using var stop = new CancellationTokenSource();
        var running = new DaemonLink(state, key, TextWriter.Null, instanceId: instanceId).RunAsync(stop.Token);
        await Eventually(() => boris.GetFromJsonAsync<MachineDto[]>("/api/v1/machines")!, m => m![0].Online);
        var now = (await boris.GetFromJsonAsync<MachineResources>($"/api/v1/machines/{machine.Id}/resources"))!;
        Assert.True(now is { MemoryBytes: > 0, MemoryAvailableBytes: > 0, DiskFreeBytes: > 0 }, now.ToString());
        Assert.True(now.MemoryAvailableBytes <= now.MemoryBytes && now.DiskFreeBytes <= now.DiskBytes, now.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync($"/api/v1/machines/{machine.Id}/resources")).StatusCode);
        await stop.CancelAsync();
        await running.WaitAsync(Timeout);
    }
}
