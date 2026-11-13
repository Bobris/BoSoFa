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

/// <summary>Whose sessions a value is for: its owner's (<c>Personal</c>), or every member's (<c>Shared</c>, set by an admin).
/// A person's own value wins over a shared one of the same name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SecretScope>))]
public enum SecretScope
{
    Personal,
    Shared
}

/// <summary>A secret or a variable as people see it. <c>Value</c> is set for variables only; a secret shows its
/// <c>Hint</c>, never its value.</summary>
public sealed record SecretDto(SecretKind Kind, SecretScope Scope, string Name, int Version, string? Hint, string? Value, string SetBy,
    DateTime SetAt);

/// <summary>Sets a value, or rotates it: a name keeps its kind, so turning a secret into a variable means deleting it first.</summary>
public sealed record SetSecretRequest(SecretKind Kind, string Value);

public static partial class SecretNames
{
    /// <summary>The longest value, enough for a PEM key or a small JSON credential.</summary>
    public const int MaxValueLength = 32 * 1024;

    /// <summary>Like an environment variable: letters, digits and <c>_</c>, not starting with a digit.</summary>
    public static bool IsValid(string name) => Simple().IsMatch(name);

    /// <summary>The reference to write in a configuration.</summary>
    public static string Reference(SecretKind kind, string name) => kind == SecretKind.Secret ? $"${{secret:{name}}}" : $"${{var:{name}}}";

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex Simple();
}
