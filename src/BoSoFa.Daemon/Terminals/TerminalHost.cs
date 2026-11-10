using System.Collections.Concurrent;
using System.Threading.Channels;
using BoSoFa.Daemon.Harnesses;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon.Terminals;

/// <summary>
/// The terminals of this machine (plan §2): the shell lives here and keeps a capped history, so people attach, leave and
/// watch together, and it outlives a lost connection to the server. Terminals end with the daemon.
/// </summary>
public sealed class TerminalHost : IAsyncDisposable
{
    public const int HistoryBytes = 256 * 1024;
    const int MaximumRunning = 16, MaximumKept = 32;
    static readonly RpcFailure Unknown = new("No such terminal.");

    readonly ConcurrentDictionary<string, Terminal> _terminals = new();

    /// <summary>Where output goes while connected. Awaited, so a flood of output slows the shell rather than the daemon.</summary>
    public Func<DaemonMessage, Task>? Sink { get; set; }

    /// <summary>Quick and in order: called from the receive loop.</summary>
    public MachineRpcResult Handle(TerminalRpc request)
    {
        try
        {
            switch (request)
            {
                case TerminalOpenRequest open:
                    return Open(open);
                case TerminalListRequest:
                    return new TerminalListResult(_terminals.Values.OrderBy(terminal => terminal.StartedAt).Select(terminal => terminal.ToDto()).ToArray());
                case TerminalAttachRequest attach:
                    return _terminals.TryGetValue(attach.TerminalId, out var attached) ? attached.Attach() : Unknown;
                case TerminalInputRequest input:
                    if (!_terminals.TryGetValue(input.TerminalId, out var target)) return Unknown;
                    target.Send(Convert.FromBase64String(input.Data));
                    return new RpcDone();
                case TerminalResizeRequest resize:
                    if (!_terminals.TryGetValue(resize.TerminalId, out var resized)) return Unknown;
                    if (!Valid(resize.Cols, resize.Rows)) return new RpcFailure("Not a terminal size.");
                    resized.Resize(resize.Cols, resize.Rows);
                    return new RpcDone();
                case TerminalCloseRequest close:
                    if (!_terminals.TryRemove(close.TerminalId, out var closed)) return Unknown;
                    closed.Terminate();
                    return new RpcDone();
                default:
                    return new RpcFailure("This daemon does not know that request.");
            }
        }
        catch (Exception exception) when (exception is IOException or FormatException or UnauthorizedAccessException or
                                              System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new RpcFailure(exception.Message);
        }
    }

    static bool Valid(int cols, int rows) => cols is > 0 and <= 1000 && rows is > 0 and <= 1000;

    MachineRpcResult Open(TerminalOpenRequest request)
    {
        if (!Valid(request.Cols, request.Rows)) return new RpcFailure("Not a terminal size.");
        var directory = string.IsNullOrEmpty(request.WorkingDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : request.WorkingDirectory;
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) return new RpcFailure("No such folder.");
        if (_terminals.Values.Count(terminal => !terminal.Exited) >= MaximumRunning)
            return new RpcFailure("This machine already runs as many terminals as it may.");
        foreach (var old in _terminals.Values.Where(terminal => terminal.Exited).OrderBy(terminal => terminal.StartedAt)
                     .SkipLast(MaximumKept - 1).ToList())
            _terminals.TryRemove(old.Id, out _);
        var (shell, arguments) = Pty.DefaultShell();
        var environment = HarnessEnvironment.Clean();
        environment["TERM"] = "xterm-256color";
        environment["COLORTERM"] = "truecolor";
        var terminal = new Terminal(this, shell, directory, Pty.Start(shell, arguments, directory, environment, request.Cols, request.Rows));
        _terminals[terminal.Id] = terminal;
        terminal.Start();
        return new TerminalOpenResult(terminal.ToDto());
    }

    async Task PublishAsync(DaemonMessage message)
    {
        try
        {
            if (Sink is { } sink) await sink(message);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or
                                              System.Net.WebSockets.WebSocketException)
        {
            // Disconnected: whoever attaches next gets the history.
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var terminal in _terminals.Values) terminal.Terminate();
        await Task.WhenAny(Task.WhenAll(_terminals.Values.Select(terminal => terminal.Finished)), Task.Delay(TimeSpan.FromSeconds(5)));
    }

    sealed class Terminal(TerminalHost host, string shell, string directory, IPty pty)
    {
        readonly object _lock = new();
        readonly byte[] _history = new byte[HistoryBytes];
        readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>(new() { SingleReader = true });
        readonly TaskCompletionSource _readDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long _total;
        int? _exitCode;
        DateTime _lastActivityAt = DateTime.UtcNow;

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public bool Exited { get; private set; }
        public Task Finished { get; private set; } = Task.CompletedTask;

        public void Start()
        {
            new Thread(ReadLoop) { IsBackground = true, Name = "terminal-read" }.Start();
            new Thread(WriteLoop) { IsBackground = true, Name = "terminal-write" }.Start();
            Finished = WatchAsync();
        }

        public TerminalDto ToDto()
        {
            lock (_lock) return new(Id, shell, directory, StartedAt, Exited, _exitCode, _lastActivityAt);
        }

        public void Send(byte[] data) => _input.Writer.TryWrite(data);

        public void Resize(int cols, int rows) => pty.Resize(cols, rows);

        public void Terminate()
        {
            lock (_lock)
                if (Exited) return;
            pty.Terminate();
        }

        public TerminalAttachResult Attach()
        {
            lock (_lock)
            {
                var length = (int)Math.Min(_total, HistoryBytes);
                var retained = new byte[length];
                var start = (int)((_total - length) % HistoryBytes);
                var first = Math.Min(length, HistoryBytes - start);
                Array.Copy(_history, start, retained, 0, first);
                Array.Copy(_history, 0, retained, first, length - first);
                return new(_total, Convert.ToBase64String(retained), Exited, _exitCode);
            }
        }

        void ReadLoop()
        {
            var buffer = new byte[16 * 1024];
            try
            {
                int count;
                while ((count = pty.Read(buffer)) > 0)
                {
                    long offset;
                    lock (_lock)
                    {
                        offset = _total;
                        var position = (int)(_total % HistoryBytes);
                        var first = Math.Min(count, HistoryBytes - position);
                        Array.Copy(buffer, 0, _history, position, first);
                        Array.Copy(buffer, first, _history, 0, count - first);
                        _total += count;
                        _lastActivityAt = DateTime.UtcNow;
                    }
                    host.PublishAsync(new TerminalOutputMessage(Id, offset, Convert.ToBase64String(buffer, 0, count))).GetAwaiter().GetResult();
                }
            }
            finally { _readDone.TrySetResult(); }
        }

        void WriteLoop()
        {
            while (_input.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                while (_input.Reader.TryRead(out var data))
                    pty.Write(data);
        }

        async Task WatchAsync()
        {
            var code = await pty.WaitForExitAsync();
            // The last output is still on its way; a background process keeping the terminal open must not hold up the end.
            await Task.WhenAny(_readDone.Task, Task.Delay(300));
            pty.StopReading();
            await Task.WhenAny(_readDone.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            _input.Writer.TryComplete();
            lock (_lock)
            {
                Exited = true;
                _exitCode = code;
            }
            pty.Dispose();
            await host.PublishAsync(new TerminalExitedMessage(Id, code));
        }
    }
}
