using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BoSoFa.Protocol;

/// <summary>What a harness account holds (plan §5.4.1). Subscriptions are personal; API keys may serve a team.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AccountKind>))]
public enum AccountKind
{
    /// <summary>A Claude subscription token from <c>claude setup-token</c> (one year, inference only).</summary>
    ClaudeOAuthToken,

    /// <summary>An Anthropic API key (API billing).</summary>
    AnthropicApiKey,

    /// <summary>An OpenAI API key.</summary>
    OpenAIApiKey
}

/// <summary>A harness account as people see it; the secret never leaves the server except to a daemon's lease.</summary>
public sealed record HarnessAccountDto(
    ulong Id,
    AccountKind Kind,
    string Harness,
    string Label,
    ulong OwnerId,
    string OwnerName,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    string SecretHint);

public sealed record CreateHarnessAccountRequest(AccountKind Kind, string Label, string Secret);

/// <summary>Daemon → server: the secret of the account a session runs with.</summary>
public sealed record AccountLeaseRequest(ulong SessionId);

public sealed record AccountLeaseResponse(AccountKind Kind, string Secret);

public static partial class AccountSecrets
{
    /// <summary>The harness an account serves.</summary>
    public static string HarnessOf(AccountKind kind) =>
        kind is AccountKind.OpenAIApiKey ? Harnesses.Codex : Harnesses.Claude;

    /// <summary>A loose shape check that catches pasting the wrong thing; the provider has the final word.</summary>
    public static bool LooksValid(AccountKind kind, string secret) => kind switch
    {
        AccountKind.ClaudeOAuthToken => ClaudeOAuthToken().IsMatch(secret),
        AccountKind.AnthropicApiKey => AnthropicApiKey().IsMatch(secret),
        AccountKind.OpenAIApiKey => OpenAIApiKey().IsMatch(secret),
        _ => false
    };

    /// <summary>The last characters, enough to tell two secrets apart in a list.</summary>
    public static string Hint(string secret) => secret.Length > 12 ? "…" + secret[^4..] : "…";

    [GeneratedRegex(@"^sk-ant-oat\d+-[A-Za-z0-9_\-]{20,}$")]
    private static partial Regex ClaudeOAuthToken();

    [GeneratedRegex(@"^sk-ant-api\d+-[A-Za-z0-9_\-]{20,}$")]
    private static partial Regex AnthropicApiKey();

    [GeneratedRegex(@"^sk-[A-Za-z0-9_\-]{20,}$")]
    private static partial Regex OpenAIApiKey();
}
