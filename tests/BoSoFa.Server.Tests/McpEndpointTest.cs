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
}
