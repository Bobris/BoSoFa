using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using BoSoFa.Daemon.Harnesses;
using BoSoFa.Protocol;
using ToolNames = BoSoFa.Protocol.MachineTools;

namespace BoSoFa.Daemon.Tools;

/// <summary>Runs a command in the system shell: bash or sh, or PowerShell on Windows. The whole process tree ends with the call.</summary>
sealed class ShellTool(Action<string> stream)
{
    const int StreamLimit = 256 * 1024;
    static readonly TimeSpan StreamEvery = TimeSpan.FromMilliseconds(200), PipesGrace = TimeSpan.FromSeconds(2);

    // Build tools of a developer's machine; nothing with a credential in it.
    static readonly string[] DeveloperVariables =
    [
        "DOTNET_ROOT", "JAVA_HOME", "GOPATH", "GOROOT", "CARGO_HOME", "RUSTUP_HOME", "NVM_DIR", "PYENV_ROOT", "VIRTUAL_ENV", "ANDROID_HOME", "XDG_CONFIG_HOME",
        "XDG_CACHE_HOME", "XDG_DATA_HOME", "SSH_AUTH_SOCK"
    ];

    readonly object _lock = new();
    readonly StringBuilder _head = new(), _tail = new(), _pending = new();
    long _total;
    long _streamed;

    public static (string Executable, string[] Arguments) Shell(string command)
    {
        if (OperatingSystem.IsWindows())
            return (HarnessEnvironment.FindExecutable(null, ["pwsh.exe"]) ?? "powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command]);
        return (File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh", ["-c", command]);
    }

    /// <param name="environment">The profile's variables, resolved; the shell's own settings below win over them.</param>
    public async Task<ToolRunResult> RunAsync(string workingDirectory, ToolArguments arguments, int timeoutSeconds, CancellationToken cancellation,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var command = arguments.Required("command");
        var (executable, shellArguments) = Shell(command);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var argument in shellArguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var (name, value) in HarnessEnvironment.Clean(DeveloperVariables)) start.Environment[name] = value;
        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) start.Environment[name] = value;
        start.Environment["TERM"] = "dumb";
        start.Environment["NO_COLOR"] = "1";
        start.Environment["PAGER"] = "cat";
        start.Environment["GIT_PAGER"] = "cat";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";

        Process process;
        try { process = Process.Start(start) ?? throw new ToolProblem("The shell did not start."); }
        catch (Win32Exception problem) { throw new ToolProblem($"The shell did not start: {problem.Message}"); }
        using (process)
        {
            process.StandardInput.Close(); // a command that reads its input gets end of file instead of hanging
            var readers = Task.WhenAll(PumpAsync(process.StandardOutput.BaseStream), PumpAsync(process.StandardError.BaseStream));
            using var flushing = new CancellationTokenSource();
            var flusher = FlushAsync(flushing.Token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var timedOut = false;
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                timedOut = !cancellation.IsCancellationRequested;
                Kill(process);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            // A child left in the background can hold the pipes open for ever.
            await Task.WhenAny(readers, Task.Delay(PipesGrace, CancellationToken.None));
            await flushing.CancelAsync();
            await flusher;
            Flush();
            cancellation.ThrowIfCancellationRequested();
            if (timedOut) return new(Output() + $"[Stopped after {timeoutSeconds} seconds.]", true);
            var exit = process.ExitCode;
            var output = Output();
            if (exit != 0) return new(output + $"[Exit code {exit}]", true);
            return new(output.Length == 0 ? "The command printed nothing." : output, false);
        }
    }

    static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception problem) when (problem is InvalidOperationException or Win32Exception) { }
    }

    async Task PumpAsync(Stream stream)
    {
        var decoder = new UTF8Encoding(false).GetDecoder();
        var bytes = new byte[8192];
        var characters = new char[8192 + 4];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(bytes);
                if (read == 0) return;
                var count = decoder.GetChars(bytes, 0, read, characters, 0);
                Add(characters.AsSpan(0, count));
            }
        }
        catch (Exception problem) when (problem is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    /// <summary>Keeps the start and the end of the output: an error is usually at the end, and what ran at the start.</summary>
    void Add(ReadOnlySpan<char> text)
    {
        var headLimit = ToolNames.OutputLimit / 3;
        var tailLimit = ToolNames.OutputLimit - headLimit;
        lock (_lock)
        {
            _total += text.Length;
            var toHead = Math.Min(headLimit - _head.Length, text.Length);
            if (toHead > 0) _head.Append(text[..toHead]);
            _tail.Append(text[toHead..]);
            if (_tail.Length > tailLimit * 2) _tail.Remove(0, _tail.Length - tailLimit);
            if (_streamed < StreamLimit)
            {
                _pending.Append(text);
                _streamed += text.Length;
            }
        }
    }

    async Task FlushAsync(CancellationToken cancellation)
    {
        try
        {
            while (true)
            {
                await Task.Delay(StreamEvery, cancellation);
                Flush();
            }
        }
        catch (OperationCanceledException) { }
    }

    void Flush()
    {
        string text;
        lock (_lock)
        {
            if (_pending.Length == 0) return;
            text = _pending.ToString();
            _pending.Clear();
        }
        stream(text);
    }

    string Output()
    {
        lock (_lock)
        {
            var tailLimit = ToolNames.OutputLimit - ToolNames.OutputLimit / 3;
            var tail = _tail.ToString();
            var cut = _total - _head.Length - Math.Min(tail.Length, tailLimit);
            if (tail.Length > tailLimit) tail = tail[^tailLimit..];
            var text = cut > 0 ? $"{_head}\n[{cut} characters of output were cut]\n{tail}" : _head + tail;
            return text.Length > 0 && !text.EndsWith('\n') ? text + "\n" : text;
        }
    }
}
