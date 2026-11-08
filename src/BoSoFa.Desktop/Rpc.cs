using System.Text.Json.Serialization;
using NeoAstra.Rpc;

[assembly: NeoRpcJsonContext(typeof(BoSoFa.Desktop.DesktopRpcJsonContext))]

namespace BoSoFa.Desktop;

public sealed record EmptyRequest;

public sealed record AuthState(string? Server, bool SignedIn);

public sealed record AccessTokenResponse(string? AccessToken, string? ExpiresAt);

public sealed record SignInRequest(string Server);

public sealed record SignInResponse(string? Error);

public sealed record DoneResponse;

public sealed record SaveBlobRequest(string Sha256, string Name);

public sealed record SaveBlobResponse(string? Path);

/// <summary>What the page may ask about the sign-in. The refresh token stays on this side.</summary>
[NeoRpcService("auth", Version = 1)]
public sealed class AuthService
{
    readonly DesktopAccount _account;

    internal AuthService(DesktopAccount account) => _account = account;

    [NeoRpcMethod("state")]
    public async ValueTask<AuthState> StateAsync(EmptyRequest request, CancellationToken cancellationToken) =>
        new(_account.Server, await _account.IsSignedInAsync(cancellationToken));

    [NeoRpcMethod("accessToken")]
    public async ValueTask<AccessTokenResponse> AccessTokenAsync(EmptyRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tokens = await _account.AccessTokenAsync(cancellationToken);
            return new(tokens?.AccessToken, tokens?.ExpiresAt.ToString("O"));
        }
        catch (HttpRequestException) { return new(null, null); } // the server is away for now
    }

    [NeoRpcMethod("signIn")]
    public async ValueTask<SignInResponse> SignInAsync(SignInRequest request, CancellationToken cancellationToken) =>
        new(await _account.SignInAsync(request.Server, cancellationToken));

    [NeoRpcMethod("signOut")]
    public async ValueTask<DoneResponse> SignOutAsync(EmptyRequest request, CancellationToken cancellationToken)
    {
        await _account.SignOutAsync(cancellationToken);
        return new();
    }
}

[NeoRpcService("files", Version = 1)]
public sealed class FilesService
{
    readonly DesktopAccount _account;

    internal FilesService(DesktopAccount account) => _account = account;

    /// <summary>Into the Downloads folder: the page cannot save a file from a bearer-protected address by itself.</summary>
    [NeoRpcMethod("saveBlob")]
    public async ValueTask<SaveBlobResponse> SaveBlobAsync(SaveBlobRequest request, CancellationToken cancellationToken)
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return new(await _account.SaveBlobAsync(request.Sha256, request.Name, downloads, cancellationToken));
    }
}

[JsonSerializable(typeof(EmptyRequest))]
[JsonSerializable(typeof(AuthState))]
[JsonSerializable(typeof(AccessTokenResponse))]
[JsonSerializable(typeof(SignInRequest))]
[JsonSerializable(typeof(SignInResponse))]
[JsonSerializable(typeof(DoneResponse))]
[JsonSerializable(typeof(SaveBlobRequest))]
[JsonSerializable(typeof(SaveBlobResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class DesktopRpcJsonContext : JsonSerializerContext;
