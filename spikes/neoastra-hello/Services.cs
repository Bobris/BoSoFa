using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using NeoAstra.Rpc;

[assembly: NeoRpcJsonContext(typeof(NeoAstraHello.SpikeJsonContext))]

namespace NeoAstraHello;

/// <summary>The typed NeoRpc service of the spike: facts about this machine, and the page's probe reports.</summary>
[NeoRpcService("system", Version = 1)]
public sealed class SystemService
{
    readonly DateTime _processStart;
    readonly TaskCompletionSource _rendered;

    internal SystemService(DateTime processStart, TaskCompletionSource rendered)
    {
        _processStart = processStart;
        _rendered = rendered;
    }

    [NeoRpcMethod("info")]
    public SystemInfo Info(SystemInfoRequest request) => new(
        Environment.MachineName, RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString(),
        RuntimeInformation.FrameworkDescription, Environment.ProcessId, (DateTime.UtcNow - _processStart).TotalMilliseconds);

    /// <summary>The page writes what it found to the console of the process, where the spike's logs collect it.</summary>
    [NeoRpcMethod("report")]
    public ReportResponse Report(ReportRequest request)
    {
        var sinceStart = (DateTime.UtcNow - _processStart).TotalMilliseconds;
        Console.WriteLine($"[page +{sinceStart:F0} ms] {request.Name}: {(request.Ok ? "OK" : "FAIL")} {request.Detail}");
        if (request.Name == "done") _rendered.TrySetResult();
        return new(sinceStart);
    }
}

/// <summary>
/// Prototype c, RPC variant: the page asks C# to call the server. Asynchronous and off the UI thread, unlike the scheme
/// provider; it can return any status with its body, and a later version could stream (NeoRpc channels).
/// </summary>
[NeoRpcService("server", Version = 1)]
public sealed class ServerService
{
    readonly ServerSession _session;

    internal ServerService(ServerSession session) => _session = session;

    [NeoRpcMethod("fetch")]
    public async ValueTask<ServerFetchResponse> FetchAsync(ServerFetchRequest request, CancellationToken cancellationToken)
    {
        var body = request.Body is null ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(request.Body);
        var response = await _session.SendAsync(request.Method, request.Path, null, body, request.ContentType, cancellationToken);
        return new(response.Status, response.ContentType, Encoding.UTF8.GetString(response.Body));
    }

    [NeoRpcMethod("origin")]
    public ServerOriginResponse Origin(SystemInfoRequest request) => new(_session.Origin.GetLeftPart(UriPartial.Authority));
}

public sealed record SystemInfoRequest;
public sealed record SystemInfo(string MachineName, string Os, string Architecture, string Framework, int ProcessId, double UptimeMs);
public sealed record ReportRequest(string Name, bool Ok, string Detail);
public sealed record ReportResponse(double SinceProcessStartMs);
public sealed record ServerFetchRequest(string Method, string Path, string? Body, string? ContentType);
public sealed record ServerFetchResponse(int Status, string? ContentType, string Body);
public sealed record ServerOriginResponse(string Origin);

[JsonSerializable(typeof(SystemInfoRequest))]
[JsonSerializable(typeof(SystemInfo))]
[JsonSerializable(typeof(ReportRequest))]
[JsonSerializable(typeof(ReportResponse))]
[JsonSerializable(typeof(ServerFetchRequest))]
[JsonSerializable(typeof(ServerFetchResponse))]
[JsonSerializable(typeof(ServerOriginResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class SpikeJsonContext : JsonSerializerContext;
