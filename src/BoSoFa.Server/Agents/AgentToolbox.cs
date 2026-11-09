using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Agents;

/// <summary>Who runs a tool, and so what it needs from a person (docs/server-agent.md §6.5).</summary>
public enum AgentToolKind
{
    /// <summary>Not offered to this session: the model made the name up or the permission mode hides it.</summary>
    Unknown,

    /// <summary>A note in the transcript; needs nobody.</summary>
    Notify,

    /// <summary>A yes-or-no question: the person's answer is the tool's result.</summary>
    Ask,

    /// <summary>A tool of an MCP server, which needs approval unless the mode bypasses it.</summary>
    Mcp
}

/// <summary>The tools of one session's loop: BoSoFa's own, and those of its HTTP MCP servers, as <c>mcp__server__tool</c>.</summary>
public sealed partial class AgentToolbox
{
    public const string NotifyTool = "notify_user", AskTool = "ask_user";
    const int LongestToolName = 64; // what the model APIs accept

    static readonly ToolSpec[] Own =
    [
        new(NotifyTool, "Tell the person following this session something, without waiting for an answer.", Schema("message", "What to say.")),
        new(AskTool, "Ask the person following this session a yes-or-no question and wait for the answer.", Schema("question", "The question."))
    ];

    readonly List<ToolSpec> _mcpSpecs = [];
    readonly Dictionary<string, (McpHttpClient Client, string Tool)> _mcp = [];

    static JsonObject Schema(string property, string description) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { [property] = new JsonObject { ["type"] = "string", ["description"] = description } },
        ["required"] = new JsonArray(property)
    };

    /// <summary>Connects to every server. One that fails is left out and named in the returned problems: the rest still work.</summary>
    public static async Task<(AgentToolbox Toolbox, List<string> Problems)> LoadAsync(HttpClient http, IEnumerable<ResolvedMcpServer> servers,
        CancellationToken cancellation)
    {
        var toolbox = new AgentToolbox();
        var problems = new List<string>();
        foreach (var server in servers)
        {
            var client = new McpHttpClient(http, server.Name, new Uri(server.Url!), server.Secrets);
            try
            {
                foreach (var tool in await client.ListToolsAsync(cancellation))
                {
                    var name = NameOf(server.Name, tool.Name);
                    if (!toolbox._mcp.TryAdd(name, (client, tool.Name))) continue;
                    toolbox._mcpSpecs.Add(new(name, tool.Description, tool.InputSchema));
                }
            }
            catch (Exception problem) when (problem is McpException or HttpRequestException or System.Text.Json.JsonException)
            {
                problems.Add($"The MCP server {server.Name} is not available: {problem.Message}");
            }
        }
        return (toolbox, problems);
    }

    /// <summary>The name as the model sees it, within what the APIs allow.</summary>
    static string NameOf(string server, string tool)
    {
        var name = NotAllowed().Replace($"mcp__{server}__{tool}", "_");
        return name.Length <= LongestToolName ? name : name[..LongestToolName];
    }

    /// <summary>In <c>plan</c> mode only tools that change nothing are offered.</summary>
    public IReadOnlyList<ToolSpec> SpecsFor(string permissionMode) => permissionMode == "plan" ? Own : [..Own, .._mcpSpecs];

    public AgentToolKind KindOf(string name, string permissionMode) => name switch
    {
        NotifyTool => AgentToolKind.Notify,
        AskTool => AgentToolKind.Ask,
        _ when permissionMode != "plan" && _mcp.ContainsKey(name) => AgentToolKind.Mcp,
        _ => AgentToolKind.Unknown
    };

    public static bool NeedsApproval(AgentToolKind kind, string permissionMode) =>
        kind == AgentToolKind.Ask || (kind == AgentToolKind.Mcp && permissionMode != "bypassPermissions");

    public Task<McpToolResult> CallMcpAsync(string name, JsonObject arguments, CancellationToken cancellation) =>
        _mcp[name].Client.CallToolAsync(_mcp[name].Tool, arguments, cancellation);

    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex NotAllowed();
}
