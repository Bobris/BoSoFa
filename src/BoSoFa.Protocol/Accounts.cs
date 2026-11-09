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
    OpenAIApiKey,

    /// <summary>An OpenCode Go API key, from a person's own workspace; only the server agent uses it.</summary>
    OpenCodeGoApiKey
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
    string SecretHint,
    AccountUsageDto? Usage = null,
    bool ServesAgent = false);

/// <summary>One limit window of a subscription. <c>Utilization</c> runs from 0 to 1.</summary>
public sealed record UsageWindow(double Utilization, DateTime? ResetsAt, string? Status);

/// <summary>Asks a machine for its Codex sign-in's plan and limits, which Codex can tell without running a turn.</summary>
public sealed record CodexUsageRequest : MachineRpc;

/// <summary>One limit window of a Codex plan and its length.</summary>
public sealed record CodexWindow(int Minutes, UsageWindow Usage);

/// <summary>A machine's own Codex sign-in; empty when it has none. <c>Credits</c> is the balance, as Codex words it.</summary>
public sealed record CodexUsageResult(bool SignedIn, string? Plan, CodexWindow[] Windows, string? Credits) : MachineRpcResult;

/// <summary>The limits an account's latest turn reported, as of <c>UpdatedAt</c>. A subscription token cannot tell its plan.</summary>
public sealed record AccountUsageDto(UsageWindow? FiveHour, UsageWindow? SevenDay, DateTime UpdatedAt);

/// <summary>A model a provider account can run, for the new-session form.</summary>
public sealed record AgentModelDto(string Id, string Name, long? ContextWindow, long? MaxOutputTokens);

public sealed record CreateHarnessAccountRequest(AccountKind Kind, string Label, string Secret);

/// <summary>Daemon → server: the secret of the account a session runs with.</summary>
public sealed record AccountLeaseRequest(ulong SessionId);

public sealed record AccountLeaseResponse(AccountKind Kind, string Secret);

public static partial class AccountSecrets
{
    /// <summary>The harness an account serves.</summary>
    public static string HarnessOf(AccountKind kind) =>
        kind switch
        {
            AccountKind.OpenAIApiKey => Harnesses.Codex,
            AccountKind.OpenCodeGoApiKey => Harnesses.Agent,
            _ => Harnesses.Claude
        };

    /// <summary>Whether the server's own loop can run on this account (docs/server-agent.md).</summary>
    public static bool ServesAgent(AccountKind kind) => kind is AccountKind.OpenAIApiKey or AccountKind.AnthropicApiKey or AccountKind.OpenCodeGoApiKey;

    /// <summary>A loose shape check that catches pasting the wrong thing; the provider has the final word.</summary>
    public static bool LooksValid(AccountKind kind, string secret) => kind switch
    {
        AccountKind.ClaudeOAuthToken => ClaudeOAuthToken().IsMatch(secret),
        AccountKind.AnthropicApiKey => AnthropicApiKey().IsMatch(secret),
        AccountKind.OpenAIApiKey => OpenAIApiKey().IsMatch(secret),
        AccountKind.OpenCodeGoApiKey => OpenCodeGoApiKey().IsMatch(secret),
        _ => false
    };

    /// <summary>The last characters, enough to tell two secrets apart in a list.</summary>
    public static string Hint(string secret) => secret.Length > 12 ? "…" + secret[^4..] : "…";

    [GeneratedRegex(@"^sk-ant-oat\d+-[A-Za-z0-9_\-]{20,}$")]
    private static partial Regex ClaudeOAuthToken();

    // Anthropic has more than one form of key (sk-ant-api03-, sk-ant-usr-); subscription tokens and admin keys are not API keys.
    [GeneratedRegex(@"^sk-ant-(?!oat|admin)[a-z]+\d*-[A-Za-z0-9_\-]{20,}$")]
    private static partial Regex AnthropicApiKey();

    [GeneratedRegex(@"^sk-[A-Za-z0-9_\-]{20,}$")]
    private static partial Regex OpenAIApiKey();

    // OpenCode does not document the format.
    [GeneratedRegex(@"^\S{20,}$")]
    private static partial Regex OpenCodeGoApiKey();
}
