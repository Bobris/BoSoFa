using System.Reflection;
using BoSoFa.Daemon;

return await Cli.RunAsync(args, Console.Out, Console.Error);

namespace BoSoFa.Daemon
{
    public static class Cli
    {
        public static string Version { get; } =
            typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        const string Usage = """
            Usage: bosofad <command>

              version                 Print the daemon version.
              status --server <url>   Check that the server answers and speaks this protocol version.
            """;

        public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
        {
            switch (args)
            {
                case ["version"] or ["--version"]:
                    output.WriteLine($"bosofad {Version} (protocol {Protocol.ProtocolVersion.Current})");
                    return 0;
                case ["status", "--server", var server]:
                    return await StatusAsync(new Uri(server), output, error);
                default:
                    error.WriteLine(Usage);
                    return args is [] or ["help"] or ["--help"] ? 0 : 2;
            }
        }

        static async Task<int> StatusAsync(Uri server, TextWriter output, TextWriter error)
        {
            using var client = new ServerClient(server);
            try
            {
                var info = await client.GetVersionAsync(CancellationToken.None);
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
    }
}
