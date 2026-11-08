namespace BoSoFa.Server;

/// <summary>For requests between nodes. A pod that went away takes its IP with it and connection attempts to that IP are dropped
/// silently, so without a limit one attempt hangs for the kernel's two minutes.</summary>
static class PeerHttp
{
    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(3),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
}
