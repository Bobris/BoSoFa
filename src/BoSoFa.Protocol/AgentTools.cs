using System.Text;
using System.Text.Json.Nodes;

namespace BoSoFa.Protocol;

/// <summary>The tools of a server agent session that a daemon runs (docs/server-agent.md §6.1).</summary>
public static class MachineTools
{
    /// <summary>Not offered to models: the loop reads the project's instructions with it once, at the start of a session.</summary>
    public const string ProjectInstructions = "project_instructions";

    /// <summary>How much of a project's instructions a session takes.</summary>
    public const int ProjectInstructionsLimit = 32 * 1024;

    /// <summary>Shares a file of the machine with the people following the session; the result is an <see cref="UploadedFile"/>.</summary>
    public const string UploadFile = "upload_file";

    public const string ReadFile = "read_file", ListDir = "list_dir", Glob = "glob", Grep = "grep", WriteFile = "write_file", EditFile = "edit_file",
        ApplyPatch = "apply_patch", Shell = "shell";

    /// <summary>The longest a model may let a command run.</summary>
    public const int LongestShellSeconds = 600;

    public const int DefaultShellSeconds = 120;

    /// <summary>The other machine tools should be instant.</summary>
    public const int FileToolSeconds = 60;

    /// <summary>What a tool's output may be, so that nothing important is lost to the transcript's own limit.</summary>
    public const int OutputLimit = ItemReport.InlineLimit - 1024;

    /// <summary>Tools that may change files, so a run with one of them gets a diff.</summary>
    public static bool Changes(string tool) => tool is WriteFile or EditFile or ApplyPatch or Shell;
}

public sealed record UploadedFile(string Name, string Sha256, long Size);

/// <summary>Calls about a server agent's tools. They take as long as they take, so the daemon serves them on their own tasks.</summary>
public abstract record ToolRpc : MachineRpc;

/// <summary>Runs one tool. A call sent again with the same <c>Key</c> joins the first one or gets its result, and runs nothing.</summary>
/// <param name="Key">Identifies this call on the machine for a while after it ended.</param>
/// <param name="LiveRef">The ref of streamed output, and later of the result's item.</param>
/// <param name="IsRetry">An earlier attempt may have started it: a daemon that does not know the key must not run it.</param>
/// <param name="CheckpointRef">Before running, commit the working tree under this hidden ref, unless it exists.</param>
/// <param name="McpServer">The call is a tool of this stdio MCP server, started if needed.</param>
public sealed record ToolRunRequest(string Key, ulong SessionId, string WorkingDirectory, string Tool, string Arguments, int TimeoutSeconds,
    string LiveRef, bool IsRetry = false, string? CheckpointRef = null, ResolvedMcpServer? McpServer = null) : ToolRpc;

/// <summary>Stops a running call; its answer says so. Nothing happens for a call that ended.</summary>
public sealed record ToolCancelRequest(string Key) : ToolRpc;

/// <summary>What changed since the checkpoint <c>BeforeRef</c>; the tree as it is now is kept under <c>AfterRef</c>.</summary>
public sealed record ToolDiffRequest(string WorkingDirectory, string BeforeRef, string AfterRef) : ToolRpc;

/// <summary>The tools of a stdio MCP server, which is started in the session's working directory if needed.</summary>
public sealed record McpToolsRequest(ulong SessionId, string WorkingDirectory, ResolvedMcpServer Server) : ToolRpc;

public sealed record ToolRunResult(string Text, bool IsError) : MachineRpcResult;

/// <summary>Both are null when nothing changed or the folder is not a repository. <c>Data</c> is the JSON of a <c>Diff</c> item.</summary>
public sealed record ToolDiffResult(string? Patch, string? Data) : MachineRpcResult;

public sealed record McpToolInfo(string Name, string Description, string InputSchema);

public sealed record McpToolsResult(McpToolInfo[] Tools) : MachineRpcResult;

/// <summary>Reads what an MCP server answered to <c>tools/call</c>, whichever transport brought it.</summary>
public static class McpResults
{
    public static (string Text, bool IsError) Read(JsonNode? result)
    {
        var text = new StringBuilder();
        foreach (var part in (result?["content"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (text.Length > 0) text.Append('\n');
            text.Append(Str(part["type"]) switch
            {
                "text" => Str(part["text"]),
                "resource" => Str(part["resource"]?["text"]) ?? $"[resource {Str(part["resource"]?["uri"])}]",
                "resource_link" => $"[resource {Str(part["uri"])}]",
                var other => $"[{other} content, not shown to the model]"
            });
        }
        if (text.Length == 0 && result?["structuredContent"] is { } structured) text.Append(structured.ToJsonString());
        return (text.ToString(), result?["isError"] is JsonValue flag && flag.TryGetValue<bool>(out var isError) && isError);
    }

    static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
