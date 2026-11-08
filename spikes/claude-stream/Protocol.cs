using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeStreamSpike;

// Outgoing lines (host -> CLI). Inbound lines are read with JsonDocument and, where the shape is stable enough, bound
// to typed records through the same source-generated context.

/// <summary>A user turn: <c>{"type":"user","message":{"role":"user","content":...}}</c>.</summary>
internal sealed record UserLine(string SessionId, UserMessageBody Message)
{
    public string Type => "user";

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ParentToolUseId => null;
}

internal sealed record UserMessageBody(string Content)
{
    public string Role => "user";
}

/// <summary><c>{"type":"control_request","request_id":"..","request":{"subtype":..}}</c>.</summary>
internal sealed record ControlRequestLine<T>(string RequestId, T Request)
{
    public string Type => "control_request";
}

// The initialize request's own fields are camelCase (they mirror the TS SDK options), unlike the envelope.
internal sealed record InitializeRequest(
    Dictionary<string, HookMatcher[]>? Hooks,
    [property: JsonPropertyName("sdkMcpServers")] string[]? SdkMcpServers,
    [property: JsonPropertyName("appendSystemPrompt")] string? AppendSystemPrompt)
{
    public string Subtype => "initialize";
}

internal sealed record HookMatcher(
    string? Matcher,
    [property: JsonPropertyName("hookCallbackIds")] string[] HookCallbackIds,
    int? Timeout);

internal sealed record InterruptRequest
{
    public string Subtype => "interrupt";
}

/// <summary><c>{"type":"control_response","response":{"subtype":"success","request_id":..,"response":{..}}}</c>.</summary>
internal sealed record ControlResponseLine(ControlResponseBody Response)
{
    public string Type => "control_response";
}

internal sealed record ControlResponseBody(string Subtype, string RequestId, JsonElement? Response, string? Error);

/// <summary>Answer to <c>can_use_tool</c>.</summary>
// camelCase too: these are the TS SDK's PermissionResult fields.
internal sealed record PermissionResult(
    string Behavior,
    [property: JsonPropertyName("updatedInput")] JsonElement? UpdatedInput = null,
    [property: JsonPropertyName("updatedPermissions")] JsonElement? UpdatedPermissions = null,
    string? Message = null,
    bool? Interrupt = null);

/// <summary>Answer to <c>mcp_message</c>: the JSON-RPC response travels inside <c>mcp_response</c>.</summary>
internal sealed record McpMessageResult(JsonRpcResponse McpResponse);

internal sealed record JsonRpcResponse(JsonElement? Id, JsonElement? Result, JsonRpcError? Error)
{
    public string Jsonrpc => "2.0";
}

internal sealed record JsonRpcError(int Code, string Message);

internal sealed record McpInitializeResult(string ProtocolVersion, McpCapabilities Capabilities, McpServerInfo ServerInfo);
internal sealed record McpCapabilities(McpToolsCapability Tools);
internal sealed record McpToolsCapability(bool ListChanged);
internal sealed record McpServerInfo(string Name, string Version);
internal sealed record McpToolsList(McpTool[] Tools);
internal sealed record McpTool(string Name, string Description, JsonElement InputSchema);
internal sealed record McpCallResult(McpContent[] Content, bool IsError);
internal sealed record McpContent(string Type, string Text);
internal sealed record EmptyObject;

// Inbound, typed: the result line ends every turn.
internal sealed record ResultLine(
    string Type,
    string Subtype,
    bool IsError,
    long DurationMs,
    long DurationApiMs,
    int NumTurns,
    string? Result,
    string SessionId,
    double TotalCostUsd,
    ResultUsage? Usage,
    string? StopReason,
    JsonElement? PermissionDenials);

internal sealed record ResultUsage(
    long InputTokens,
    long OutputTokens,
    long CacheCreationInputTokens,
    long CacheReadInputTokens);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UserLine))]
[JsonSerializable(typeof(ControlRequestLine<InitializeRequest>))]
[JsonSerializable(typeof(ControlRequestLine<InterruptRequest>))]
[JsonSerializable(typeof(ControlResponseLine))]
[JsonSerializable(typeof(PermissionResult))]
[JsonSerializable(typeof(McpMessageResult))]
[JsonSerializable(typeof(McpInitializeResult))]
[JsonSerializable(typeof(McpToolsList))]
[JsonSerializable(typeof(McpCallResult))]
[JsonSerializable(typeof(EmptyObject))]
[JsonSerializable(typeof(ResultLine))]
internal sealed partial class SpikeJson : JsonSerializerContext;

// MCP field names are camelCase, unlike the CLI's snake_case control fields.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(McpInitializeResult))]
[JsonSerializable(typeof(McpToolsList))]
[JsonSerializable(typeof(McpCallResult))]
[JsonSerializable(typeof(EmptyObject))]
internal sealed partial class McpJson : JsonSerializerContext;
