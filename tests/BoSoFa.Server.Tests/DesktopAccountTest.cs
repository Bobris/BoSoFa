using System.Net;
using System.Net.Http.Json;
using System.Web;
using BoSoFa.Desktop;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

public class DesktopAccountTest
{
    sealed class MemoryVault : ITokenVault
    {
        public string? Token { get; private set; }
        public Task<string?> ReadAsync(CancellationToken cancellation) => Task.FromResult(Token);

        public Task WriteAsync(string token, CancellationToken cancellation)
        {
            Token = token;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellation)
        {
            Token = null;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task TheAppSignsInKeepsItsTokenAndSignsOut()
    {
        await using var server = await TestServer.StartAsync();
        using var browser = server.Client(new CookieContainer());
        (await browser.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", null))).EnsureSuccessStatusCode();
        var data = Directory.CreateTempSubdirectory("bosofa-desktop-").FullName;

        // What the person does in the system browser.
        async Task ApproveAsync(Uri authorize)
        {
            var query = HttpUtility.ParseQueryString(authorize.Query);
            var approved = await (await browser.PostAsJsonAsync("/auth/desktop/approve", new DesktopApproveRequest(query["redirect_uri"]!, query["code_challenge"]!, query["state"]!)))
                .Content.ReadFromJsonAsync<DesktopApproveResponse>();
            using var client = new HttpClient();
            (await client.GetAsync(approved!.Redirect)).EnsureSuccessStatusCode();
        }

        var vault = new MemoryVault();
        using var account = new DesktopAccount(data, vault, ApproveAsync);
        Assert.False(await account.IsSignedInAsync(default));
        Assert.Equal("That is not a server address.", await account.SignInAsync("not a url", default));
        Assert.Null(await account.SignInAsync(server.BaseAddress.ToString(), default));
        Assert.StartsWith("bsr_", vault.Token);
        Assert.True(await account.IsSignedInAsync(default));

        // Started again: nothing in memory, the refresh token from the vault, and a new one in its place.
        var first = vault.Token;
        using var again = new DesktopAccount(data, vault, ApproveAsync);
        Assert.Equal(server.BaseAddress.GetLeftPart(UriPartial.Authority), again.Server);
        var tokens = await again.AccessTokenAsync(default);
        Assert.NotEqual(first, vault.Token);
        using var app = server.Client();
        app.DefaultRequestHeaders.Authorization = new("Bearer", tokens!.AccessToken);
        Assert.Equal("boris@example.com", (await app.GetFromJsonAsync<UserDto>("/api/v1/me"))!.Email);

        var blob = await (await browser.PostAsync("/api/v1/blobs", new ByteArrayContent("report"u8.ToArray()))).Content.ReadFromJsonAsync<BlobRef>();
        var folder = Path.Combine(data, "downloads");
        var saved = await again.SaveBlobAsync(blob!.Sha256, "../report.txt", folder, default);
        Assert.Equal(Path.Combine(folder, "report.txt"), saved);
        Assert.Equal(Path.Combine(folder, "report (1).txt"), await again.SaveBlobAsync(blob.Sha256, "report.txt", folder, default));

        await again.SignOutAsync(default);
        Assert.Null(vault.Token);
        Assert.False(await again.IsSignedInAsync(default));
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public void ThePagesPolicyNamesOnlyTheServer()
    {
        Assert.Contains("connect-src 'self';", SpaProvider.ContentSecurityPolicy(null));
        Assert.Contains("connect-src 'self' https://bosofa.example.com wss://bosofa.example.com;", SpaProvider.ContentSecurityPolicy("https://bosofa.example.com"));
        Assert.Contains("connect-src 'self' http://localhost:5080 ws://localhost:5080;", SpaProvider.ContentSecurityPolicy("http://localhost:5080"));
    }
}
