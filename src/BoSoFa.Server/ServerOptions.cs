namespace BoSoFa.Server;

/// <summary>Configuration section "BoSoFa".</summary>
public sealed class ServerOptions
{
    /// <summary>Root of the node's data: the local database cache and, in single-node mode, the shared storage.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>Directory with the built SPA (index.html and assets); relative to the content root.</summary>
    public string WebRoot { get; set; } = "wwwroot";

    /// <summary>Flush every storage write to disk (fsync). False is much faster and survives a crash of the process,
    /// but not of the machine; only for development and tests.</summary>
    public bool FlushToDisk { get; set; } = true;

    /// <summary>Lets anyone sign in by name and email without a password. Only for development.</summary>
    public bool DevLogin { get; set; }

    /// <summary>Refuses daemon WebSockets, as some proxies do, to try the long-poll fallback.</summary>
    public bool RefuseDaemonWebSockets { get; set; }

    /// <summary>Where desktop apps run their page from; only these may call the API from another origin.</summary>
    public string[] DesktopOrigins { get; set; } = ["app://bosofa"];

    /// <summary>How often a running daemon rollout is moved along.</summary>
    public int RolloutTickSeconds { get; set; } = 15;

    /// <summary>Takes the client address and scheme from X-Forwarded-* headers. Only behind a proxy that sets them and
    /// with nothing else able to reach this port.</summary>
    public bool TrustForwardedHeaders { get; set; }

    public OidcOptions? Oidc { get; set; }

    /// <summary>Set to run this server as one node of a cluster; null is single-node mode.</summary>
    public ClusterOptions? Cluster { get; set; }
}

public sealed class ClusterOptions
{
    /// <summary>This node's identity, unique and stable across restarts (a StatefulSet pod name).</summary>
    public string NodeId { get; set; } = "";

    /// <summary>This node's origin as other nodes reach it, on the port it serves everything on (HTTP only on loopback).</summary>
    public string PeerUrl { get; set; } = "";

    /// <summary>Bearer key shared by the nodes for EventLog requests between them.</summary>
    public string PeerKey { get; set; } = "";

    /// <summary>File-system storage shared by every node of a cluster that runs in one process (tests).</summary>
    public string? SharedDirectory { get; set; }

    /// <summary>Azure Blob storage shared by the nodes; required for a cluster of separate processes.</summary>
    public AzureStorageOptions? Azure { get; set; }
}

public sealed class AzureStorageOptions
{
    /// <summary>A storage connection string (Azurite locally). Set this or <see cref="ServiceUri"/>.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>The storage account's blob endpoint, reached through workload identity (AKS).</summary>
    public string ServiceUri { get; set; } = "";

    /// <summary>The container holding the leader document, EventLog, database files and Data Protection keys.</summary>
    public string Container { get; set; } = "bosofa";

    /// <summary>A Key Vault key that wraps the Data Protection key ring at rest, reached through workload identity;
    /// empty leaves the ring unwrapped in the container.</summary>
    public string KeyVaultKey { get; set; } = "";
}

public sealed class OidcOptions
{
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string? ClientSecret { get; set; }
    public string DisplayName { get; set; } = "Single sign-on";
}
