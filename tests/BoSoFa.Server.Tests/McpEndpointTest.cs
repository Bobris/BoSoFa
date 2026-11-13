using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

public class McpEndpointTest
{
    sealed class Sink : ISessionSink
    {
        public ConcurrentQueue<SessionReport> Reports { get; } = new();
        public void Report(ulong sessionId, SessionReport report) => Reports.Enqueue(report);
        public void Delta(ulong sessionId, string itemRef, string text) { }
    }

    sealed class Uploader : IBlobUploader
    {
        public List<string> Files { get; } = [];
        public Task<BlobRef> UploadAsync(string file, CancellationToken cancellation)
        {
            Files.Add(file);
            return Task.FromResult(new BlobRef(new string('a', 64), new FileInfo(file).Length));
        }
    }

    static async Task<JsonElement> Call(HttpClient client, string url, string method, string? parametersJson = null)
    {
        var parameters = parametersJson == null ? "" : $",\"params\":{parametersJson}";
        var response = await client.PostAsync(url, new StringContent($"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"{method}\"{parameters}}}", Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("result");
    }

    static string Text(JsonElement result) => result.GetProperty("content")[0].GetProperty("text").GetString()!;

    [Fact]
    public async Task ToolsReachThePeopleAndStayInsideTheWorkspace()
    {
        var root = Directory.CreateTempSubdirectory("bosofa-mcp-").FullName;
        var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
        await File.WriteAllTextAsync(Path.Combine(work, "report.txt"), "hello");
        await File.WriteAllTextAsync(Path.Combine(root, "outside.txt"), "secret");
        if (!OperatingSystem.IsWindows()) File.CreateSymbolicLink(Path.Combine(work, "link.txt"), Path.Combine(root, "outside.txt"));
        try
        {
            var sink = new Sink();
            var uploader = new Uploader();
            var state = new SessionState { SessionId = 7, WorkingDirectory = work };
            await using var endpoint = new McpEndpoint(id => id == 7 ? state : null, sink, uploader);
            var server = endpoint.ServerFor(7);
            using var anonymous = new HttpClient();
            var refused = await anonymous.PostAsync(server.Url, new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            using var other = new HttpClient();
            other.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(endpoint.ServerFor(8).Secrets["Authorization"]);
            Assert.Equal(HttpStatusCode.Unauthorized, (await other.PostAsync(server.Url, new StringContent("{}"))).StatusCode); // another session's bearer

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(server.Secrets["Authorization"]);
            Assert.Equal("2025-11-25", (await Call(client, server.Url!, "initialize", "{\"protocolVersion\":\"2025-11-25\"}")).GetProperty("protocolVersion").GetString());
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync(server.Url, new StringContent("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"))).StatusCode);
            Assert.Equal(3, (await Call(client, server.Url!, "tools/list")).GetProperty("tools").GetArrayLength());

            await Call(client, server.Url!, "tools/call", "{\"name\":\"" + McpEndpoint.NotifyTool + "\",\"arguments\":{\"message\":\"half way\"}}");
            Assert.Contains(sink.Reports, report => report is ItemReport { Kind: SessionItemKind.Notice, Text: "half way" });

            var asking = Call(client, server.Url!, "tools/call", "{\"name\":\"" + McpEndpoint.AskTool + "\",\"arguments\":{\"question\":\"Ship it?\"}}");
            while (!sink.Reports.OfType<ApprovalRequestedReport>().Any()) await Task.Delay(10);
            var question = sink.Reports.OfType<ApprovalRequestedReport>().Single();
            Assert.Equal("Ship it?", question.Description);
            Assert.True(endpoint.TryAnswer(question.Key, true, "after lunch"));
            Assert.Equal("The person answered yes. They added: after lunch", Text(await asking.WaitAsync(TimeSpan.FromSeconds(10))));
            Assert.False(endpoint.TryAnswer("not-ours", true, null));

            string Upload(string path) => "{\"name\":\"" + McpEndpoint.UploadTool + "\",\"arguments\":{\"path\":" + JsonSerializer.Serialize(path) + "}}";
            Assert.Equal("Uploaded report.txt.", Text(await Call(client, server.Url!, "tools/call", Upload("report.txt"))));
            Assert.Equal(Path.Combine(work, "report.txt"), Assert.Single(uploader.Files));
            Assert.Contains(sink.Reports, report => report is ItemReport { Data: not null } item && item.Data.Contains(new string('a', 64)));
            foreach (var escape in new[] { "../outside.txt", Path.Combine(root, "outside.txt") })
                Assert.True((await Call(client, server.Url!, "tools/call", Upload(escape))).GetProperty("isError").GetBoolean());
            if (!OperatingSystem.IsWindows())
                Assert.True((await Call(client, server.Url!, "tools/call", Upload("link.txt"))).GetProperty("isError").GetBoolean());
            Assert.Single(uploader.Files);
        }
        finally { Directory.Delete(root, true); }
    }
    static async Task<JsonDocument> Post(HttpClient client, string url, string json)
    {
        var response = await client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    static HttpClient Bearer(ResolvedMcpServer entry)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(entry.Secrets["Authorization"]);
        return client;
    }

    /// <summary>A harness gets the gateway's address and the session's bearer; the daemon starts the server with its secret.</summary>
    [Fact]
    public async Task AStdioServerOfTheProfileIsServedByTheDaemonWithItsSecret()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("bosofa-gateway-").FullName;
        var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
        try
        {
            await using var endpoint = new McpEndpoint(_ => null, new Sink(), null);
            var real = new ResolvedMcpServer("local", McpTransport.Stdio, "/bin/bash", [AgentToolHostTest.WriteFakeMcpServer(root)], null,
                new() { ["MCP_SECRET"] = "s3cret-of-the-server" });
            var entries = endpoint.ServersFor(7, work, [real]);
            Assert.Equal([McpEndpoint.ServerName, "local"], entries.Select(entry => entry.Name));
            var local = entries[1];
            Assert.Equal((McpTransport.Http, $"{entries[0].Url}/local"), (local.Transport, local.Url));
            Assert.Equal(entries[0].Secrets, local.Secrets); // the session's bearer, nothing of the server
            Assert.DoesNotContain("s3cret", McpConfig.ForClaude(entries));

            using var anonymous = new HttpClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(local.Url, new StringContent("{}"))).StatusCode);
            using var client = Bearer(local);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(entries[0].Url + "/other", new StringContent("{}"))).StatusCode);

            // Claude Code probes for the stateless protocol first; the gateway answers it, so the harness falls back to initialize at once.
            using (var probe = await Post(client, local.Url!, """{"jsonrpc":"2.0","id":"p","method":"server/discover","params":{}}"""))
                Assert.Equal(-32601, probe.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            using (var initialized = await Post(client, local.Url!, """{"jsonrpc":"2.0","id":"a","method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{}}}"""))
            {
                Assert.Equal("a", initialized.RootElement.GetProperty("id").GetString());
                Assert.Equal("fake", initialized.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString()); // the server's own answer
            }
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync(local.Url, new StringContent("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""))).StatusCode);
            using (var tools = await Post(client, local.Url!, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""))
                Assert.Equal("where", tools.RootElement.GetProperty("result").GetProperty("tools")[0].GetProperty("name").GetString());
            using (var called = await Post(client, local.Url!, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"where","arguments":{}}}"""))
            {
                Assert.Equal(3, called.RootElement.GetProperty("id").GetInt32());
                Assert.Equal("in work with s3cret-of-the-server", Text(called.RootElement.GetProperty("result")));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>A remote server gets its own headers from the daemon in place of the session's bearer, and its event stream as it is.</summary>
    [Fact]
    public async Task ARemoteServerOfTheProfileIsCalledByTheDaemonWithItsHeaders()
    {
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        using var upstream = new HttpListener();
        upstream.Prefixes.Add($"http://localhost:{port}/");
        upstream.Start();
        var seen = new ConcurrentQueue<(string? Authorization, string? Session, string Body)>();
        var serving = Task.Run(async () =>
        {
            while (upstream.IsListening)
            {
                HttpListenerContext context;
                try { context = await upstream.GetContextAsync(); }
                catch (Exception) { return; }
                var body = await new StreamReader(context.Request.InputStream).ReadToEndAsync();
                seen.Enqueue((context.Request.Headers["Authorization"], context.Request.Headers["Mcp-Session-Id"], body));
                if (context.Request.Headers["Authorization"] != "Bearer remote-secret-token")
                {
                    context.Response.StatusCode = 401;
                    context.Response.Headers["WWW-Authenticate"] = "Bearer resource_metadata=\"x\"";
                }
                else
                {
                    context.Response.ContentType = "text/event-stream";
                    context.Response.Headers["Mcp-Session-Id"] = "remote-session";
                    var bytes = Encoding.UTF8.GetBytes("event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\n\n");
                    await context.Response.OutputStream.WriteAsync(bytes);
                }
                context.Response.Close();
            }
        });
        try
        {
            await using var endpoint = new McpEndpoint(_ => null, new Sink(), null);
            var entries = endpoint.ServersFor(7, Path.GetTempPath(), [new("remote", McpTransport.Http, null, [], $"http://localhost:{port}/mcp",
                new() { ["Authorization"] = "Bearer remote-secret-token" })]);
            Assert.DoesNotContain("remote-secret-token", McpConfig.ForClaude(entries));
            using var client = Bearer(entries[1]);
            using var message = new HttpRequestMessage(HttpMethod.Post, entries[1].Url)
            {
                Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json")
            };
            message.Headers.Add("Mcp-Session-Id", "remote-session");
            using var response = await client.SendAsync(message);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("remote-session", Assert.Single(response.Headers.GetValues("Mcp-Session-Id")));
            Assert.Contains("\"result\":{}", await response.Content.ReadAsStringAsync());
            var (authorization, session, body) = Assert.Single(seen);
            Assert.Equal(("Bearer remote-secret-token", "remote-session"), (authorization, session)); // never the session's own bearer
            Assert.Contains("\"ping\"", body);

            // Another server's 401 stays a 401, without its sign-in challenge, which would point the harness at the daemon.
            var wrong = endpoint.ServersFor(8, Path.GetTempPath(), [new("remote", McpTransport.Http, null, [], $"http://localhost:{port}/mcp", [])]);
            using var other = Bearer(wrong[1]);
            using var refused = await other.PostAsync(wrong[1].Url, new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.False(refused.Headers.Contains("WWW-Authenticate"));
        }
        finally
        {
            upstream.Stop();
            await serving;
        }
    }
}
