using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BoSoFa.Daemon.Harnesses.Claude;

// Lines the daemon writes to `claude -p --input-format stream-json` (docs/spikes/04-claude-stream-json.md). Inbound
// lines are read as JsonElement: the CLI adds fields often, and only a few of them matter here.

/// <summary>A user turn: <c>{"type":"user","message":{"role":"user","content":...}}</c>.</summary>
sealed record UserLine<T>(UserMessageBody<T> Message)
{
    public string Type => "user";

    /// <summary>Ignored by the CLI, which keeps its own session ID, but expected in the shape.</summary>
    public string SessionId => "";

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ParentToolUseId => null;
}

sealed record UserMessageBody<T>(T Content)
{
    public string Role => "user";
}

/// <summary>Content of a user turn with images; a turn with only text is a plain string.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(ImageBlock), "image")]
abstract record ContentBlock;

sealed record TextBlock(string Text) : ContentBlock;

sealed record ImageBlock(ImageSource Source) : ContentBlock;

sealed record ImageSource(string MediaType, string Data)
{
    public string Type => "base64";
}

/// <summary><c>{"type":"control_request","request_id":"..","request":{"subtype":..}}</c>.</summary>
sealed record ControlRequestLine<T>(string RequestId, T Request)
{
    public string Type => "control_request";
}

/// <summary>The handshake. Its own fields are camelCase (the TS SDK options), unlike the envelope.</summary>
sealed record InitializeRequest([property: JsonPropertyName("appendSystemPrompt")] string? AppendSystemPrompt = null)
{
    public string Subtype => "initialize";
}

sealed record InterruptRequest
{
    public string Subtype => "interrupt";
}

/// <summary><c>{"type":"control_response","response":{"subtype":"success","request_id":..,"response":{..}}}</c>.</summary>
sealed record ControlResponseLine(ControlResponseBody Response)
{
    public string Type => "control_response";
}

sealed record ControlResponseBody(string Subtype, string RequestId, JsonElement? Response, string? Error);

/// <summary>Answer to <c>can_use_tool</c>, camelCase like the TS SDK's PermissionResult. An allow must echo the input.</summary>
sealed record PermissionResult(
    string Behavior,
    [property: JsonPropertyName("updatedInput")] JsonElement? UpdatedInput = null,
    string? Message = null);

sealed record EmptyObject;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UserLine<string>))]
[JsonSerializable(typeof(UserLine<ContentBlock[]>))]
[JsonSerializable(typeof(ControlRequestLine<InitializeRequest>))]
[JsonSerializable(typeof(ControlRequestLine<InterruptRequest>))]
[JsonSerializable(typeof(ControlRequestLine<JsonObject>))]
[JsonSerializable(typeof(ControlResponseLine))]
[JsonSerializable(typeof(PermissionResult))]
[JsonSerializable(typeof(EmptyObject))]
sealed partial class ClaudeJson : JsonSerializerContext
{
    public static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement.Clone();

    public static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static JsonElement? Obj(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    public static long Long(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : 0;

    public static bool Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;
}
