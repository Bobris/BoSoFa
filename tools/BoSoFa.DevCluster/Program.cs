using BoSoFa.DevCluster;

// A local BoSoFa cluster for development: Azurite, N server nodes (each its own process, restarted when it exits by
// itself) and a front door on the usual development port, so the SPA dev server and daemons need no other setup.
var nodes = 3;
var port = 5080;
var dataDirectory = Path.GetFullPath("data/cluster");
for (var i = 0; i < args.Length - 1; i += 2)
    switch (args[i])
    {
        case "--nodes": nodes = int.Parse(args[i + 1]); break;
        case "--port": port = int.Parse(args[i + 1]); break;
        case "--data-dir": dataDirectory = Path.GetFullPath(args[i + 1]); break;
        default:
            Console.Error.WriteLine("Usage: bosofa-devcluster [--nodes 3] [--port 5080] [--data-dir data/cluster]");
            return 2;
    }

await using var azurite = new Azurite();
await azurite.StartAsync(Path.Combine(dataDirectory, "azurite"), port + 1000);
Console.WriteLine($"Azurite blob service on port {port + 1000} ({Path.Combine(dataDirectory, "azurite")})");

var cluster = new List<NodeProcess>();
try
{
    for (var n = 1; n <= nodes; n++)
    {
        var node = new NodeProcess(dataDirectory, $"node-{n}", azurite.ConnectionString, "bosofa", port + 100 + n,
            port + 200 + n);
        cluster.Add(node);
        Console.WriteLine($"{node.NodeId}: {node.MainUrl} (peer {node.PeerUrl}), log {Path.Combine(dataDirectory, node.NodeId + ".log")}");
        // The first node of a new cluster publishes its genesis; others restoring before that restart once.
        if (n == 1) await Task.Delay(TimeSpan.FromSeconds(3));
    }
    await using var front = new TcpBalancer(cluster.Select(node => node.MainUrl).ToList(), port);
    Console.WriteLine($"Front door: {front.Url} (each connection goes to the first live node)");
    Console.WriteLine("Commands: kill <n>, start <n>, status, quit");
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Cancel();
    };
    using var terminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(
        System.Runtime.InteropServices.PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stop.Cancel();
        });
    while (!stop.IsCancellationRequested)
    {
        var line = await Task.Run(Console.ReadLine, stop.Token).WaitAsync(stop.Token).ContinueWith(t =>
            t.IsCompletedSuccessfully ? t.Result : null);
        if (line == null)
        {
            // No console input (started in the background): run until Ctrl+C or a termination signal.
            try { await Task.Delay(Timeout.Infinite, stop.Token); }
            catch (OperationCanceledException) { }
            break;
        }
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        NodeProcess? Node() => parts.Length == 2 && int.TryParse(parts[1], out var n) && n >= 1 && n <= cluster.Count
            ? cluster[n - 1]
            : null;
        switch (parts.FirstOrDefault())
        {
            case "kill" when Node() is { } node:
                node.Kill();
                Console.WriteLine($"{node.NodeId} killed");
                break;
            case "start" when Node() is { } node:
                node.Restart();
                Console.WriteLine($"{node.NodeId} started");
                break;
            case "status":
                foreach (var node in cluster)
                    Console.WriteLine($"{node.NodeId}: {(node.Running ? "running" : "killed")}, restarts {node.Restarts}");
                break;
            case "quit" or "exit":
                stop.Cancel();
                break;
            case null:
                break;
            default:
                Console.WriteLine("Commands: kill <n>, start <n>, status, quit");
                break;
        }
    }
}
finally
{
    foreach (var node in cluster) await node.DisposeAsync();
}
return 0;
