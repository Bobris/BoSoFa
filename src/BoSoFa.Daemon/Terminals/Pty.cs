namespace BoSoFa.Daemon.Terminals;

/// <summary>A shell running on a pseudo-terminal.</summary>
public interface IPty : IDisposable
{
    /// <summary>Blocks for output; 0 once there will be no more.</summary>
    int Read(Span<byte> buffer);

    /// <summary>Blocks until written; a terminal whose shell is gone swallows the input.</summary>
    void Write(ReadOnlySpan<byte> data);

    void Resize(int cols, int rows);

    /// <summary>Completes with the shell's exit code.</summary>
    Task<int> WaitForExitAsync();

    /// <summary>Makes <see cref="Read"/> return 0, even if a background process still holds the terminal.</summary>
    void StopReading();

    /// <summary>Asks the shell to end, and forces it after a grace period.</summary>
    void Terminate();
}

public static class Pty
{
    public static IPty Start(string shell, string[] arguments, string workingDirectory, Dictionary<string, string> environment, int cols, int rows) =>
        OperatingSystem.IsWindows()
            ? new WindowsPty(shell, workingDirectory, environment, cols, rows)
            : new UnixPty(shell, arguments, workingDirectory, environment, cols, rows);

    /// <summary>The user's shell, and the arguments that make it a login shell.</summary>
    public static (string Shell, string[] Arguments) DefaultShell()
    {
        if (OperatingSystem.IsWindows()) return (Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe", []);
        var shell = Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } configured && File.Exists(configured)
            ? configured
            : File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
        return (shell, ["-l"]);
    }
}
