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
    Mcp,

    /// <summary>Reads the machine; needs nobody.</summary>
    MachineRead,

    /// <summary>Changes files on the machine; <c>acceptEdits</c> allows it without asking.</summary>
    MachineWrite,

    /// <summary>Runs a command on the machine, which can do anything, so only <c>bypassPermissions</c> allows it without asking.</summary>
    MachineShell
}

/// <summary>The tools of one session's loop: BoSoFa's own, those of its machine, and those of its MCP servers, as <c>mcp__server__tool</c>.</summary>
public sealed partial class AgentToolbox
{
    public const string NotifyTool = "notify_user", AskTool = "ask_user";
    const int LongestToolName = 64; // what the model APIs accept
    static readonly TimeSpan HttpToolTimeout = TimeSpan.FromMinutes(10);

    static readonly ToolSpec[] Own =
    [
        new(NotifyTool, "Tell the person following this session something, without waiting for an answer.", Schema(("message", "string", "What to say.", true))),
        new(AskTool, "Ask the person following this session a yes-or-no question and wait for the answer.", Schema(("question", "string", "The question.", true)))
    ];

    static readonly ToolSpec[] MachineRead =
    [
        new(MachineTools.ReadFile, "Read lines of a text file, numbered. Long files come in parts: continue with offset.", Schema(
            ("path", "string", "The file, relative to the working directory.", true),
            ("offset", "integer", "The first line to read, from 1.", false),
            ("limit", "integer", "How many lines to read.", false))),
        new(MachineTools.ListDir, "List a folder.", Schema(("path", "string", "The folder, relative to the working directory; by default the working directory itself.", false))),
        new(MachineTools.Glob, "Find files by name. ** crosses folders, * and ? do not, {a,b} is either. Files git ignores are left out.", Schema(
            ("pattern", "string", "For example src/**/*.cs, or *.md to find a name anywhere.", true),
            ("path", "string", "The folder to look in; by default the working directory.", false))),
        new(MachineTools.Grep, "Search file contents with a regular expression. Answers file:line:text. Files git ignores are left out.", Schema(
            ("pattern", "string", "The regular expression.", true),
            ("path", "string", "The file or folder to search; by default the working directory.", false),
            ("glob", "string", "Only files whose names match this.", false),
            ("ignore_case", "boolean", "Ignore case.", false)))
    ];

    static readonly ToolSpec WriteFile = new(MachineTools.WriteFile, "Create a file or replace all of it. Folders are created as needed.", Schema(
        ("path", "string", "The file, relative to the working directory.", true),
        ("content", "string", "The whole new content.", true)));

    static readonly ToolSpec EditFile = new(MachineTools.EditFile, "Replace exact text in a file. old_text must occur once, unless replace_all is set.", Schema(
        ("path", "string", "The file, relative to the working directory.", true),
        ("old_text", "string", "The text to replace, copied exactly, without line numbers.", true),
        ("new_text", "string", "What to put there.", true),
        ("replace_all", "boolean", "Replace every occurrence.", false)));

    static readonly ToolSpec ApplyPatch = new(MachineTools.ApplyPatch, """
        Change several files at once. Nothing is changed if any part does not fit. The format:
        *** Begin Patch
        *** Add File: path/new.txt
        +every line starts with a plus
        *** Delete File: path/old.txt
        *** Update File: path/file.txt
        *** Move to: path/renamed.txt
        @@ an optional line above the change, such as a function's signature
         a context line starts with a space
        -a removed line
        +an added line
        *** End Patch
        """, Schema(("input", "string", "The whole patch.", true)));

    static readonly ToolSpec Shell = new(MachineTools.Shell,
        "Run a command in the working directory: bash or sh, or PowerShell on Windows. Input is closed, and the output's middle is cut when it is long. Nothing stays running afterwards.", Schema(
            ("command", "string", "The command.", true),
            ("timeout_seconds", "integer", $"When to stop it, at most {MachineTools.LongestShellSeconds}. By default {MachineTools.DefaultShellSeconds}.", false)));

    readonly List<ToolSpec> _mcpSpecs = [];
    readonly Dictionary<string, (McpHttpClient? Http, ResolvedMcpServer? Stdio, string Tool)> _mcp = [];
    AgentMachine? _machine;
    bool _offersPatch;

