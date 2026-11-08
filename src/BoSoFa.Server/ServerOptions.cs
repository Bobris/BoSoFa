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

    public OidcOptions? Oidc { get; set; }

    /// <summary>Set to run this server as one node of a cluster; null is single-node mode.</summary>
    public ClusterOptions? Cluster { get; set; }
}

public sealed class ClusterOptions
{
    /// <summary>This node's identity, unique and stable across restarts (a StatefulSet pod name).</summary>
    public string NodeId { get; set; } = "";

    /// <summary>This node's peer origin as other nodes reach it, on its own port (HTTP only on loopback).</summary>
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
    /// <summary>A storage connection string (Azurite locally). Workload identity replaces it on AKS (M7).</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>The container holding the leader document, EventLog, database files and Data Protection keys.</summary>
    public string Container { get; set; } = "bosofa";
}

public sealed class OidcOptions
{
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string? ClientSecret { get; set; }
    public string DisplayName { get; set; } = "Single sign-on";
}
