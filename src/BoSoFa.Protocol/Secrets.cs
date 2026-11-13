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
