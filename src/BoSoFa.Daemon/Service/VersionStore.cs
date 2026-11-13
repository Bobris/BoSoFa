using System.Text.Json;
using System.Text.Json.Serialization;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon.Service;

/// <summary>Which version the launcher runs.</summary>
public sealed record VersionState(string? Current);

[JsonSerializable(typeof(VersionState))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class VersionJsonContext : JsonSerializerContext;

/// <summary>The side-by-side versions in the data folder (plan §5.7): <c>versions/&lt;v&gt;/bosofad</c> and the state.</summary>
public sealed class VersionStore(string dataDirectory)
{
    public string Root { get; } = Path.Combine(dataDirectory, "versions");
    string StatePath => Path.Combine(Root, "state.json");

    public static string ExecutableName => OperatingSystem.IsWindows() ? Brand.Daemon + ".exe" : Brand.Daemon;

    public string ExecutableOf(string version) => Path.Combine(Root, version, ExecutableName);

    public VersionState Load() => File.Exists(StatePath)
        ? JsonSerializer.Deserialize(File.ReadAllText(StatePath), VersionJsonContext.Default.VersionState)!
        : new(null);

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

    /// <summary>Deletes every version but <paramref name="keep"/>. One that cannot be deleted (still running, on Windows) goes next time.</summary>
    public void RemoveAllBut(string keep)
    {
        if (!Directory.Exists(Root)) return;
        foreach (var directory in Directory.GetDirectories(Root))
            if (Path.GetFileName(directory) != keep)
                try { Directory.Delete(directory, true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
