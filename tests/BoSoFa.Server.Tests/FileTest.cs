using System.Net;
using System.Net.Http.Json;
using BoSoFa.Daemon;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

public class FileTest
{
    [Fact]
    public async Task PeopleBrowseAndTransferFilesOnlyUnderTheRoots()
    {
        await using var server = await TestServer.StartAsync();
        using var boris = await LoginAsync(server, "boris@example.com");
        using var anna = await LoginAsync(server, "anna@example.com");
        var dataDirectory = Path.Combine(server.DataDirectory, "daemon");
        var state = await SessionTest.EnrollAsync(server.BaseAddress, boris, dataDirectory);
        var work = Directory.CreateDirectory(Path.Combine(server.DataDirectory, "work")).FullName;
        Directory.CreateDirectory(Path.Combine(work, "src"));
        await File.WriteAllTextAsync(Path.Combine(work, "src", "notes.txt"), "héllo");
        await File.WriteAllBytesAsync(Path.Combine(work, "tool.bin"), [1, 0, 2]);
        await File.WriteAllTextAsync(Path.Combine(server.DataDirectory, "secret.txt"), "secret");
        var canLink = !OperatingSystem.IsWindows();
        if (canLink) File.CreateSymbolicLink(Path.Combine(work, "escape.txt"), Path.Combine(server.DataDirectory, "secret.txt"));

        using var key = new DaemonStore(dataDirectory).LoadOrCreateKey();
        using var outbox = new Outbox(Path.Combine(dataDirectory, "outbox"));
        var leaser = new ServerAccountLeaser(state, key);
        await using var host = new SessionHost(dataDirectory, outbox, new(Accounts: leaser, Profiles: leaser, Uploader: leaser, Roots: [work]), TextWriter.Null);
        using var stop = new CancellationTokenSource();
        var link = new DaemonLink(state, key, TextWriter.Null, outbox, host);
        var connected = new TaskCompletionSource();
        link.Connected += _ => connected.TrySetResult();
        var running = link.RunAsync(stop.Token);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var files = $"/api/v1/machines/{state.MachineId}/files";
        string Query(string path) => "?path=" + Uri.EscapeDataString(path);
        await Eventually(async () => (await boris.GetAsync(files + "/roots")).StatusCode == HttpStatusCode.OK);
        var roots = await boris.GetFromJsonAsync<FileRootsResult>(files + "/roots");
        Assert.Equal(work, Assert.Single(roots!.Roots));
        Assert.Equal(HttpStatusCode.Forbidden, (await anna.GetAsync(files + "/roots")).StatusCode);

        var list = await boris.GetFromJsonAsync<FileListResult>(files + Query(work));
        Assert.Null(list!.Parent);
        Assert.Equal("src", list.Entries[0].Name); // folders first
        Assert.True(list.Entries[0].IsDirectory);
        var inner = await boris.GetFromJsonAsync<FileListResult>(files + Query(Path.Combine(work, "src")));
        Assert.Equal(work, inner!.Parent);

        var text = await boris.GetFromJsonAsync<FileTextResult>(files + "/text" + Query(Path.Combine(work, "src", "notes.txt")));
        Assert.Equal("héllo", text!.Text);
        Assert.Equal(6, text.NextOffset); // bytes, not characters
        Assert.True((await boris.GetFromJsonAsync<FileTextResult>(files + "/text" + Query(Path.Combine(work, "tool.bin"))))!.Binary);

        // Traversal, a sibling that shares the root's prefix, and a link that leads outside.
        var escapes = new List<string> { Path.Combine(work, "..", "secret.txt"), Path.Combine(server.DataDirectory, "secret.txt"), work + "-other" };
        if (canLink) escapes.Add(Path.Combine(work, "escape.txt"));
        foreach (var escape in escapes)
        {
            var refused = await boris.GetAsync(files + "/text" + Query(escape));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("outside", await refused.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await boris.PostAsJsonAsync(files + "/fetch", new FileFetchRequest(escapes[1]))).StatusCode);

        var fetched = await (await boris.PostAsJsonAsync(files + "/fetch", new FileFetchRequest(Path.Combine(work, "src", "notes.txt"))))
            .Content.ReadFromJsonAsync<FileFetchResult>();
        Assert.Equal("héllo", await boris.GetStringAsync($"/api/v1/blobs/{fetched!.Blob.Sha256}"));

        var uploaded = await (await boris.PostAsync("/api/v1/blobs", new ByteArrayContent("from the browser"u8.ToArray())))
            .Content.ReadFromJsonAsync<BlobRef>();
        Task<HttpResponseMessage> Store(string directory, string name, bool overwrite) =>
            boris.PostAsJsonAsync(files + "/store", new FileStoreRequest(directory, name, uploaded!.Sha256, overwrite));
        Assert.Equal(HttpStatusCode.OK, (await Store(work, "new.txt", false)).StatusCode);
        Assert.Equal("from the browser", await File.ReadAllTextAsync(Path.Combine(work, "new.txt")));
        var existing = await Store(work, "new.txt", false);
        Assert.Contains("already exists", await existing.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await Store(work, "new.txt", true)).StatusCode);
        foreach (var bad in new[] { "../out.txt", "..", "" })
            Assert.Equal(HttpStatusCode.BadRequest, (await Store(work, bad, true)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Store(server.DataDirectory, "out.txt", true)).StatusCode);
        Assert.False(File.Exists(Path.Combine(server.DataDirectory, "out.txt")));
        Assert.Empty(Directory.GetFiles(work, ".bosofa-*"));

        await stop.CancelAsync();
        try { await running; }
        catch (OperationCanceledException) { }
        await Eventually(async () => (await (await boris.GetAsync(files + "/roots")).Content.ReadAsStringAsync()).Contains("offline"));
    }

    static async Task<HttpClient> LoginAsync(TestServer server, string email)
    {
        var client = server.Client(new CookieContainer());
        (await client.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest(email, null))).EnsureSuccessStatusCode();
        return client;
    }

    static async Task Eventually(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(50);
        }
    }
}
