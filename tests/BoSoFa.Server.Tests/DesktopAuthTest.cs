using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using BoSoFa.Protocol;
using BoSoFa.Server.Desktop;

namespace BoSoFa.Server.Tests;

public class DesktopAuthTest
{
    const string Redirect = "http://127.0.0.1:43123/callback";

    static async Task<string> ApproveAsync(HttpClient browser, string verifier)
    {
        var response = await browser.PostAsJsonAsync("/auth/desktop/approve", new DesktopApproveRequest(Redirect, DesktopTokenService.ChallengeOf(verifier), "abc"));
        response.EnsureSuccessStatusCode();
        var redirect = new Uri((await response.Content.ReadFromJsonAsync<DesktopApproveResponse>())!.Redirect);
        Assert.StartsWith(Redirect, redirect.ToString());
        Assert.Contains("state=abc", redirect.Query);
        return Uri.UnescapeDataString(redirect.Query.TrimStart('?').Split('&')[0]["code=".Length..]);
    }

    static string Verifier() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public async Task AnAppSignsInThroughTheBrowserAndHoldsRotatingTokens()
    {
        await using var server = await TestServer.StartAsync();
        var cookies = new CookieContainer();
        using var browser = server.Client(cookies);
        (await browser.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", null))).EnsureSuccessStatusCode();
        using var app = server.Client();

        foreach (var bad in new[] { "https://evil.example/cb", "http://evil.example/cb", "bosofa://cb", "http://127.0.0.1.evil.example/" })
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsJsonAsync("/auth/desktop/approve",
                new DesktopApproveRequest(bad, DesktopTokenService.ChallengeOf(Verifier()), "abc"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.PostAsJsonAsync("/auth/desktop/approve",
            new DesktopApproveRequest(Redirect, DesktopTokenService.ChallengeOf(Verifier()), "abc"))).StatusCode);

        var verifier = Verifier();
        var code = await ApproveAsync(browser, verifier);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsJsonAsync("/auth/desktop/token", new DesktopTokenRequest(code, Verifier(), Redirect, "Mac"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsJsonAsync("/auth/desktop/token", new DesktopTokenRequest(code, verifier, "http://127.0.0.1:1/x", "Mac"))).StatusCode);
        var first = (await (await app.PostAsJsonAsync("/auth/desktop/token", new DesktopTokenRequest(code, verifier, Redirect, "Mac"))).Content.ReadFromJsonAsync<DesktopTokens>())!;
        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsJsonAsync("/auth/desktop/token", new DesktopTokenRequest(code, verifier, Redirect, "Again"))).StatusCode); // once

        HttpClient Bearer(string token)
        {
            var client = server.Client();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        using var desktop = Bearer(first.AccessToken);
        Assert.Equal("boris@example.com", (await desktop.GetFromJsonAsync<UserDto>("/api/v1/me"))!.Email);
        Assert.Equal(HttpStatusCode.Forbidden, (await desktop.PostAsJsonAsync("/auth/desktop/approve",
            new DesktopApproveRequest(Redirect, DesktopTokenService.ChallengeOf(Verifier()), "abc"))).StatusCode); // no app can approve another

        var second = (await (await app.PostAsJsonAsync("/auth/desktop/refresh", new DesktopRefreshRequest(first.RefreshToken))).Content.ReadFromJsonAsync<DesktopTokens>())!;
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        using var refreshed = Bearer(second.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await refreshed.GetAsync("/api/v1/me")).StatusCode);

        // An old refresh token coming back ends the session: one of its holders is not the app.
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.PostAsJsonAsync("/auth/desktop/refresh", new DesktopRefreshRequest(first.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await refreshed.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.PostAsJsonAsync("/auth/desktop/refresh", new DesktopRefreshRequest(second.RefreshToken))).StatusCode);

        var again = Verifier();
        var signedIn = (await (await app.PostAsJsonAsync("/auth/desktop/token", new DesktopTokenRequest(await ApproveAsync(browser, again), again, Redirect, "Mac"))).Content.ReadFromJsonAsync<DesktopTokens>())!;
        using var current = Bearer(signedIn.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await current.GetAsync("/api/v1/me")).StatusCode);
        (await app.PostAsJsonAsync("/auth/desktop/revoke", new DesktopRefreshRequest(signedIn.RefreshToken))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await current.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task TheLiveSocketTakesAOneTimeTicketAndChecksTheOrigin()
    {
        await using var server = await TestServer.StartAsync();
        var cookies = new CookieContainer();
        using var browser = server.Client(cookies);
        (await browser.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", null))).EnsureSuccessStatusCode();
        var ticket = (await (await browser.PostAsync(LiveSocket.Path + "/ticket", null)).Content.ReadFromJsonAsync<LiveTicket>())!.Ticket;

        Uri Url(string query) => new UriBuilder(server.BaseAddress) { Scheme = "ws", Path = LiveSocket.Path, Query = query }.Uri;
        async Task<ClientWebSocket> ConnectAsync(string query, string? origin = null)
        {
            var socket = new ClientWebSocket();
            if (origin != null) socket.Options.SetRequestHeader("Origin", origin);
            await socket.ConnectAsync(Url(query), default);
            return socket;
        }

        using var opened = await ConnectAsync("ticket=" + Uri.EscapeDataString(ticket), "app://bosofa");
        Assert.Equal(WebSocketState.Open, opened.State);
        await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync("ticket=" + Uri.EscapeDataString(ticket))); // only once
        await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync("ticket=forged"));

        var another = (await (await browser.PostAsync(LiveSocket.Path + "/ticket", null)).Content.ReadFromJsonAsync<LiveTicket>())!.Ticket;
        await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync("ticket=" + Uri.EscapeDataString(another), "https://evil.example"));
    }

    [Fact]
    public async Task OnlyTheDesktopOriginPassesCors()
    {
        await using var server = await TestServer.StartAsync();
        using var client = server.Client();
        async Task<HttpResponseMessage> PreflightAsync(string origin)
        {
            var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/machines");
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", "GET");
            request.Headers.Add("Access-Control-Request-Headers", "authorization");
            return await client.SendAsync(request);
        }

        var allowed = await PreflightAsync("app://bosofa");
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.Equal("app://bosofa", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(allowed.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.False((await PreflightAsync("https://evil.example")).Headers.Contains("Access-Control-Allow-Origin"));
    }
}
