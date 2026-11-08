using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace BoSoFa.Server.Tests;

/// <summary>A private Azurite blob service on a loopback port for the cluster tests, with its own account. Needs
/// `azurite-blob` on the PATH (`npm install --global azurite`) or BOSOFA_AZURITE pointing at it.</summary>
public sealed class Azurite : IAsyncLifetime
{
    const string Account = "bosofa";
    static readonly string Key = Convert.ToBase64String(new byte[32]);
    Process? _process;
    string? _directory;
    Task<string>? _output, _error;

    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        _directory = Directory.CreateTempSubdirectory("bosofa-azurite-").FullName;
        var executable = Environment.GetEnvironmentVariable("BOSOFA_AZURITE") ?? "azurite-blob";
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : executable)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(executable);
        }
        foreach (var argument in new[]
                 {
                     "--blobHost", "127.0.0.1", "--blobPort", port.ToString(), "--location", _directory, "--silent",
                     "--skipApiVersionCheck", "--disableTelemetry"
                 })
            start.ArgumentList.Add(argument);
        start.Environment["AZURITE_ACCOUNTS"] = $"{Account}:{Key}";
        try { _process = Process.Start(start)!; }
        catch (System.ComponentModel.Win32Exception error)
        {
            throw new InvalidOperationException(
                "Azurite is not installed: run `npm install --global azurite` or set BOSOFA_AZURITE.", error);
        }
        _output = _process.StandardOutput.ReadToEndAsync();
        _error = _process.StandardError.ReadToEndAsync();
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (_process.HasExited) throw new InvalidOperationException(await _error + await _output);
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port);
                ConnectionString = $"DefaultEndpointsProtocol=http;AccountName={Account};AccountKey={Key};" +
                                   $"BlobEndpoint=http://127.0.0.1:{port}/{Account};";
                return;
            }
            catch (SocketException) { await Task.Delay(50); }
        }
        throw new TimeoutException("Azurite did not start.");
    }

    public async Task DisposeAsync()
    {
        if (_process != null)
        {
            if (!_process.HasExited) _process.Kill(true);
            await _process.WaitForExitAsync();
            await Task.WhenAll(_output!, _error!);
            _process.Dispose();
        }
        if (_directory != null)
            try { Directory.Delete(_directory, true); }
            catch (IOException) { }
    }
}
