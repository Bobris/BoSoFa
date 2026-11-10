using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoSoFa.Daemon.Harnesses;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon.Tools;

/// <summary>The stdio MCP servers of server agent sessions, one process per session and server. The server names what to start in
/// every call, so a daemon that restarted simply starts them again; those not used for a while are stopped.</summary>
sealed class StdioMcpServers(ToolImages images) : IAsyncDisposable
{
    const string SupportedVersion = "2025-11-25";
    static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60), Idle = TimeSpan.FromMinutes(15);

    sealed class Running(string signature)
    {
        public string Signature { get; } = signature;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public JsonRpcProcess? Process { get; set; }
        public Task Consumer { get; set; } = Task.CompletedTask;
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
        public bool Alive => Process != null && !Consumer.IsCompleted;
    }

    readonly ConcurrentDictionary<(ulong Session, string Server), Running> _servers = new();

    static string SignatureOf(string workingDirectory, ResolvedMcpServer server) =>
        JsonSerializer.Serialize(new[] { workingDirectory, server.Command ?? "", string.Join('\0', server.Args), string.Join('\0', server.Secrets.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value)) },
            ProtocolJsonContext.Default.StringArray);

    async Task<JsonRpcProcess> ProcessOfAsync(ulong sessionId, string workingDirectory, ResolvedMcpServer server, CancellationToken cancellation)
    {
        var key = (sessionId, server.Name);
        var signature = SignatureOf(workingDirectory, server);
        var running = _servers.GetOrAdd(key, _ => new(signature));
        if (running.Signature != signature)
        {
            _servers.TryRemove(new(key, running));
            await StopAsync(running);
            running = _servers.GetOrAdd(key, _ => new(signature));
        }
        await running.Gate.WaitAsync(cancellation);
        try
        {
            running.LastUsed = DateTime.UtcNow;
            if (running.Alive) return running.Process!;
            if (running.Process != null) await running.Process.DisposeAsync();
            running.Process = null;
            var process = Launch(workingDirectory, server);
            running.Consumer = ConsumeAsync(process);
            try
            {
                await process.RequestAsync("initialize", new JsonObject
                {
                    ["protocolVersion"] = SupportedVersion,
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = Brand.Id, ["version"] = Cli.Version }
                }, StartTimeout, cancellation);
                await process.NotifyAsync("notifications/initialized", new JsonObject());
            }
            catch (Exception problem) when (problem is IOException or TimeoutException or JsonRpcException)
            {
                var tail = process.StandardErrorTail;
                await process.DisposeAsync();
                throw new ToolProblem($"The MCP server {server.Name} did not start: {problem.Message}{(tail.Length > 0 ? "\n" + tail : "")}");
            }
            catch
            {
                await process.DisposeAsync();
                throw;
            }
            running.Process = process;
            return process;
        }
        finally { running.Gate.Release(); }
    }

    static JsonRpcProcess Launch(string workingDirectory, ResolvedMcpServer server)
    {
        var environment = HarnessEnvironment.Clean();
        foreach (var (name, value) in server.Secrets) environment[name] = value;
        var command = server.Command ?? throw new ToolProblem($"The MCP server {server.Name} has no command.");
        try
        {
            // npx and friends are batch files on Windows, which only a shell can start.
            return OperatingSystem.IsWindows()
                ? JsonRpcProcess.Start("cmd.exe", ["/d", "/c", command, ..server.Args], workingDirectory, environment, includeVersion: true)
                : JsonRpcProcess.Start(command, server.Args, workingDirectory, environment, includeVersion: true);
        }
        catch (System.ComponentModel.Win32Exception problem) { throw new ToolProblem($"The MCP server {server.Name} did not start: {problem.Message}"); }
    }

    /// <summary>Nothing to do with its notifications; its requests are refused, except that a ping is answered.</summary>
    static async Task ConsumeAsync(JsonRpcProcess process)
    {
        process.OnRequest = request => request.Method == "ping"
            ? Task.FromResult<JsonNode?>(new JsonObject())
            : throw new JsonRpcException(-32601, $"{request.Method} is not supported.");
        await foreach (var message in process.Incoming.ReadAllAsync())
            if (message.Id != null) process.Serve(message);
    }

    public async Task<McpToolsResult> ListAsync(McpToolsRequest request, CancellationToken cancellation)
    {
        var process = await ProcessOfAsync(request.SessionId, request.WorkingDirectory, request.Server, cancellation);
        var tools = new List<McpToolInfo>();
        string? cursor = null;
        do
        {
            var parameters = new JsonObject();
            if (cursor != null) parameters["cursor"] = cursor;
            var result = await process.RequestAsync("tools/list", parameters, StartTimeout, cancellation);
            if (result.ValueKind != JsonValueKind.Object) break;
            if (result.TryGetProperty("tools", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var tool in list.EnumerateArray())
                    if (Text(tool, "name") is { Length: > 0 } name)
                        tools.Add(new(name, Text(tool, "description") ?? "",
                            tool.TryGetProperty("inputSchema", out var schema) && schema.ValueKind == JsonValueKind.Object ? schema.GetRawText() : "{\"type\":\"object\"}"));
            cursor = Text(result, "nextCursor");
        } while (cursor is { Length: > 0 });
        return new(tools.ToArray());
    }

    public async Task<ToolRunResult> CallAsync(ToolRunRequest request, CancellationToken cancellation)
    {
        var server = request.McpServer!;
        var process = await ProcessOfAsync(request.SessionId, request.WorkingDirectory, server, cancellation);
        JsonNode? arguments;
        try { arguments = JsonNode.Parse(request.Arguments.Length == 0 ? "{}" : request.Arguments); }
        catch (JsonException problem) { throw new ToolProblem($"The arguments are not valid JSON: {problem.Message}"); }
        JsonElement result;
        try
        {
            result = await process.RequestAsync("tools/call", new JsonObject { ["name"] = request.Tool, ["arguments"] = arguments },
                TimeSpan.FromSeconds(request.TimeoutSeconds), cancellation);
        }
        catch (JsonRpcException problem) { return new($"{server.Name}: {problem.Message}", true); }
        catch (TimeoutException) { return new($"The tool did not answer within {request.TimeoutSeconds} seconds.", true); }
        catch (IOException) { return new($"The MCP server {server.Name} exited during the call.", true); }
        var content = McpResults.Read(result.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(result.GetRawText()));
        return await images.ResultAsync(content.Text, content.IsError, content.Images, cancellation);
    }

    public async Task StopIdleAsync()
    {
        var limit = DateTime.UtcNow - Idle;
        foreach (var (key, running) in _servers)
            if (running.LastUsed < limit && _servers.TryRemove(new(key, running))) await StopAsync(running);
    }

    static async Task StopAsync(Running running)
    {
        await running.Gate.WaitAsync();
        try
        {
            if (running.Process != null) await running.Process.DisposeAsync();
            running.Process = null;
        }
        finally { running.Gate.Release(); }
    }

    static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public async ValueTask DisposeAsync()
    {
        var running = _servers.Values.ToArray();
        _servers.Clear();
        await Task.WhenAll(running.Select(StopAsync));
    }
}
