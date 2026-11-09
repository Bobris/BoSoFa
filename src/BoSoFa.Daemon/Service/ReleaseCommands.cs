using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon.Service;

/// <summary>Making releases: <c>bosofad release-keygen</c> and <c>release-publish</c>. The private key stays offline.</summary>
public static class ReleaseCommands
{
    /// <summary>Writes a new private key and prints the public key that builds embed as <c>BoSoFaReleasePublicKey</c>.</summary>
    public static int KeyGen(string file, TextWriter output, TextWriter error)
    {
        if (File.Exists(file))
        {
            error.WriteLine($"{file} already exists.");
            return 2;
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(file, key.ExportECPrivateKeyPem());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        output.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }

    /// <summary>Uploads the binaries, signs the manifest and publishes it. The API token comes from BOSOFA_TOKEN.</summary>
    /// <param name="product">What is published: the daemon, or archives of the desktop app.</param>
    /// <param name="artifacts"><c>rid=path</c>, separated by commas.</param>
    public static async Task<int> PublishAsync(Uri server, string keyFile, string product, string version, string channel, string artifacts, TextWriter output, TextWriter error,
        CancellationToken cancellation)
    {
        if (!Releases.IsValidProduct(product))
        {
            error.WriteLine($"Unknown product '{product}': use {Releases.Daemon} or {Releases.Desktop}.");
            return 2;
        }
        if (Environment.GetEnvironmentVariable("BOSOFA_TOKEN") is not { Length: > 0 } token)
        {
            error.WriteLine("Set BOSOFA_TOKEN to an API token with full control.");
            return 2;
        }
        using var http = new HttpClient { BaseAddress = server, Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var uploaded = new List<ReleaseArtifact>();
        foreach (var entry in artifacts.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var (rid, path) = (entry[..entry.IndexOf('=')], entry[(entry.IndexOf('=') + 1)..]);
            await using var content = File.OpenRead(path);
            using var response = await http.PostAsync("/api/v1/blobs", new StreamContent(content), cancellation);
            response.EnsureSuccessStatusCode();
            var blob = (await response.Content.ReadFromJsonAsync(ProtocolJsonContext.Default.BlobRef, cancellation))!;
            uploaded.Add(new(rid, blob.Sha256, blob.Size));
            output.WriteLine($"Uploaded {rid}: {blob.Sha256}");
        }
        var manifest = JsonSerializer.Serialize(new ReleaseManifest(version, uploaded.ToArray(), product == Releases.Daemon ? null : product), ProtocolJsonContext.Default.ReleaseManifest);
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(keyFile));
        var signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(manifest), HashAlgorithmName.SHA256));
        using var published = await http.PostAsJsonAsync($"/api/v1/{product}-releases", new PublishRelease(manifest, signature, channel),
            ProtocolJsonContext.Default.PublishRelease, cancellation);
        if (!published.IsSuccessStatusCode)
        {
            error.WriteLine($"Publishing failed ({(int)published.StatusCode}): {await published.Content.ReadAsStringAsync(cancellation)}");
            return 1;
        }
        output.WriteLine($"Published {product} {version} on {channel}.");
        return 0;
    }
}
