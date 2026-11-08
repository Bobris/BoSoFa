using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using BoSoFa.Protocol;

namespace BoSoFa.Desktop;

public sealed record LocalDaemonStatus(bool Enrolled, bool Installed, double? MachineId, string? Server);

/// <summary>A command the app runs, for tests to replace: its exit code and output.</summary>
public interface ICommandRunner
{
    /// <param name="onOutputLine">Told each line of standard output as it comes.</param>
    Task<(int Code, string Output, string Error)> RunAsync(string executable, string[] arguments, Action<string>? onOutputLine, CancellationToken cancellation);
}

public sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<(int Code, string Output, string Error)> RunAsync(string executable, string[] arguments, Action<string>? onOutputLine, CancellationToken cancellation)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = new System.Text.StringBuilder();
        var error = process.StandardError.ReadToEndAsync(cancellation);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellation) is { } line)
            {
                output.AppendLine(line);
                onOutputLine?.Invoke(line);
            }
            await process.WaitForExitAsync(cancellation);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return (process.ExitCode, output.ToString(), await error);
    }
}

/// <summary>
/// The daemon of this computer, as the "enable this machine" button sees it. It is driven through its own command line;
/// its binary comes signed from the server's releases, and the person's signed-in app approves its enrollment.
/// </summary>
public sealed partial class LocalDaemon(DesktopAccount account, string dataRoot, string daemonDirectory, ICommandRunner runner, HttpClient http, string? releaseKey,
    string? configuredExecutable = null, string[]? extraInstallArguments = null)
{
    static string ExecutableName => OperatingSystem.IsWindows() ? "bosofad.exe" : "bosofad";

    public static string? EmbeddedReleaseKey => typeof(LocalDaemon).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "BoSoFaReleasePublicKey")?.Value;

    /// <summary>The per-user folder the daemon uses by default.</summary>
    public static string DefaultDirectory()
    {
        if (OperatingSystem.IsWindows()) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoSoFa");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Application Support", "BoSoFa");
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".local", "share") : xdg, "bosofa");
    }

    static string CurrentRid() => DaemonReleases.RidOf(
        OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux",
        RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());

    public LocalDaemonStatus Status()
    {
        double? machineId = null;
        string? server = null;
        var statePath = Path.Combine(daemonDirectory, "daemon.json");
        if (File.Exists(statePath))
        {
            using var state = JsonDocument.Parse(File.ReadAllText(statePath));
            machineId = state.RootElement.GetProperty("machineId").GetDouble();
            server = state.RootElement.GetProperty("server").GetString()?.TrimEnd('/');
        }
        var versionsPath = Path.Combine(daemonDirectory, "versions", "state.json");
        var installed = false;
        if (File.Exists(versionsPath))
        {
            using var versions = JsonDocument.Parse(File.ReadAllText(versionsPath));
            installed = versions.RootElement.GetProperty("current").ValueKind == JsonValueKind.String;
        }
        return new(machineId != null, installed, machineId, server);
    }

    /// <summary>A daemon binary to run: the one in use, or a signed release from the server. The text when there is none.</summary>
    async Task<(string? Executable, string? Problem)> FindExecutableAsync(CancellationToken cancellation)
    {
        if (configuredExecutable != null) return (configuredExecutable, null);
        var installed = Path.Combine(daemonDirectory, ExecutableName);
        if (File.Exists(installed)) return (installed, null);
        if (releaseKey == null) return (null, "This build of the app cannot check daemon releases.");
        if (account.Server == null || await account.AccessTokenAsync(cancellation) is not { } tokens) return (null, "Sign in first.");
        var rid = CurrentRid();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(account.Server), $"/api/v1/daemon-releases/latest?rid={rid}"));
        request.Headers.Authorization = new("Bearer", tokens.AccessToken);
        using var response = await http.SendAsync(request, cancellation);
        if (!response.IsSuccessStatusCode) return (null, $"The server has no daemon release for {rid}.");
        var release = (await response.Content.ReadFromJsonAsync(ProtocolJsonContext.Default.SignedDaemonRelease, cancellation))!;
        if (!DaemonReleases.Verify(releaseKey, release.Manifest, release.Signature)) return (null, "The server's daemon release is not properly signed.");
        var manifest = JsonSerializer.Deserialize(release.Manifest, ProtocolJsonContext.Default.DaemonManifest);
        if (manifest?.Artifacts.FirstOrDefault(artifact => artifact.Rid == rid) is not { } artifact || !DaemonReleases.IsValidVersion(manifest.Version))
            return (null, $"The server has no daemon release for {rid}.");
        var directory = Path.Combine(dataRoot, "daemon", manifest.Version);
        var target = Path.Combine(directory, ExecutableName);
        Directory.CreateDirectory(directory);
        using var download = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(account.Server), "/api/v1/blobs/" + artifact.Sha256));
        download.Headers.Authorization = new("Bearer", tokens.AccessToken);
        using var content = await http.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, cancellation);
        content.EnsureSuccessStatusCode();
        var temporary = target + ".tmp";
        await using (var file = File.Create(temporary)) await content.Content.CopyToAsync(file, cancellation);
        string hash;
        await using (var written = File.OpenRead(temporary))
            hash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(written, cancellation));
        if (hash != artifact.Sha256)
        {
            File.Delete(temporary);
            return (null, "The downloaded daemon does not match its release.");
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.Move(temporary, target, true);
        return (target, null);
    }

    [GeneratedRegex(@"approve the code (\S+)")]
    private static partial Regex CodeLine();

    /// <summary>Makes this computer a machine of the signed-in person. Null when it worked, else what to tell them.</summary>
    public async Task<string?> EnableAsync(CancellationToken cancellation)
    {
        if (account.Server is not { } server) return "Sign in first.";
        var status = Status();
        if (status.Enrolled && status.Server != server.TrimEnd('/')) return $"This computer already belongs to {status.Server}.";
        var (executable, problem) = await FindExecutableAsync(cancellation);
        if (executable == null) return problem;
        if (!status.Enrolled)
        {
            string[] enroll = ["enroll", "--server", server, "--name", Environment.MachineName, "--data-dir", daemonDirectory];
            using var enrolling = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            Task? approving = null;
            try
            {
                var (code, _, error) = await runner.RunAsync(executable, enroll, line =>
                {
                    if (CodeLine().Match(line) is { Success: true } match) approving = ApproveAsync(match.Groups[1].Value, enrolling);
                }, enrolling.Token);
                if (approving != null) await approving;
                if (code != 0) return $"Enrolling this computer failed. {error}".Trim();
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && approving != null)
            {
                await approving; // it says why
                throw;
            }
        }
        if (status.Installed) return null;
        var (installCode, _, installError) = await runner.RunAsync(executable, ["install", "--scope", "user", "--data-dir", daemonDirectory, ..extraInstallArguments ?? []], null, cancellation);
        return installCode == 0 ? null : $"Installing the service failed. {installError}".Trim();
    }

    /// <summary>When this fails, nobody else will approve the code, so the waiting enroll is stopped.</summary>
    async Task ApproveAsync(string code, CancellationTokenSource enrolling)
    {
        try
        {
            var tokens = await account.AccessTokenAsync(enrolling.Token) ?? throw new InvalidOperationException("Not signed in.");
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(account.Server!), $"/api/v1/enrollments/{code}/approve"));
            request.Headers.Authorization = new("Bearer", tokens.AccessToken);
            using var response = await http.SendAsync(request, enrolling.Token);
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            await enrolling.CancelAsync();
            throw;
        }
    }

    /// <summary>Removes the service; the machine stays enrolled.</summary>
    public async Task<string?> DisableAsync(CancellationToken cancellation)
    {
        var (executable, problem) = await FindExecutableAsync(cancellation);
        if (executable == null) return problem;
        var (code, _, error) = await runner.RunAsync(executable, ["uninstall", "--scope", "user", "--data-dir", daemonDirectory, ..extraInstallArguments ?? []], null, cancellation);
        if (code != 0) return error;
        var state = Path.Combine(daemonDirectory, "versions", "state.json");
        if (File.Exists(state) && (extraInstallArguments is not { Length: > 0 })) File.Delete(state);
        return null;
    }

    /// <summary>Runs the official Claude sign-in and stores the resulting token on the server. It is never shown.</summary>
    public async Task<string?> AddClaudeAccountAsync(string label, CancellationToken cancellation)
    {
        if (account.Server is not { } server || await account.AccessTokenAsync(cancellation) is null) return "Sign in first.";
        var (executable, problem) = await FindExecutableAsync(cancellation);
        if (executable == null) return problem;
        var (code, output, error) = await runner.RunAsync(executable, ["claude-setup-token"], null, cancellation);
        if (code != 0) return error.Trim();
        var tokens = await account.AccessTokenAsync(cancellation) ?? throw new InvalidOperationException("Not signed in.");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(server), "/api/v1/accounts"))
        {
            Content = JsonContent.Create(new CreateHarnessAccountRequest(AccountKind.ClaudeOAuthToken, label, output.Trim()), ProtocolJsonContext.Default.CreateHarnessAccountRequest)
        };
        request.Headers.Authorization = new("Bearer", tokens.AccessToken);
        using var response = await http.SendAsync(request, cancellation);
        return response.IsSuccessStatusCode ? null : "The server refused the account: " + await response.Content.ReadAsStringAsync(cancellation);
    }
}
