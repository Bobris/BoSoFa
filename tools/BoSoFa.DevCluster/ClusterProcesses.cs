using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace BoSoFa.DevCluster;

/// <summary>A server node in its own process (`dotnet bosofa-server.dll`), which can be killed like a crashed pod.
/// Restarted when it exits by itself (BTDB asks a node to restart, e.g. after restoring a new cluster before its
/// genesis was published), as a Kubernetes supervisor would; never after <see cref="Kill"/> until
/// <see cref="Restart"/>.</summary>
public sealed class NodeProcess : IAsyncDisposable
{
    static readonly string ServerDll = Path.Combine(AppContext.BaseDirectory, "bosofa-server.dll");
    readonly string[] _arguments;
    readonly string _log;
    readonly object _lock = new();
    Process? _process;
    bool _stopped;

    /// <param name="root">Each node keeps its data and log below it.</param>
    /// <param name="mainPort">The port for clients and peers alike; 0 picks a free one.</param>
    public NodeProcess(string root, string nodeId, string azure, string container, int mainPort = 0,
        string environment = "Development", string[]? extraArguments = null)
    {
        NodeId = nodeId;
        MainUrl = new($"http://127.0.0.1:{(mainPort == 0 ? FreePort() : mainPort)}");
        PeerUrl = MainUrl.GetLeftPart(UriPartial.Authority);
        var data = Path.Combine(root, nodeId);
        Directory.CreateDirectory(data);
        _log = Path.Combine(root, nodeId + ".log");
        _arguments =
        [
            ServerDll, "--urls", PeerUrl, "--environment", environment,
            "--BoSoFa:DataDirectory", data, "--BoSoFa:DevLogin", "true", "--BoSoFa:FlushToDisk", "false",
            "--BoSoFa:WebRoot", Path.Combine(data, "no-web-root"), "--BoSoFa:Cluster:NodeId", nodeId,
            "--BoSoFa:Cluster:PeerUrl", PeerUrl, "--BoSoFa:Cluster:PeerKey", "test-peer-key",
            "--BoSoFa:Cluster:Azure:ConnectionString", azure, "--BoSoFa:Cluster:Azure:Container", container,
            ..extraArguments ?? []
        ];
        Start();
    }

    public string NodeId { get; }
    public Uri MainUrl { get; }
    public string PeerUrl { get; }
    public int Restarts { get; private set; }
    public bool Running
    {
        get { lock (_lock) return !_stopped; }
    }
    /// <summary>What the node wrote so far; readable while it still writes, which Windows allows only when the reader shares writing.</summary>
    public string Log
    {
        get
        {
            if (!File.Exists(_log)) return "";
            using var reader = new StreamReader(new FileStream(_log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            return reader.ReadToEnd();
        }
    }

    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    void Start()
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var argument in _arguments) start.ArgumentList.Add(argument);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var log = TextWriter.Synchronized(new StreamWriter(_log, append: true) { AutoFlush = true });
        process.OutputDataReceived += (_, line) => { if (line.Data != null) log.WriteLine(line.Data); };
        process.ErrorDataReceived += (_, line) => { if (line.Data != null) log.WriteLine(line.Data); };
        process.Exited += (_, _) =>
        {
            lock (_lock)
            {
                if (_stopped || _process != process) return;
                Restarts++;
                Start();
            }
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
    }

    /// <summary>Starts a killed node again, over its existing data.</summary>
    public void Restart()
    {
        lock (_lock)
        {
            if (!_stopped) return;
            _stopped = false;
            Start();
        }
    }

    /// <summary>Like a crashed pod: no graceful shutdown, no lease release, and no restart.</summary>
    public void Kill()
    {
        lock (_lock)
        {
            _stopped = true;
            try { _process?.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Process? process;
        lock (_lock)
        {
            _stopped = true;
            process = _process;
        }
        if (process == null) return;
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        await process.WaitForExitAsync();
        process.Dispose();
    }
}

/// <summary>A TCP front door like an ingress: each connection goes to the first backend that accepts it, so clients
/// fail over to a live node when one dies (their open connections break and they reconnect).</summary>
public sealed class TcpBalancer : IAsyncDisposable
{
    readonly TcpListener _listener;
    readonly IReadOnlyList<Uri> _backends;
    readonly CancellationTokenSource _stop = new();
    readonly Task _accepting;

    /// <param name="port">The listening port; 0 picks a free one.</param>
    public TcpBalancer(IReadOnlyList<Uri> backends, int port = 0)
    {
        _backends = backends;
        _listener = new(IPAddress.Loopback, port);
        _listener.Start();
        Url = new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
        _accepting = AcceptAsync();
    }

    public Uri Url { get; }

    async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = ForwardAsync(client);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    async Task ForwardAsync(TcpClient client)
    {
        using (client)
        {
            TcpClient? backend = null;
            foreach (var target in _backends)
            {
                var candidate = new TcpClient();
                try
                {
                    await candidate.ConnectAsync(target.Host, target.Port, _stop.Token);
                    backend = candidate;
                    break;
                }
                catch (SocketException) { candidate.Dispose(); }
            }
            if (backend == null) return;
            using (backend)
            {
                var up = client.GetStream().CopyToAsync(backend.GetStream(), _stop.Token);
                var down = backend.GetStream().CopyToAsync(client.GetStream(), _stop.Token);
                try { await Task.WhenAny(up, down); }
                catch (Exception) { /* either side went away */ }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _accepting;
    }
}
