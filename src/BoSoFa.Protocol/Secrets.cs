using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BoSoFa.Protocol;

/// <summary>A secret is written and used, never read back; a variable is a value that is not secret (docs/secrets.md).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SecretKind>))]
public enum SecretKind
{
    Secret,
    Variable
}

/// <summary>Whose sessions a value is for: its owner's (<c>Personal</c>), every member's (<c>Shared</c>, set by an admin), or
/// those whose profile names an environment (<c>Environment</c>, set by an admin). The environment's value of a name wins, then
/// a person's own, then the shared one.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SecretScope>))]
public enum SecretScope
{
    Personal,
    Shared,
    Environment
}

/// <summary>A secret or a variable as people see it. <c>Value</c> is set for variables only; a secret shows its
/// <c>Hint</c>, never its value. A <c>HostOnly</c> secret goes to MCP servers, never to an agent's shell (docs/secrets.md §6).
/// <c>Machines</c> limits it to sessions on those machines (empty: any); <c>RotateBy</c> is when it is due for rotation, and
/// <c>RotationReason</c> says why it should be rotated now. <c>UsedOn</c> is for whoever manages it, null for others.</summary>
public sealed record SecretDto(SecretKind Kind, SecretScope Scope, string Name, int Version, string? Hint, string? Value, string SetBy,
    DateTime SetAt, bool HostOnly = false, string? Environment = null, ulong[]? Machines = null, int RotateEveryDays = 0,
    DateTime? RotateBy = null, string? RotationReason = null, SecretUseDto[]? UsedOn = null);

/// <summary>The last time a value went to a machine; <c>MachineId</c> 0 is the server itself (a server agent session without a
/// machine).</summary>
public sealed record SecretUseDto(ulong MachineId, string Machine, int Version, DateTime At);

/// <summary>Sets a value, or rotates it: a name keeps its kind, so turning a secret into a variable means deleting it first.
/// <c>HostOnly</c> is for secrets only, and each version says it again; <c>Machines</c> and <c>RotateEveryDays</c> keep what
/// the name had when null.</summary>
public sealed record SetSecretRequest(SecretKind Kind, string Value, bool HostOnly = false, ulong[]? Machines = null, int? RotateEveryDays = null);

/// <summary>Changes how a value is used without a new version of it.</summary>
public sealed record SecretSettingsRequest(bool HostOnly, ulong[] Machines, int RotateEveryDays);

/// <summary>Someone, or a machine, as a name to show.</summary>
public sealed record NamedRef(ulong Id, string Name);

/// <summary>
/// A named set of secrets and variables with rules of its own (docs/secrets.md §10), such as <c>production</c>: a profile names
/// it, and its values win over a person's and the shared ones. <c>Reviewers</c>: one of them, not the session's creator, approves
/// each session that uses it before it starts (empty: none needed). <c>Machines</c>: the only machines its sessions run on
/// (empty: any).
/// </summary>
public sealed record EnvironmentDto(string Name, string Description, NamedRef[] Reviewers, NamedRef[] Machines, SecretDto[] Values,
    string ChangedBy, DateTime ChangedAt);

public sealed record SaveEnvironmentRequest(string Description, ulong[] Reviewers, ulong[] Machines);

[JsonConverter(typeof(JsonStringEnumConverter<EnvironmentApprovalStatus>))]
public enum EnvironmentApprovalStatus
{
    Pending,
    Approved,
    Rejected
}

/// <summary>A session that waits for a reviewer of its environment. It names where and how the session runs, never its prompt
/// or title: those stay its creator's.</summary>
public sealed record EnvironmentApprovalDto(ulong SessionId, string Environment, string RequestedBy, string Machine, string Harness,
    string? Profile, string WorkingDirectory, DateTime RequestedAt);

public sealed record AnswerEnvironmentApprovalRequest(bool Approve);

/// <summary>A value a session may refer to, by name: never its value (docs/secrets.md §4.3). It is the one a reference resolves to
/// for the session's creator on its machine, so a name the environment, the person and the team all set is listed once, with the
/// scope that wins.</summary>
public sealed record SessionSecretName(string Name, SecretKind Kind, SecretScope Scope, bool HostOnly = false);

