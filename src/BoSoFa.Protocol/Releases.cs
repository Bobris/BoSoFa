namespace BoSoFa.Protocol;

/// <summary>One file a person or an install script can fetch without signing in.</summary>
public sealed record DownloadFile(string Rid, string FileName, string Sha256, long Size, string Url);

/// <summary>The build of a product that this server carries (plan §5.7): one version, every platform it was built for.</summary>
public sealed record ProductDownload(string Product, string Version, DownloadFile[] Files);

/// <summary>Sent to a daemon whose version is older than the one the server carries: fetch
/// <c>/api/v1/downloads/daemon/{rid}</c>, check its SHA-256 and switch to it.</summary>
public sealed record DaemonUpdate(string Version, string Sha256, long Size);

public static class Releases
{
    public const string Daemon = "daemon";
    public const string Desktop = "desktop";

    /// <summary>Every platform a server build carries the daemon and the desktop app for.</summary>
    public static readonly string[] Rids = ["linux-x64", "win-x64", "osx-arm64"];

    public static bool IsValidProduct(string product) => product is Daemon or Desktop;

    /// <summary>What a download is called: the daemon is a bare executable, the desktop app an archive of its folder.</summary>
    public static string FileName(string product, string rid)
    {
        var windows = rid.StartsWith("win-", StringComparison.Ordinal);
        return product == Daemon
            ? windows ? Brand.Daemon + ".exe" : Brand.Daemon
            : $"{Brand.Desktop}-{rid}{(windows ? ".zip" : ".tar.gz")}";
    }

    /// <summary>A version names a folder on every machine, so it must be plain.</summary>
    public static bool IsValidVersion(string version) =>
        version.Length is > 0 and <= 100 && version[0] != '.' && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '_' or '-');

    /// <summary>The .NET runtime identifier of a machine, from what its daemon reports.</summary>
    public static string RidOf(string os, string arch) => (os == "macos" ? "osx" : os == "windows" ? "win" : os) + "-" + arch;

    /// <summary>Only forward, by SemVer precedence (build metadata after <c>+</c> does not count). While a cluster rolls to a
    /// new build, its nodes carry different versions; a computer moves up once and never back.</summary>
    public static bool IsNewer(string candidate, string current)
    {
        static (Version? Number, string? Prerelease) Parse(string text)
        {
            var plus = text.IndexOf('+');
            if (plus >= 0) text = text[..plus];
            var dash = text.IndexOf('-');
            return (Version.TryParse(dash < 0 ? text : text[..dash], out var number) ? number : null, dash < 0 ? null : text[(dash + 1)..]);
        }
        var (a, aPre) = Parse(candidate);
        var (b, bPre) = Parse(current);
        if (a == null || b == null) return false;
        var order = a.CompareTo(b);
        if (order != 0) return order > 0;
        if (aPre == null || bPre == null) return aPre == null && bPre != null;
        return ComparePrerelease(aPre.Split('.'), bPre.Split('.')) > 0;
    }

    /// <summary>SemVer: numeric identifiers by value and below alphanumeric ones, then the longer list wins.</summary>
    static int ComparePrerelease(string[] a, string[] b)
    {
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNumber = ulong.TryParse(a[i], out var x);
            var bNumber = ulong.TryParse(b[i], out var y);
            var order = aNumber && bNumber ? x.CompareTo(y) : aNumber ? -1 : bNumber ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (order != 0) return order;
        }
        return a.Length.CompareTo(b.Length);
    }
}