    static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] properties)
    {
        var map = new JsonObject();
        foreach (var (name, type, description, _) in properties) map[name] = new JsonObject { ["type"] = type, ["description"] = description };
        return new()
        {
            ["type"] = "object",
            ["properties"] = map,
            ["required"] = new JsonArray(properties.Where(property => property.Required).Select(property => (JsonNode?)JsonValue.Create(property.Name)).ToArray())
        };
    }

    /// <summary>Connects to every server. One that fails is left out and named in the returned problems: the rest still work.</summary>
    /// <param name="machine">The session's machine; null: no machine tools, and stdio servers have nowhere to run.</param>
    /// <param name="offersPatch">The model was trained on <c>apply_patch</c>.</param>
    public static async Task<(AgentToolbox Toolbox, List<string> Problems)> LoadAsync(HttpClient http, IEnumerable<ResolvedMcpServer> servers, AgentMachine? machine,
        bool offersPatch, CancellationToken cancellation)
    {
        var toolbox = new AgentToolbox { _machine = machine, _offersPatch = offersPatch };
        var problems = new List<string>();
        foreach (var server in servers)
        {
            var isHttp = server.Transport == McpTransport.Http;
            if (!isHttp && machine == null) continue;
            var client = isHttp ? new McpHttpClient(http, server.Name, new Uri(server.Url!), server.Secrets) : null;
            try
            {
                foreach (var tool in client != null ? await client.ListToolsAsync(cancellation) : await machine!.ListMcpToolsAsync(server, cancellation))
                {
                    var name = NameOf(server.Name, tool.Name);
                    if (!toolbox._mcp.TryAdd(name, (client, isHttp ? null : server, tool.Name))) continue;
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

    IEnumerable<ToolSpec> ChangingTools() => _machine == null ? [] : _offersPatch ? [WriteFile, EditFile, ApplyPatch, Shell] : [WriteFile, EditFile, Shell];

    /// <summary>In <c>plan</c> mode only tools that change nothing are offered.</summary>
    public IReadOnlyList<ToolSpec> SpecsFor(string permissionMode)
    {
        IEnumerable<ToolSpec> reading = _machine == null ? Own : [..Own, ..MachineRead];
        return permissionMode == "plan" ? reading.ToList() : [..reading, ..ChangingTools(), .._mcpSpecs];
    }

    public AgentToolKind KindOf(string name, string permissionMode)
    {
        if (name == NotifyTool) return AgentToolKind.Notify;
        if (name == AskTool) return AgentToolKind.Ask;
        if (_machine != null && MachineRead.Any(spec => spec.Name == name)) return AgentToolKind.MachineRead;
        if (permissionMode == "plan") return AgentToolKind.Unknown;
        if (_mcp.ContainsKey(name)) return AgentToolKind.Mcp;
        if (!ChangingTools().Any(spec => spec.Name == name)) return AgentToolKind.Unknown;
        return name == MachineTools.Shell ? AgentToolKind.MachineShell : AgentToolKind.MachineWrite;
    }

    public static bool NeedsApproval(AgentToolKind kind, string permissionMode) => kind switch
    {
        AgentToolKind.Ask => true,
        AgentToolKind.Mcp or AgentToolKind.MachineShell => permissionMode != "bypassPermissions",
        AgentToolKind.MachineWrite => permissionMode is not ("acceptEdits" or "bypassPermissions"),
        _ => false
    };

    public async Task<McpToolResult> CallAsync(string name, JsonObject arguments, AgentToolCall call, CancellationToken cancellation)
    {
        if (_mcp.TryGetValue(name, out var route))
        {
            if (route.Stdio != null) return await _machine!.RunAsync(call, route.Tool, arguments.ToJsonString(), MachineTools.LongestShellSeconds, route.Stdio, cancellation);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(HttpToolTimeout);
            try { return await route.Http!.CallToolAsync(route.Tool, arguments, timeout.Token); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                return new McpToolResult($"The tool did not answer within {HttpToolTimeout.TotalMinutes:0} minutes.", true);
            }
        }
        var seconds = name != MachineTools.Shell
            ? MachineTools.FileToolSeconds
            : Math.Clamp(arguments["timeout_seconds"] is JsonValue value && value.TryGetValue<int>(out var asked) ? asked : MachineTools.DefaultShellSeconds, 1, MachineTools.LongestShellSeconds);
        return await _machine!.RunAsync(call, name, arguments.ToJsonString(), seconds, null, cancellation);
    }

    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex NotAllowed();
}
