using System.Runtime.InteropServices;
using System.Reflection;
using BoSoFa.Daemon;

// Ctrl+C and service managers (SIGTERM from systemd and launchd, the Windows console close) stop the daemon gracefully.
using var stop = new CancellationTokenSource();
void Stop(PosixSignalContext context)
{
    context.Cancel = true;
    stop.Cancel();
}
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
using var quit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, Stop);
return await Cli.RunAsync(args, Console.Out, Console.Error, stop.Token);

namespace BoSoFa.Daemon
{
    public static class Cli
    {
        public static string Version { get; } =
            typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        const string Usage = """
            Usage: bosofad <command> [options]

              enroll --server <url> [--name <name>]   Join a BoSoFa server; a signed-in person approves the shown code.
              run [--claude <path>] [--codex <path>]  Stay connected to the server and run its sessions (foreground).
                  [--roots <dir[:dir…]>]              Folders people may browse, besides the sessions' working directories.
                  [--replay-fixture <file>]           Also offer the replay harness, playing this recorded transcript.
              status [--server <url>]                 Check that the server answers and speaks this protocol version.
              version                                 Print the daemon version.

            Options: --data-dir <dir>   Where the daemon keeps its identity (default: the per-user data directory).
            """;

        public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
            CancellationToken cancellation = default)
        {
            if (args is [] or ["help"] or ["--help"])
            {
                output.WriteLine(Usage);
                return 0;
            }
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 1; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                {
                    error.WriteLine($"Unexpected argument '{args[i]}'.\n\n{Usage}");
                    return 2;
                }
                options[args[i][2..]] = args[++i];
            }
            var store = new DaemonStore(options.GetValueOrDefault("data-dir") ?? DaemonStore.DefaultDirectory());
            try
            {
                switch (args[0])
                {
                    case "version" or "--version":
                        output.WriteLine($"bosofad {Version} (protocol {Protocol.ProtocolVersion.Current})");
                        return 0;
                    case "status":
                        var server = options.GetValueOrDefault("server") is { } url ? new Uri(url) : store.LoadState()?.Server;
                        if (server == null)
                        {
                            error.WriteLine("Not enrolled; pass --server <url>.");
                            return 2;
                        }
                        return await StatusAsync(server, output, error, cancellation);
                    case "enroll" when options.TryGetValue("server", out var enrollServer):
                        return await EnrollAsync(store, new Uri(enrollServer),
                            options.GetValueOrDefault("name") ?? Environment.MachineName, output, error, cancellation);
                    case "run":
                        return await RunLinkAsync(store, new(options.GetValueOrDefault("claude"),
                            options.GetValueOrDefault("replay-fixture"), CodexExecutable: options.GetValueOrDefault("codex"),
                            Roots: options.GetValueOrDefault("roots")?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)),
                            output, error, cancellation);
                    default:
                        error.WriteLine(Usage);
                        return 2;
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return 130;
            }
        }

        static async Task<int> StatusAsync(Uri server, TextWriter output, TextWriter error, CancellationToken cancellation)
        {
            using var client = new ServerClient(server);
            try
            {
                var info = await client.GetVersionAsync(cancellation);
                output.WriteLine($"{server}: {info.Component} {info.Version}, protocol {info.ProtocolVersion}");
                if (info.ProtocolVersion == Protocol.ProtocolVersion.Current) return 0;
                error.WriteLine($"Protocol mismatch: this daemon speaks {Protocol.ProtocolVersion.Current}.");
                return 3;
            }
            catch (HttpRequestException exception)
            {
                error.WriteLine($"{server}: unreachable ({exception.Message})");
                return 1;
            }
        }

        static async Task<int> EnrollAsync(DaemonStore store, Uri server, string name, TextWriter output, TextWriter error,
            CancellationToken cancellation)
        {
            if (store.LoadState() is { } existing)
            {
                error.WriteLine($"Already enrolled as machine {existing.MachineId} on {existing.Server} ({store.Directory}).");
                return 1;
            }
            using var key = store.LoadOrCreateKey();
            using var client = new ServerClient(server);
            var started = await client.StartEnrollmentAsync(new(name, DaemonStore.OsName(), DaemonStore.ArchName(), Version,
                DaemonStore.PublicKey(key)), cancellation);
            output.WriteLine($"To add this machine, open {started.VerificationUri}");
            output.WriteLine($"and approve the code {started.UserCode} (valid until {started.ExpiresAt.ToLocalTime():t}).");
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(started.PollIntervalSeconds), cancellation);
                var poll = await client.PollEnrollmentAsync(started.EnrollmentId, cancellation);
                switch (poll.Status)
                {
                    case Protocol.EnrollStatus.Approved:
                        store.SaveState(new(server, poll.MachineId!.Value));
                        output.WriteLine($"Approved: this is machine {poll.MachineId} on {server}. Start it with 'bosofad run'.");
                        return 0;
                    case Protocol.EnrollStatus.Expired:
                        error.WriteLine("The code expired before anyone approved it; run enroll again.");
                        return 1;
                }
            }
        }

        static async Task<int> RunLinkAsync(DaemonStore store, Sessions.HarnessOptions harnesses, TextWriter output,
            TextWriter error, CancellationToken cancellation)
        {
            if (store.LoadState() is not { } state)
            {
                error.WriteLine($"Not enrolled ({store.Directory}); run 'bosofad enroll --server <url>' first.");
                return 2;
            }
            using var key = store.LoadOrCreateKey();
            using var outbox = new Outbox(Path.Combine(store.Directory, "outbox"));
            var leaser = new ServerAccountLeaser(state, key);
            await using var host = new Sessions.SessionHost(store.Directory, outbox,
                harnesses with { Accounts = leaser, Profiles = leaser, Uploader = leaser }, output);
            try
            {
                await new DaemonLink(state, key, output, outbox, host).RunAsync(cancellation);
                return 0;
            }
            catch (UnauthorizedAccessException exception)
            {
                error.WriteLine(exception.Message);
                return 1;
            }
        }
    }
}
