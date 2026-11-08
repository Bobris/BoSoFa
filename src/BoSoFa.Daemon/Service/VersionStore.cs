using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoSoFa.Daemon.Service;

/// <summary>Which version the launcher runs. A new one is on trial until it has reached the server.</summary>
public sealed record VersionState(string? Current, string? Previous, DateTime? TrialStartedAt, string[] Failed);

[JsonSerializable(typeof(VersionState))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class VersionJsonContext : JsonSerializerContext;

/// <summary>The side-by-side versions in the data folder (plan §5.7): <c>versions/&lt;v&gt;/bosofad</c> and the state.</summary>
public sealed class VersionStore(string dataDirectory)
{
    public string Root { get; } = Path.Combine(dataDirectory, "versions");
    string StatePath => Path.Combine(Root, "state.json");
    string ConfirmedPath => Path.Combine(Root, "confirmed");

    public static string ExecutableName => OperatingSystem.IsWindows() ? "bosofad.exe" : "bosofad";

    public string ExecutableOf(string version) => Path.Combine(Root, version, ExecutableName);

    public VersionState Load() => File.Exists(StatePath)
        ? JsonSerializer.Deserialize(File.ReadAllText(StatePath), VersionJsonContext.Default.VersionState)!
        : new(null, null, null, []);

    public void Save(VersionState state)
    {
        Directory.CreateDirectory(Root);
        var temporary = StatePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, VersionJsonContext.Default.VersionState));
        File.Move(temporary, StatePath, true);
    }

    /// <summary>Copies an executable in as a version. Copying to a temporary name first: a half-copied file is never run.</summary>
    public void Add(string version, string executable)
    {
        var target = ExecutableOf(version);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".tmp";
        File.Copy(executable, temporary, true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead |
                                            UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.Move(temporary, target, true);
    }

    /// <summary>The running version has reached the server. Its own file: the launcher owns the state.</summary>
    public void Confirm(string version)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(ConfirmedPath, version);
    }

    public bool IsConfirmed(string version) => File.Exists(ConfirmedPath) && File.ReadAllText(ConfirmedPath) == version;
}
