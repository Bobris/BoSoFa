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
    public string MessagesUrl => Address + "/v1/messages";
    public string GoBaseUrl => Address + "/go";
    public ConcurrentQueue<string> ModelPaths { get; } = new();
    public ConcurrentQueue<string> GoSessions { get; } = new();
    public ConcurrentQueue<string> GoModelListAuthorizations { get; } = new();
    public string McpUrl => Address + "/mcp";
    string Address => _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    public static async Task<FakeAgentBackend> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(["--urls", "http://127.0.0.1:0"]);
        var app = builder.Build();
        var backend = new FakeAgentBackend(app);
        app.MapPost("/v1/responses", backend.ModelAsync);
        app.MapPost("/v1/messages", backend.ModelAsync);
        app.MapPost("/go/openai/v1/responses", backend.ModelAsync);
        app.MapPost("/go/anthropic/v1/messages", backend.ModelAsync);
        app.MapGet("/go/v1/models", backend.GoModelsAsync);
        app.MapGet("/v1/models", (HttpContext http) =>
        {
            if (backend.ModelListFails)
            {
                http.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return Task.CompletedTask;
            }
            http.Response.ContentType = "application/json";
            return http.Response.WriteAsync(new JsonObject
            {
                ["data"] = new JsonArray(new[] { "gpt-5.5", "claude-opus-5-5", "text-embedding-9", "no-such-model" }.Select(id => (JsonNode)new JsonObject { ["id"] = id }).ToArray())
            }.ToJsonString());
        });
        app.MapGet("/page", (HttpContext http) =>
        {
            http.Response.ContentType = "text/html; charset=utf-8";
            return http.Response.WriteAsync("<html><head><title>x</title><script>evil()</script></head><body><h1>Big news</h1><p>Hello &amp; <a href=\"/more\">more</a></p></body></html>");
        });
        app.MapGet("/redirect", (HttpContext http) =>
        {
            http.Response.Redirect("/page");
            return Task.CompletedTask;
        });
        app.MapPost("/mcp", backend.McpAsync);
        await app.StartAsync();
        return backend;
    }

    public void Answer(string text) => _script.Enqueue(http => Completed(http, [Message(text)]));

    public void Call(string id, string name, string arguments, long inputTokens = 10) => _script.Enqueue(http => Completed(http, [Function(id, name, arguments)], inputTokens: inputTokens));

    /// <summary>A hosted web search and the answer that used it, as one response.</summary>
    public void SearchedAndAnswered(string query, string text) => _script.Enqueue(http => Completed(http,
    [
        new JsonObject { ["type"] = "web_search_call", ["id"] = "ws_1", ["status"] = "completed", ["action"] = new JsonObject { ["type"] = "search", ["query"] = query } },
        Message(text)
    ]));

    /// <summary>What the next Messages API answers say came from the cache.</summary>
    public long AnthropicCacheRead { get; set; } = 4;

    /// <summary>When set, the providers' model lists fail.</summary>
    public bool ModelListFails { get; set; }

    public string PageUrl => Address + "/page";
    public string RedirectUrl => Address + "/redirect";

    /// <summary>A Messages API step that searched and paused; the same call goes on from there.</summary>
    public void AnthropicSearchPaused(string query) => _script.Enqueue(http => AnthropicStream(http, "pause_turn",
        Block(0, new JsonObject { ["type"] = "server_tool_use", ["id"] = "srvtoolu_1", ["name"] = "web_search", ["input"] = new JsonObject() }),
        Delta(0, new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = new JsonObject { ["query"] = query }.ToJsonString() }),
        Block(1, new JsonObject { ["type"] = "web_search_tool_result", ["tool_use_id"] = "srvtoolu_1", ["content"] = new JsonArray(new JsonObject { ["type"] = "web_search_result", ["url"] = "https://example.com", ["encrypted_content"] = "opaque" }) }),
        Block(2, new JsonObject { ["type"] = "server_tool_use", ["id"] = "srvtoolu_2", ["name"] = "code_execution", ["input"] = new JsonObject(), ["caller"] = new JsonObject { ["type"] = "direct" } }),
        Delta(2, new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = "{\"code\":\"print(1)\"}" }),
        Block(3, new JsonObject { ["type"] = "code_execution_tool_result", ["tool_use_id"] = "srvtoolu_2", ["content"] = new JsonObject { ["stdout"] = "1" } })));

    /// <summary>A Messages API answer with a citation.</summary>
    public void AnthropicCitedAnswer(string text) => _script.Enqueue(http => AnthropicStream(http, "end_turn",
        Block(0, new JsonObject { ["type"] = "text", ["text"] = "" }), Delta(0, new JsonObject { ["type"] = "text_delta", ["text"] = text }),
        Delta(0, new JsonObject { ["type"] = "citations_delta", ["citation"] = new JsonObject { ["type"] = "web_search_result_location", ["url"] = "https://example.com" } })));

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

    static JsonObject Block(int index, JsonObject block) => new() { ["type"] = "content_block_start", ["index"] = index, ["content_block"] = block };
    static JsonObject Delta(int index, JsonObject delta) => new() { ["type"] = "content_block_delta", ["index"] = index, ["delta"] = delta };

    Task AnthropicStream(HttpContext http, string stopReason, params JsonObject[] events) => Task.Run(async () =>
    {
        await StartStream(http);
        await WriteEvent(http, new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject { ["usage"] = new JsonObject { ["input_tokens"] = 10, ["cache_read_input_tokens"] = AnthropicCacheRead, ["output_tokens"] = 1 } } });
        foreach (var item in events) await WriteEvent(http, item);
        var delta = new JsonObject { ["stop_reason"] = stopReason };
        if (stopReason == "refusal") delta["stop_details"] = new JsonObject { ["type"] = "refusal", ["category"] = "cyber", ["explanation"] = "It looks like an attack." };
        await WriteEvent(http, new JsonObject { ["type"] = "message_delta", ["delta"] = delta, ["usage"] = new JsonObject { ["output_tokens"] = 5 } });
        await WriteEvent(http, new JsonObject { ["type"] = "message_stop" });
    });

    static async Task WriteEvent(HttpContext http, JsonObject data)
    {
        await http.Response.WriteAsync($"event: {data["type"]}\ndata: {data.ToJsonString()}\n\n", http.RequestAborted);
        await http.Response.Body.FlushAsync(http.RequestAborted);
    }

    /// <summary>A Messages API answer in text.</summary>
    public void AnthropicAnswer(string text) => _script.Enqueue(http => AnthropicStream(http, "end_turn",
        Block(0, new JsonObject { ["type"] = "text", ["text"] = "" }), Delta(0, new JsonObject { ["type"] = "text_delta", ["text"] = text })));

    /// <summary>A Messages API step: signed thinking, then a tool call whose input arrives in two pieces.</summary>
    public void AnthropicThinkAndCall(string thinking, string signature, string id, string name, string arguments) => _script.Enqueue(http => AnthropicStream(http, "tool_use",
        Block(0, new JsonObject { ["type"] = "thinking", ["thinking"] = "" }),
        Delta(0, new JsonObject { ["type"] = "thinking_delta", ["thinking"] = thinking }),
        Delta(0, new JsonObject { ["type"] = "signature_delta", ["signature"] = signature }),
        Block(1, new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = new JsonObject() }),
        Delta(1, new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = arguments[..(arguments.Length / 2)] }),
        Delta(1, new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = arguments[(arguments.Length / 2)..] })));

    public void AnthropicRefuse() => _script.Enqueue(http => AnthropicStream(http, "refusal"));

    /// <summary>A Messages API step cut off while it was writing a tool call, which must not run.</summary>
    public void AnthropicCutOffCall(string id, string name, string arguments) => _script.Enqueue(http => AnthropicStream(http, "max_tokens",
        Block(0, new JsonObject { ["type"] = "text", ["text"] = "" }), Delta(0, new JsonObject { ["type"] = "text_delta", ["text"] = "Writing it." }),
        Block(1, new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = new JsonObject() }),
        Delta(1, new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = arguments })));

    /// <summary>A Messages API error body, as a refused signature gets.</summary>
    public void AnthropicFail(int status, string type, string message) => _script.Enqueue(async http =>
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = type, ["message"] = message } }.ToJsonString());
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

    static async Task Completed(HttpContext http, JsonObject[] output, bool start = true, long inputTokens = 10)
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
                ["usage"] = new JsonObject { ["input_tokens"] = inputTokens, ["output_tokens"] = 5 }
            }
        });
    }

    async Task ModelAsync(HttpContext http)
    {
        using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
        ModelRequests.Enqueue((JsonNode.Parse(await reader.ReadToEndAsync()) as JsonObject)!);
        ModelPaths.Enqueue(http.Request.Path);
        if (http.Request.Headers.TryGetValue("x-opencode-session", out var goSession)) GoSessions.Enqueue(goSession.ToString());
        ModelAuthorizations.Enqueue(http.Request.Headers.TryGetValue("x-api-key", out var apiKey) ? apiKey.ToString() : http.Request.Headers.Authorization.ToString());
        _modelCalled.Release();
        if (_script.TryDequeue(out var step)) await step(http);
        else await Completed(http, [Message("The script ended.")]);
    }

    Task GoModelsAsync(HttpContext http)
    {
        GoModelListAuthorizations.Enqueue(http.Request.Headers.Authorization.ToString());
        static JsonObject Model(string id, string name, string type, bool tools) => new()
        {
            ["id"] = id, ["name"] = name, ["type"] = type, ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["supported"] = tools }, ["reasoning"] = new JsonObject { ["supported"] = true, ["effort"] = new JsonArray("low", "high") } },
            ["limit"] = new JsonObject { ["context"] = 1000000, ["output"] = 384000 }
        };
        http.Response.ContentType = "application/json";
        return http.Response.WriteAsync(new JsonObject
        {
            ["object"] = "list",
            ["data"] = new JsonArray(Model("glm-5.3", "GLM-5.3", "chat", true), Model("qwen3.8-max", "Qwen3.8 Max", "chat", true), Model("no-tools", "No Tools", "chat", false), Model("draw", "Draw", "image", true))
        }.ToJsonString());
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
