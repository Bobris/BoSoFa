namespace BoSoFa.Server;

/// <summary>Configuration section "BoSoFa".</summary>
public sealed class ServerOptions
{
    /// <summary>Root of the node's data: the local database cache and, in single-node mode, the shared storage.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>Directory with the built SPA (index.html and assets); relative to the content root.</summary>
    public string WebRoot { get; set; } = "wwwroot";

    /// <summary>Lets anyone sign in by name and email without a password. Only for development.</summary>
    public bool DevLogin { get; set; }

    public OidcOptions? Oidc { get; set; }
}

public sealed class OidcOptions
{
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string? ClientSecret { get; set; }
    public string DisplayName { get; set; } = "Single sign-on";
}
