using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon;

/// <summary>What an enrolled daemon remembers: its server and machine ID (daemon.json) and its private key
/// (machine-key.pem, readable only by the owner). Nothing else is secret.</summary>
public sealed record DaemonState(Uri Server, ulong MachineId);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(DaemonState))]
internal sealed partial class DaemonJsonContext : JsonSerializerContext;

public sealed class DaemonStore(string directory)
{
    public string Directory { get; } = directory;
    string StatePath => Path.Combine(Directory, "daemon.json");
    string KeyPath => Path.Combine(Directory, "machine-key.pem");

    /// <summary>Per-user data directory (<see cref="Brand.DataFolder"/>): ~/Library/Application Support/BoSoFa (macOS),
    /// $XDG_DATA_HOME/bosofa or ~/.local/share/bosofa (Linux), %LOCALAPPDATA%\BoSoFa (Windows).</summary>
    public static string DefaultDirectory() => Brand.DataFolder();

    public DaemonState? LoadState() => File.Exists(StatePath)
        ? JsonSerializer.Deserialize(File.ReadAllText(StatePath), DaemonJsonContext.Default.DaemonState)
        : null;

    public void SaveState(DaemonState state)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temporary = StatePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, DaemonJsonContext.Default.DaemonState));
        File.Move(temporary, StatePath, true);
    }

    /// <summary>The machine key, created on first use. On Windows the user profile ACL protects it; DPAPI comes with the
    /// service installation (M5).</summary>
    public ECDsa LoadOrCreateKey()
    {
        var key = ECDsa.Create();
        if (File.Exists(KeyPath))
        {
            key.ImportFromPem(File.ReadAllText(KeyPath));
            return key;
        }
        key.GenerateKey(ECCurve.NamedCurves.nistP256);
        System.IO.Directory.CreateDirectory(Directory);
        var temporary = KeyPath + ".tmp";
        var fileOptions = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporary, fileOptions))
        using (var writer = new StreamWriter(stream))
            writer.Write(key.ExportPkcs8PrivateKeyPem());
        File.Move(temporary, KeyPath, true);
        return key;
    }

    public static string PublicKey(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    public static string OsName() =>
        OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other";

    public static string ArchName() => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
}
