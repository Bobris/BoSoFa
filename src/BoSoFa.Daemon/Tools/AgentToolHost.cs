using System.Collections.Concurrent;
using System.Text.Json;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;
using ToolNames = BoSoFa.Protocol.MachineTools;

namespace BoSoFa.Daemon.Tools;

/// <summary>
/// Runs the tools of server agent sessions on this machine (docs/server-agent.md §6.1). A tool outlives the connection that asked
/// for it: a request sent again after a dropped connection finds it by its key and joins it, or gets the result it left.
/// </summary>
public sealed class AgentToolHost : IAsyncDisposable
{
    static readonly TimeSpan Remembered = TimeSpan.FromMinutes(15), Sweep = TimeSpan.FromMinutes(1);
    const string LostAcrossRestart = "This daemon restarted while the tool ran, so whether it took effect is unknown.";

    sealed class Call(CancellationTokenSource cancel)
    {
        public CancellationTokenSource Cancel { get; } = cancel;
        public Task<ToolRunResult> Result { get; set; } = null!;
        public DateTime? EndedAt { get; set; }
    }

    readonly ConcurrentDictionary<string, Call> _calls = new();
    readonly ConcurrentDictionary<string, byte> _directories = new();
    readonly object _start = new();
    readonly StdioMcpServers _mcp = new();
    readonly ISessionSink _sink;
    readonly CancellationTokenSource _stopping = new();
    readonly Task _sweeper;
    readonly bool _useRipgrep;

    /// <param name="useRipgrep">Search with ripgrep when the machine has it.</param>
    public AgentToolHost(ISessionSink sink, bool useRipgrep = true)
    {
        _sink = sink;
        _useRipgrep = useRipgrep;
        _sweeper = SweepAsync(_stopping.Token);
    }

    /// <summary>Tools are running, so an update must wait.</summary>
    public bool IsBusy => _calls.Values.Any(call => call.EndedAt == null);

    /// <summary>The folders agent sessions worked in since this daemon started; people may browse them.</summary>
    public IEnumerable<string> WorkingDirectories => _directories.Keys;

    public async Task<MachineRpcResult> HandleAsync(ToolRpc request, CancellationToken cancellation)
    {
        try
        {
            switch (request)
            {
                case ToolRunRequest run:
                    return await Start(run).WaitAsync(cancellation);
                case ToolCancelRequest cancel:
                    if (_calls.TryGetValue(cancel.Key, out var call) && call.EndedAt == null) await call.Cancel.CancelAsync();
                    return new RpcDone();
                case ToolDiffRequest diff:
                    return await Task.Run(() => Diff(diff), cancellation);
                case McpToolsRequest list:
                    if (!Directory.Exists(list.WorkingDirectory)) return new RpcFailure(NoDirectory(list.WorkingDirectory));
                    _directories.TryAdd(list.WorkingDirectory, 0);
                    return await _mcp.ListAsync(list, cancellation);
                default:
                    return new RpcFailure("This daemon does not know that request.");
            }
        }
        catch (ToolProblem problem) { return new RpcFailure(problem.Message); }
        catch (Exception problem) when (problem is IOException or TimeoutException or JsonException or Harnesses.JsonRpcException) { return new RpcFailure(problem.Message); }
    }

    static string NoDirectory(string directory) => $"The working directory {directory} does not exist on this machine.";

    Task<ToolRunResult> Start(ToolRunRequest request)
    {
        lock (_start)
        {
            if (_calls.TryGetValue(request.Key, out var known)) return known.Result;
            if (request.IsRetry) return Task.FromResult(new ToolRunResult(LostAcrossRestart, true));
            var call = new Call(CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token));
            call.Result = Task.Run(async () =>
            {
                try { return await ExecuteAsync(request, call.Cancel.Token); }
                finally { call.EndedAt = DateTime.UtcNow; }
            }, CancellationToken.None);
            _calls[request.Key] = call;
            return call.Result;
        }
    }

    async Task<ToolRunResult> ExecuteAsync(ToolRunRequest request, CancellationToken cancellation)
    {
        var timeoutSeconds = Math.Clamp(request.TimeoutSeconds, 1, ToolNames.LongestShellSeconds);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        // The shell reports its own timeout, with the output it had by then.
        if (request.Tool != ToolNames.Shell || request.McpServer != null) timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            if (!Directory.Exists(request.WorkingDirectory)) return new(NoDirectory(request.WorkingDirectory), true);
            _directories.TryAdd(request.WorkingDirectory, 0);
            if (request.McpServer != null) return await _mcp.CallAsync(request, timeout.Token);
            using var document = JsonDocument.Parse(request.Arguments.Length == 0 ? "{}" : request.Arguments);
            var arguments = new ToolArguments(document.RootElement);
            if (request.CheckpointRef is { } checkpoint)
            {
                var workspace = new GitWorkspace(request.WorkingDirectory);
                if (!workspace.HasRef(checkpoint)) workspace.Checkpoint(checkpoint);
            }
            var files = new FileTools(request.WorkingDirectory, _useRipgrep);
            return request.Tool switch
            {
                ToolNames.ReadFile => await files.ReadAsync(arguments, timeout.Token),
                ToolNames.ListDir => files.List(arguments),
                ToolNames.Glob => files.Glob(arguments, timeout.Token),
                ToolNames.Grep => await files.GrepAsync(arguments, timeout.Token),
                ToolNames.WriteFile => await files.WriteAsync(arguments, timeout.Token),
                ToolNames.EditFile => await files.EditAsync(arguments, timeout.Token),
                ToolNames.ApplyPatch => await new ApplyPatchTool(files).RunAsync(arguments, timeout.Token),
                ToolNames.Shell => await new ShellTool(text => _sink.Delta(request.SessionId, request.LiveRef, text))
                    .RunAsync(request.WorkingDirectory, arguments, timeoutSeconds, timeout.Token),
                _ => new($"This daemon has no tool named {request.Tool}.", true)
            };
        }
        catch (ToolProblem problem) { return new(problem.Message, true); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return new($"The tool did not finish within {timeoutSeconds} seconds.", true); }
        catch (OperationCanceledException) { return new("The tool was stopped.", true); }
        catch (Exception problem) { return new(problem.Message, true); } // whatever went wrong, the model is told and the daemon carries on
    }

    static ToolDiffResult Diff(ToolDiffRequest request)
    {
        if (!Directory.Exists(request.WorkingDirectory)) return new(null, null);
        var workspace = new GitWorkspace(request.WorkingDirectory);
        if (!workspace.HasRef(request.BeforeRef) || workspace.Checkpoint(request.AfterRef) is not { } after || workspace.Diff(request.BeforeRef, after) is not { } diff)
            return new(null, null);
        return new(diff.Patch, diff.Data);
    }

    async Task SweepAsync(CancellationToken cancellation)
    {
        try
        {
            while (true)
            {
                await Task.Delay(Sweep, cancellation);
                var limit = DateTime.UtcNow - Remembered;
                foreach (var (key, call) in _calls)
                    if (call.EndedAt < limit && _calls.TryRemove(new(key, call))) call.Cancel.Dispose();
                await _mcp.StopIdleAsync();
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        await _sweeper;
        await Task.WhenAll(_calls.Values.Select(call => call.Result));
        await _mcp.DisposeAsync();
        _stopping.Dispose();
    }
}
