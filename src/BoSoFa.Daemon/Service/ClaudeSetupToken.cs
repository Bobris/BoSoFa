using System.Text;
using System.Text.RegularExpressions;
using BoSoFa.Daemon.Harnesses;
using BoSoFa.Daemon.Terminals;

namespace BoSoFa.Daemon.Service;

/// <summary>
/// Makes a Claude subscription token with the official CLI (plan §5.4.1): <c>claude setup-token</c> needs a terminal and
/// prints the token once, so it runs on a pseudo-terminal with an empty config folder, which leaves the user's own login
/// alone. Only the token goes to standard output, for the desktop app that asked.
/// </summary>
public static partial class ClaudeSetupToken
{
    static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);
    static readonly TimeSpan QuietAfterToken = TimeSpan.FromMilliseconds(500);

    [GeneratedRegex(@"sk-ant-oat\d+-[A-Za-z0-9_-]+")]
    public static partial Regex TokenPattern();

    [GeneratedRegex(@"\x1B(\[[0-9;?]*[ -/]*[@-~]|\][^\x07\x1B]*(\x07|\x1B\\)|[@-Z\\-_])")]
    public static partial Regex AnsiPattern();

    /// <summary>The longest token in what the terminal showed: it may arrive in pieces.</summary>
    public static string? FindToken(string terminalOutput) =>
        TokenPattern().Matches(AnsiPattern().Replace(terminalOutput, "")).Select(match => match.Value).OrderByDescending(token => token.Length).FirstOrDefault();

    public static async Task<int> RunAsync(string? configured, TextWriter output, TextWriter error, CancellationToken cancellation)
    {
        var claude = HarnessEnvironment.FindExecutable(configured, ["claude", "claude.exe", "claude.cmd"]);
        if (claude == null)
        {
            error.WriteLine("Claude Code is not installed on this computer.");
            return 3;
        }
        var config = Directory.CreateTempSubdirectory("bosofa-claude-").FullName;
        var environment = HarnessEnvironment.Clean();
        environment["CLAUDE_CONFIG_DIR"] = config;
        environment["TERM"] = "xterm-256color";
        try
        {
            // As wide as a terminal gets: a line wrapped inside the token could not be matched.
            using var pty = Pty.Start(claude, ["setup-token"], Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), environment, 1000, 50);
            var text = new StringBuilder();
            var lastOutput = DateTime.UtcNow;
            var finished = false;
            var reader = new Thread(() =>
            {
                var buffer = new byte[16 * 1024];
                int count;
                while ((count = pty.Read(buffer)) > 0)
                    lock (text)
                    {
                        text.Append(Encoding.UTF8.GetString(buffer, 0, count));
                        lastOutput = DateTime.UtcNow;
                    }
                Volatile.Write(ref finished, true);
            }) { IsBackground = true };
            reader.Start();
            var exit = pty.WaitForExitAsync();
            var deadline = DateTime.UtcNow + Limit;
            while (DateTime.UtcNow < deadline && !cancellation.IsCancellationRequested)
            {
                await Task.Delay(100, CancellationToken.None);
                string? token;
                bool quiet;
                lock (text)
                {
                    token = FindToken(text.ToString());
                    quiet = DateTime.UtcNow - lastOutput > QuietAfterToken;
                }
                if (token != null && (quiet || Volatile.Read(ref finished)))
                {
                    pty.Terminate();
                    output.WriteLine(token);
                    return 0;
                }
                if (exit.IsCompleted)
                {
                    await Task.WhenAny(Task.Delay(300, CancellationToken.None));
                    pty.StopReading();
                    lock (text)
                        if (FindToken(text.ToString()) is { } last)
                        {
                            output.WriteLine(last);
                            return 0;
                        }
                    error.WriteLine("Claude Code ended without a token.");
                    return 1;
                }
            }
            pty.Terminate();
            error.WriteLine(cancellation.IsCancellationRequested ? "Stopped." : "No token came in time.");
            return 1;
        }
        finally
        {
            try { Directory.Delete(config, true); }
            catch (IOException) { }
        }
    }
}
