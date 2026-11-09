using System.Runtime.InteropServices;
using System.Reflection;
using BoSoFa.Daemon;
using BoSoFa.Daemon.Terminals;

// Before anything else of the runtime is set up: this process is about to become a terminal's shell.
if (args is [UnixPty.HelperCommand, _, _, ..]) return UnixPty.Exec(args);

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
// Under the launcher, a closed standard input means stop; it also ends this process should the launcher die.
if (Environment.GetEnvironmentVariable(BoSoFa.Daemon.Service.Launcher.LaunchedVariable) == "1")
    _ = Task.Run(() =>
    {
        using var input = Console.OpenStandardInput();
        var buffer = new byte[64];
        while (input.Read(buffer, 0, buffer.Length) > 0) { }
        stop.Cancel();
    });
return await Cli.RunAsync(args, Console.Out, Console.Error, stop.Token);

namespace BoSoFa.Daemon
{
    public static class Cli
    {
        public static string Version { get; } = Environment.GetEnvironmentVariable(Service.Launcher.VersionVariable) ??
            typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        const string Usage = """
            Usage: bosofad <command> [options]

              enroll --server <url> [--name <name>]   Join a BoSoFa server; a signed-in person approves the shown code.
              run [--claude <path>] [--codex <path>]  Stay connected to the server and run its sessions (foreground).
                  [--roots <dir[:dir…]>]              Folders people may browse, besides the sessions' working directories.
                  [--transport auto|websocket|longpoll]  How to reach the server (default auto: long-poll if WebSockets are refused).
                  [--replay-fixture <file>]           Also offer the replay harness, playing this recorded transcript.
              install [--scope user|system] [--service-user <name>] [--dry-run]
                                                      Start with the machine (system) or at sign-in (user, the default).
              uninstall [--scope user|system] [--dry-run]  Remove the service; the machine's identity stays.
              doctor                                  Check what this daemon needs.
              release-keygen --out <file>             Create the release signing key; prints the public key builds embed.
              release-publish --server <url> [--key <file>] --version <v> --artifacts <rid=path,…> [--channel stable|beta] [--product daemon|desktop]
                                                      Sign and publish a release (token in BOSOFA_TOKEN; without --key,
                                                      the key's PEM text in BOSOFA_RELEASE_KEY).
              status [--server <url>]                 Check that the server answers and speaks this protocol version.
              version                                 Print the daemon version.

            Options: --data-dir <dir>   Where the daemon keeps its identity (default: the per-user data directory).
            """;

        /// <summary>The public key this build checks releases against (<c>-p:BoSoFaReleasePublicKey</c>); null refuses every update.</summary>
        static string? ReleaseKey => typeof(Cli).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BoSoFaReleasePublicKey")?.Value;

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
                if (args[i] == "--dry-run")
                {
                    options["dry-run"] = "true";
                    continue;
                }
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
                    case "install":
                        return Service.ServiceCommands.Install(store, options, output, error);
                    case "uninstall":
                        return Service.ServiceCommands.Uninstall(store, options, output, error);
                    case "launcher":
                        return await Service.ServiceCommands.LauncherAsync(store, options, output, cancellation);
                    case "release-keygen" when options.TryGetValue("out", out var keyFile):
                        return Service.ReleaseCommands.KeyGen(keyFile, output, error);
                    case "release-publish" when options.ContainsKey("server") && options.ContainsKey("version") && options.ContainsKey("artifacts"):
                        return await Service.ReleaseCommands.PublishAsync(new Uri(options["server"]), options.GetValueOrDefault("key"), ReleaseKey,
                            options.GetValueOrDefault("product") ?? Protocol.Releases.Daemon, options["version"],
                            options.GetValueOrDefault("channel") ?? Protocol.Releases.Stable, options["artifacts"], output, error, cancellation);
                    case "claude-setup-token":
                        return await Service.ClaudeSetupToken.RunAsync(options.GetValueOrDefault("claude"), output, error, cancellation);
                    case "doctor":
                        return Service.ServiceCommands.Doctor(store, options, output);
                    case "run":
                        return await RunLinkAsync(store, new(options.GetValueOrDefault("claude"),
                            options.GetValueOrDefault("replay-fixture"), CodexExecutable: options.GetValueOrDefault("codex"),
                            Roots: options.GetValueOrDefault("roots")?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries),
                            ReleaseKey: ReleaseKey,
                            Launched: Environment.GetEnvironmentVariable(Service.Launcher.LaunchedVariable) == "1"),
                            options.GetValueOrDefault("transport") switch
                            {
                                "websocket" => DaemonTransport.WebSocket,
                                "longpoll" => DaemonTransport.LongPoll,
                                _ => DaemonTransport.Auto
                            }, output, error, cancellation);
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
                if (existing.Server == server)
                {
                    // An install script may run again on a machine that already belongs to this server.
                    output.WriteLine($"Already enrolled as machine {existing.MachineId} on {existing.Server}.");
                    return 0;
                }
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

        static async Task<int> RunLinkAsync(DaemonStore store, Sessions.HarnessOptions harnesses, DaemonTransport transport, TextWriter output,
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
                using var switching = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                var switchRequested = false;
                host.Updater.SwitchRequested += () =>
                {
                    switchRequested = true;
                    switching.Cancel();
                };
                var link = new DaemonLink(state, key, output, outbox, host, transport);
                if (Environment.GetEnvironmentVariable(Service.Launcher.LaunchedVariable) == "1")
                {
                    var versions = new Service.VersionStore(Path.GetFullPath(store.Directory));
                    link.Connected += _ => versions.Confirm(Version); // ends the trial of a new version
                }
                await link.RunAsync(switching.Token);
                return switchRequested ? Service.Launcher.SwitchVersionExitCode : 0;
            }
            catch (UnauthorizedAccessException exception)
            {
                error.WriteLine(exception.Message);
                return 1;
            }
        }
    }
}
