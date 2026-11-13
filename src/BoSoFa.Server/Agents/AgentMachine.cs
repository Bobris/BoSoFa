using System.Text.Json;
using System.Text.Json.Nodes;
using BoSoFa.Protocol;
using BoSoFa.Server.Machines;

namespace BoSoFa.Server.Agents;

/// <summary>Identifies one tool call on the machine, and where its output streams.</summary>
public sealed record AgentToolCall(string Key, string LiveRef, int RunNumber);

/// <summary>The computer a session's tools run on (docs/server-agent.md §6.1). A call that was under way when the connection dropped is
/// asked for again: the daemon joins it by its key, so it does not run twice.</summary>
/// <param name="environment">The profile's variables, resolved, for the commands of the shell tool; null for none.</param>
/// <param name="mask">The secrets among the session's values, which the daemon masks in what it sends back.</param>
public sealed class AgentMachine(MachineRpcClient rpc, ulong machineId, string workingDirectory, ulong sessionId, TimeProvider time,
    IReadOnlyDictionary<string, string>? environment = null, string[]? mask = null)
{
    static readonly TimeSpan ReconnectGrace = TimeSpan.FromSeconds(60), RetryEvery = TimeSpan.FromSeconds(1), CancelTimeout = TimeSpan.FromSeconds(5);

    string CheckpointRef(int run, string phase) => $"refs/{Brand.Id}/{sessionId}/agent-{run}-{phase}";

    public async Task<McpToolResult> RunAsync(AgentToolCall call, string tool, string arguments, int timeoutSeconds, ResolvedMcpServer? server, CancellationToken cancellation)
    {
        var isRetry = false;
        DateTimeOffset? lostAt = null;
        try
        {
            while (true)
            {
                var checkpoint = server == null && MachineTools.Changes(tool) ? CheckpointRef(call.RunNumber, "before") : null;
                var answer = await rpc.CallAsync(machineId, new ToolRunRequest(call.Key, sessionId, workingDirectory, tool, arguments, timeoutSeconds, call.LiveRef, isRetry,
                    checkpoint, server, server == null && tool == MachineTools.Shell && environment is { Count: > 0 } ? new(environment) : null,
                    mask is { Length: > 0 } ? mask : null), cancellation);
                switch (answer)
                {
                    case ToolRunResult result:
                        return new(result.Text, result.IsError, result.Images);
                    case RpcFailure failure when MachineRpcClient.IsOffline(failure) && !isRetry:
                        return new(failure.Message + " The tool did not run.", true);
                    case RpcFailure failure when MachineRpcClient.IsOffline(failure) || MachineRpcClient.IsDisconnected(failure):
                        lostAt ??= time.GetUtcNow();
                        if (time.GetUtcNow() - lostAt > ReconnectGrace)
                            return new("The machine went offline while this ran; whether it took effect is unknown.", true);
                        isRetry = true;
                        await Task.Delay(RetryEvery, time, cancellation);
                        break;
                    case RpcFailure failure:
                        await CancelAsync(call.Key); // it did not answer in time, and must not go on unseen
                        return new(failure.Message, true);
                    default:
                        return new("The machine gave an answer that makes no sense.", true);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await CancelAsync(call.Key);
            throw;
        }
    }

    async Task CancelAsync(string key)
    {
        using var timeout = new CancellationTokenSource(CancelTimeout);
        try { await rpc.CallAsync(machineId, new ToolCancelRequest(key), timeout.Token); }
        catch (OperationCanceledException) { /* the daemon's own timeout ends it */ }
    }

    /// <summary>The project's instructions, or why they could not be read; all null when it has none.</summary>
    public async Task<(string? File, string? Text, string? Problem)> ProjectInstructionsAsync(CancellationToken cancellation)
    {
        var result = await RunAsync(new($"{sessionId}:project", "tool-project", 0), MachineTools.ProjectInstructions, "{}", 30, null, cancellation);
        if (result.IsError) return (null, null, result.Text);
        var split = result.Text.IndexOf('\n');
        return split < 0 ? (null, null, null) : (result.Text[..split], result.Text[(split + 1)..], null);
    }

    /// <summary>What this run changed; null when nothing did or the machine cannot say.</summary>
    public async Task<AgentItem?> DiffAsync(int run, CancellationToken cancellation) =>
        await rpc.CallAsync(machineId, new ToolDiffRequest(workingDirectory, CheckpointRef(run, "before"), CheckpointRef(run, "after")), cancellation)
            is ToolDiffResult { Patch: { } patch } diff
            ? new AgentItem(SessionItemKind.Diff, patch, Data: diff.Data)
            : null;

    public async Task<IReadOnlyList<McpRemoteTool>> ListMcpToolsAsync(ResolvedMcpServer server, CancellationToken cancellation)
    {
        switch (await rpc.CallAsync(machineId, new McpToolsRequest(sessionId, workingDirectory, server), cancellation))
        {
            case McpToolsResult result:
                return result.Tools.Select(tool => new McpRemoteTool(tool.Name, tool.Description, SchemaOf(tool.InputSchema))).ToList();
            case RpcFailure failure:
                throw new McpException(failure.Message);
            default:
                throw new McpException("The machine gave an answer that makes no sense.");
        }
    }

    static JsonObject SchemaOf(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject { ["type"] = "object" }; }
        catch (JsonException) { return new JsonObject { ["type"] = "object" }; }
    }
}
