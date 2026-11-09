using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BoSoFa.Server.Tests;

/// <summary>A model provider and an HTTP MCP server on a loopback port, so a real server (or a cluster of processes) runs the agent
/// loop against them. The model answers from a script; each step can stop half way, like a node dying mid-call.</summary>
sealed class FakeAgentBackend : IAsyncDisposable
{
    readonly WebApplication _app;
    readonly ConcurrentQueue<Func<HttpContext, Task>> _script = new();
    readonly SemaphoreSlim _modelCalled = new(0), _toolStarted = new(0);

    public ConcurrentQueue<JsonObject> ModelRequests { get; } = new();
    public ConcurrentQueue<string> ModelAuthorizations { get; } = new();
    public ConcurrentQueue<string> ToolCalls { get; } = new();
    public ConcurrentQueue<string> McpAuthorizations { get; } = new();

    /// <summary>When set, a tool call waits for it before answering.</summary>
    public TaskCompletionSource? HoldTool { get; set; }

    FakeAgentBackend(WebApplication app) => _app = app;

    public string ResponsesUrl => Address + "/v1/responses";
    public string McpUrl => Address + "/mcp";
    string Address => _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    public static async Task<FakeAgentBackend> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(["--urls", "http://127.0.0.1:0"]);
        var app = builder.Build();
        var backend = new FakeAgentBackend(app);
        app.MapPost("/v1/responses", backend.ModelAsync);
        app.MapPost("/mcp", backend.McpAsync);
        await app.StartAsync();
        return backend;
    }

    public void Answer(string text) => _script.Enqueue(http => Completed(http, [Message(text)]));

    public void Call(string id, string name, string arguments) => _script.Enqueue(http => Completed(http, [Function(id, name, arguments)]));

    public void Reasoned(string encrypted, string id, string name, string arguments) => _script.Enqueue(http => Completed(http,
    [
        new JsonObject { ["type"] = "reasoning", ["id"] = "rs_1", ["summary"] = new JsonArray(), ["encrypted_content"] = encrypted },
        Function(id, name, arguments)
    ]));

    /// <summary>Streams some text, then keeps the call open until the client goes away (or <paramref name="release"/> ends it).</summary>
    public void Hang(string text, Task? release = null) => _script.Enqueue(async http =>
    {
        await StartStream(http);
        await Write(http, new JsonObject { ["type"] = "response.output_text.delta", ["delta"] = text });
        // Keeps streaming, as a long answer does, so a client that joins late still sees some of it.
        try
        {
            while (release is not { IsCompleted: true })
            {
                await Task.Delay(100, http.RequestAborted);
                await Write(http, new JsonObject { ["type"] = "response.output_text.delta", ["delta"] = "." });
            }
        }
        catch (Exception problem) when (problem is OperationCanceledException or IOException) { return; }
        await Completed(http, [Message(text)], false);
    });

    public void Fail(int status, string code, string message) => _script.Enqueue(async http =>
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString());
    });

    public async Task WaitForModelCallAsync() => await _modelCalled.WaitAsync(TimeSpan.FromSeconds(60));
    public async Task WaitForToolAsync() => await _toolStarted.WaitAsync(TimeSpan.FromSeconds(60));

    static JsonObject Message(string text) => new()
    {
        ["type"] = "message", ["id"] = "msg_1", ["status"] = "completed", ["role"] = "assistant",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text })
    };

    static JsonObject Function(string id, string name, string arguments) => new()
    {
        ["type"] = "function_call", ["id"] = "fc_" + id, ["status"] = "completed", ["call_id"] = id, ["name"] = name, ["arguments"] = arguments
    };

    static async Task StartStream(HttpContext http)
    {
        http.Response.ContentType = "text/event-stream";
        await http.Response.StartAsync();
    }

    static async Task Write(HttpContext http, JsonObject data)
    {
        await http.Response.WriteAsync($"data: {data.ToJsonString()}\n\n", http.RequestAborted);
        await http.Response.Body.FlushAsync(http.RequestAborted);
    }

    static async Task Completed(HttpContext http, JsonObject[] output, bool start = true)
    {
        if (start) await StartStream(http);
        foreach (var item in output)
            if (item["type"]?.GetValue<string>() == "message")
                await Write(http, new JsonObject { ["type"] = "response.output_text.delta", ["delta"] = item["content"]![0]!["text"]!.GetValue<string>() });
        await Write(http, new JsonObject
        {
            ["type"] = "response.completed",
            ["response"] = new JsonObject
            {
                ["status"] = "completed", ["output"] = new JsonArray(output.Select(item => (JsonNode)item.DeepClone()).ToArray()),
                ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 }
            }
        });
    }

    async Task ModelAsync(HttpContext http)
    {
        using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
        ModelRequests.Enqueue((JsonNode.Parse(await reader.ReadToEndAsync()) as JsonObject)!);
        ModelAuthorizations.Enqueue(http.Request.Headers.Authorization.ToString());
        _modelCalled.Release();
        if (_script.TryDequeue(out var step)) await step(http);
        else await Completed(http, [Message("The script ended.")]);
    }

    async Task McpAsync(HttpContext http)
    {
        using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
        var request = (JsonNode.Parse(await reader.ReadToEndAsync()) as JsonObject)!;
        McpAuthorizations.Enqueue(http.Request.Headers["X-Secret"].ToString());
        var method = request["method"]!.GetValue<string>();
        JsonNode? result = null;
        switch (method)
        {
            case "initialize":
                http.Response.Headers["Mcp-Session-Id"] = "mcp-session";
                result = new JsonObject { ["protocolVersion"] = "2025-11-25", ["capabilities"] = new JsonObject(), ["serverInfo"] = new JsonObject { ["name"] = "fake" } };
                break;
            case "notifications/initialized":
                http.Response.StatusCode = StatusCodes.Status202Accepted;
                return;
            case "tools/list":
                result = new JsonObject
                {
                    ["tools"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "lookup", ["description"] = "Looks something up.",
                        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["q"] = new JsonObject { ["type"] = "string" } } }
                    })
                };
                break;
            case "tools/call":
                ToolCalls.Enqueue(request["params"]!["arguments"]!.ToJsonString());
                _toolStarted.Release();
                if (HoldTool is { } hold)
                    try { await hold.Task.WaitAsync(http.RequestAborted); }
                    catch (OperationCanceledException) { return; }
                result = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "The answer is 42." }) };
                break;
        }
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = result }.ToJsonString());
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
