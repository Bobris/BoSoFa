using System.Text.Json;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Machines;

/// <summary>One file of <c>bundle.json</c>; <c>Path</c> is relative to the bundle's folder.</summary>
public sealed record BundleFile(string Product, string Rid, string Path, string Sha256);

/// <summary>What <c>dotnet publish</c> of the server writes beside the daemon and desktop builds it made.</summary>
public sealed record BundleManifest(string Version, BundleFile[] Files);

/// <summary>
/// The daemon and the desktop app this server carries (plan §5.7): built for every platform together with the server and
/// shipped in its folder, so the install page, the daemons and the desktop apps always get the build that matches it. A
/// server without a bundle (a development build) offers nothing to install and updates nothing.
/// </summary>
public sealed class ClientBundle
{
    public sealed record Entry(string Product, string Rid, string FullPath, string Sha256, long Size);

    public string? Version { get; }
    public IReadOnlyList<Entry> Files { get; }

    ClientBundle(string? version, IReadOnlyList<Entry> files)
    {
        Version = version;
        Files = files;
    }

    public static readonly ClientBundle Empty = new(null, []);

    /// <summary>Files that are missing, or would lie outside the folder, are left out.</summary>
    public static ClientBundle Load(string directory)
    {
        var path = Path.Combine(directory, "bundle.json");
        if (!File.Exists(path)) return Empty;
        var manifest = JsonSerializer.Deserialize(File.ReadAllText(path), ServerJsonContext.Default.BundleManifest);
        if (manifest == null || !Releases.IsValidVersion(manifest.Version)) return Empty;
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var files = new List<Entry>();
        foreach (var file in manifest.Files)
        {
            var full = Path.GetFullPath(Path.Combine(root, file.Path));
            if (!Releases.IsValidProduct(file.Product) || !full.StartsWith(root, StringComparison.Ordinal) || !File.Exists(full)) continue;
            files.Add(new(file.Product, file.Rid, full, file.Sha256.ToLowerInvariant(), new FileInfo(full).Length));
        }
        return new(manifest.Version, files);
    }

    public Entry? Find(string product, string rid) => Files.FirstOrDefault(file => file.Product == product && file.Rid == rid);

    public static string UrlOf(string product, string rid) => $"/api/v1/downloads/{product}/{rid}";

    public ProductDownload[] Downloads() => Version is not { } version
        ? []
        : Files.GroupBy(file => file.Product).Select(product => new ProductDownload(product.Key, version, product.Select(file =>
            new DownloadFile(file.Rid, Releases.FileName(file.Product, file.Rid), file.Sha256, file.Size, UrlOf(file.Product, file.Rid))).ToArray())).ToArray();

    /// <summary>What a daemon that said hello should switch to: this server's build, when it is newer.</summary>
    public DaemonUpdate? UpdateFor(HelloMessage hello) =>
        Version is { } version && Releases.IsNewer(version, hello.DaemonVersion) && Find(Releases.Daemon, Releases.RidOf(hello.Os, hello.Arch)) is { } file
            ? new DaemonUpdate(version, file.Sha256, file.Size)
            : null;
}