public static partial class SecretNames
{
    /// <summary>The longest value, enough for a PEM key or a small JSON credential.</summary>
    public const int MaxValueLength = 32 * 1024;

    /// <summary>Like an environment variable: letters, digits and <c>_</c>, not starting with a digit.</summary>
    public static bool IsValid(string name) => Simple().IsMatch(name);

    /// <summary>The reference to write in a configuration.</summary>
    public static string Reference(SecretKind kind, string name) => kind == SecretKind.Secret ? $"${{secret:{name}}}" : $"${{var:{name}}}";

    /// <summary>The description of the agents' tool that lists names, the daemon's and the server agent's alike.</summary>
    public const string ToolDescription =
        "List the names of the secrets and variables this session can refer to, as ${secret:NAME} or ${var:NAME}. Values are never shown.";

    /// <summary>What an agent's tool answers: each name with the reference to write, or how to get one when there are none.</summary>
    public static string Describe(IReadOnlyCollection<SessionSecretName> names)
    {
        if (names.Count == 0)
            return "No secrets or variables are set for this session. Ask the person to set one under Secrets and variables; then refer to it as ${secret:NAME} or ${var:NAME}.";
        var text = new System.Text.StringBuilder("Refer to these in a profile or an MCP server's configuration; their values are never shown.\n");
        foreach (var name in names)
        {
            text.Append(Reference(name.Kind, name.Name)).Append(" (").Append(name.Kind == SecretKind.Secret ? "secret" : "variable").Append(", ")
                .Append(name.Scope switch { SecretScope.Personal => "personal", SecretScope.Shared => "shared", _ => "environment" });
            if (name.HostOnly) text.Append(", for MCP servers only: never in a session's environment");
            text.Append(")\n");
        }
        return text.ToString().TrimEnd();
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex Simple();
}

/// <summary>A <c>${secret:NAME}</c> or <c>${var:NAME}</c> in a value of the configuration.</summary>
public readonly record struct SecretReference(SecretKind Kind, string Name)
{
    public override string ToString() => SecretNames.Reference(Kind, Name);
}

/// <summary>References to secrets and variables in configuration values (docs/secrets.md §4).</summary>
public static partial class SecretReferences
{
    /// <summary>The references of a value, in order.</summary>
    public static IEnumerable<SecretReference> In(string value) =>
        Reference().Matches(value).Select(match => new SecretReference(KindOf(match.Groups["kind"].Value)!.Value, match.Groups["name"].Value));

    /// <summary>What is wrong with the references of a value, or <see langword="null"/>: every <c>${</c> must begin one.</summary>
    public static string? Problem(string value)
    {
        foreach (Match match in Opening().Matches(value))
        {
            var reference = Reference().Match(value, match.Index);
            if (reference.Success && reference.Index == match.Index) continue;
            var end = value.IndexOf('}', match.Index);
            var text = end < 0 ? value[match.Index..] : value[match.Index..(end + 1)];
            return $"{text} is not a reference: write ${{secret:NAME}} or ${{var:NAME}}, NAME being letters, digits and _.";
        }
        return null;
    }

    /// <summary>The value with each reference replaced by what <paramref name="resolve"/> gives for it.</summary>
    public static string Replace(string value, Func<SecretReference, string> resolve) =>
        Reference().Replace(value, match => resolve(new(KindOf(match.Groups["kind"].Value)!.Value, match.Groups["name"].Value)));

    /// <summary>A name whose value is likely a credential, so it must come from a secret, never be written in the clear.</summary>
    public static bool LooksSecret(string name) => SecretLike().IsMatch(name);

    static SecretKind? KindOf(string word) => word switch { "secret" => SecretKind.Secret, "var" => SecretKind.Variable, _ => null };

    [GeneratedRegex(@"\$\{(?<kind>secret|var):(?<name>[A-Za-z_][A-Za-z0-9_]{0,63})\}")]
    private static partial Regex Reference();

    [GeneratedRegex(@"\$\{")]
    private static partial Regex Opening();

    [GeneratedRegex("token|passw|secret|key|authorization|cookie|credential", RegexOptions.IgnoreCase)]
    private static partial Regex SecretLike();
}
