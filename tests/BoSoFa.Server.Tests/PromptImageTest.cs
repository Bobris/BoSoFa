using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BoSoFa.Daemon;
using BoSoFa.Daemon.Harnesses.Claude;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;
using Xunit;

namespace BoSoFa.Server.Tests;

[Collection("Replay")]
public class PromptImageTest
{
    static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 1, 2, 3];

    static async Task<string> UploadAsync(HttpClient client, byte[] content) =>
        (await (await client.PostAsync("/api/v1/blobs", new ByteArrayContent(content))).Content.ReadFromJsonAsync<BlobRef>())!.Sha256;

    static async Task<T> Eventually<T>(Func<Task<T>> probe, Func<T, bool> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var value = await probe();
            if (done(value)) return value;
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for " + what);
            await Task.Delay(50);
        }
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0, 0, 0, 0, 0, 0 }, "image/gif")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, "image/webp")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x41, 0x56, 0x45 }, null)]
    [InlineData(new byte[] { 0x3C, 0x73, 0x76, 0x67 }, null)]
    [InlineData(new byte[0], null)]
    public void TheTypeComesFromTheContent(byte[] header, string? mediaType) => Assert.Equal(mediaType, PromptImages.MediaTypeOf(header));

    [Fact]
    public async Task OnlyRealImagesWithinTheLimitsAreAccepted()
    {
        await using var server = await TestServer.StartAsync();
        using var boris = server.Client(new CookieContainer());
        (await boris.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", null))).EnsureSuccessStatusCode();
        var request = new CreateSessionRequest(1, Harnesses.Replay, "/", "Look", null, null);

        async Task<string> RefusalAsync(params string[] images)
        {
            var response = await boris.PostAsJsonAsync("/api/v1/sessions", request with { Images = images });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }

        Assert.Contains("PNG", await RefusalAsync(await UploadAsync(boris, "<svg></svg>"u8.ToArray())));
        Assert.Contains("not uploaded", await RefusalAsync(new string('a', 64)));
        Assert.Contains("SHA-256", await RefusalAsync("../../etc/passwd"));
        Assert.Contains("at most", await RefusalAsync(Enumerable.Range(0, 11).Select(i => i.ToString("x64")).ToArray()));
        var tooLarge = new byte[PromptImages.MaximumBytes + 1];
        Png.CopyTo(tooLarge, 0);
        Assert.Contains("MB", await RefusalAsync(await UploadAsync(boris, tooLarge)));
    }

    [Fact]
    public async Task AnImageTravelsToTheMachineAndStaysInTheTranscript()
    {
        ReplayTransport.SignalTimeoutOverride = TimeSpan.Zero;
        var directory = Path.Combine(Path.GetTempPath(), "bosofa-images-" + Guid.NewGuid().ToString("N"));
        var work = Directory.CreateDirectory(Path.Combine(directory, "work")).FullName;
        try
        {
            await using var server = await TestServer.StartAsync(Path.Combine(directory, "server"));
            using var boris = server.Client(new CookieContainer());
            (await boris.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", null))).EnsureSuccessStatusCode();
            var daemonDirectory = Path.Combine(directory, "daemon");
            var state = await SessionTest.EnrollAsync(server.BaseAddress, boris, daemonDirectory);
            using var key = new DaemonStore(daemonDirectory).LoadOrCreateKey();
            using var outbox = new Outbox(Path.Combine(daemonDirectory, "outbox"));
            await using var host = new SessionHost(daemonDirectory, outbox,
                new(ReplayFixture: SessionTest.Fixture, Profiles: new ServerAccountLeaser(state, key)), TextWriter.Null);
            using var stop = new CancellationTokenSource();
            var running = new DaemonLink(state, key, TextWriter.Null, outbox, host).RunAsync(stop.Token);
            await Eventually(() => boris.GetFromJsonAsync<MachineDto[]>("/api/v1/machines")!, machines => machines!.Single().Online, "the daemon");

            var sha256 = await UploadAsync(boris, Png);
            var created = await boris.PostAsJsonAsync("/api/v1/sessions",
                new CreateSessionRequest(state.MachineId, Harnesses.Replay, work, "Look", null, null, Images: [sha256]));
            created.EnsureSuccessStatusCode();
            var session = (await created.Content.ReadFromJsonAsync<SessionDto>())!.Id;

            var file = Path.Combine(daemonDirectory, "images", sha256 + ".png");
            await Eventually(() => Task.FromResult(File.Exists(file)), exists => exists, "the image on the machine");
            Assert.Equal(Png, await File.ReadAllBytesAsync(file));
            await Eventually(() => boris.GetFromJsonAsync<RuntimeRequestDto[]>($"/api/v1/sessions/{session}/requests")!, requests => requests!.Length > 0, "the turn");

            var user = (await boris.GetFromJsonAsync<SessionItemDto[]>($"/api/v1/sessions/{session}/items"))!.First(item => item.Kind == SessionItemKind.User);
            var stored = JsonSerializer.Deserialize(user.Data!, ProtocolJsonContext.Default.PromptImageArray)!;
            Assert.Equal(new PromptImage(sha256, "image/png", Png.Length), Assert.Single(stored));

            await stop.CancelAsync();
            await running;
        }
        finally
        {
            ReplayTransport.SignalTimeoutOverride = null;
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task AMessageWhoseImagesCannotBeFetchedIsNotSentWithoutThem()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bosofa-images-" + Guid.NewGuid().ToString("N"));
        var work = Directory.CreateDirectory(Path.Combine(directory, "work")).FullName;
        try
        {
            await using var server = await TestServer.StartAsync(Path.Combine(directory, "server"));
            using var boris = server.Client(new CookieContainer());
            (await boris.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", null))).EnsureSuccessStatusCode();
            var daemonDirectory = Path.Combine(directory, "daemon");
            var state = await SessionTest.EnrollAsync(server.BaseAddress, boris, daemonDirectory);
            using var key = new DaemonStore(daemonDirectory).LoadOrCreateKey();
            using var outbox = new Outbox(Path.Combine(daemonDirectory, "outbox"));
            await using var host = new SessionHost(daemonDirectory, outbox, new(ReplayFixture: SessionTest.Fixture), TextWriter.Null);
            using var stop = new CancellationTokenSource();
            var running = new DaemonLink(state, key, TextWriter.Null, outbox, host).RunAsync(stop.Token);
            await Eventually(() => boris.GetFromJsonAsync<MachineDto[]>("/api/v1/machines")!, machines => machines!.Single().Online, "the daemon");

            var created = await boris.PostAsJsonAsync("/api/v1/sessions",
                new CreateSessionRequest(state.MachineId, Harnesses.Replay, work, "Look", null, null, Images: [await UploadAsync(boris, Png)]));
            created.EnsureSuccessStatusCode();
            var session = (await created.Content.ReadFromJsonAsync<SessionDto>())!.Id;
            var items = await Eventually(() => boris.GetFromJsonAsync<SessionItemDto[]>($"/api/v1/sessions/{session}/items")!,
                found => found!.Any(item => item.Kind == SessionItemKind.Notice), "the notice");
            Assert.Contains("could not be fetched", items!.First(item => item.Kind == SessionItemKind.Notice).Text);
            Assert.DoesNotContain(items!, item => item.Kind == SessionItemKind.Assistant);

            await stop.CancelAsync();
            await running;
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
        }
    }
}
