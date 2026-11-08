using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

public class ApiTokenTest
{
    [Fact]
    public async Task TokensActForTheirOwnerWithinTheirScope()
    {
        await using var server = await TestServer.StartAsync();
        using var boris = server.Client(new CookieContainer());
        (await boris.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", null))).EnsureSuccessStatusCode();

        async Task<CreatedApiToken> CreateAsync(ApiTokenScope scope, int? days = null) =>
            (await (await boris.PostAsJsonAsync("/api/v1/tokens", new CreateApiTokenRequest("script", scope, days)))
                .Content.ReadFromJsonAsync<CreatedApiToken>())!;
        HttpClient Bearer(string token)
        {
            var client = server.Client();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        var read = await CreateAsync(ApiTokenScope.Read);
        var control = await CreateAsync(ApiTokenScope.Control, 30);
        Assert.StartsWith("bsf_", read.Token);
        Assert.NotNull(control.Details.ExpiresAt);
        Assert.Null(read.Details.ExpiresAt);
        Assert.Equal(2, (await boris.GetFromJsonAsync<ApiTokenDto[]>("/api/v1/tokens"))!.Length);
        Assert.Equal(HttpStatusCode.BadRequest, (await boris.PostAsJsonAsync("/api/v1/tokens", new CreateApiTokenRequest(" ", ApiTokenScope.Read))).StatusCode);

        using var reader = Bearer(read.Token);
        using var controller = Bearer(control.Token);
        using var nobody = Bearer("bsf_not-a-token");
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/machines")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await nobody.GetAsync("/api/v1/machines")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/machines/1/files/roots")).StatusCode); // nothing on a machine
        var create = new CreateHarnessAccountRequest(AccountKind.AnthropicApiKey, "Team key", "sk-ant-api03-" + new string('a', 40));
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/accounts", create)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await controller.PostAsJsonAsync("/api/v1/accounts", create)).StatusCode);
        Assert.Single((await controller.GetFromJsonAsync<HarnessAccountDto[]>("/api/v1/accounts"))!); // its owner's rights

        // A leaked token cannot mint or list tokens.
        Assert.Equal(HttpStatusCode.Forbidden, (await controller.GetAsync("/api/v1/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await controller.PostAsJsonAsync("/api/v1/tokens", new CreateApiTokenRequest("more", ApiTokenScope.Control))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await controller.DeleteAsync($"/api/v1/tokens/{read.Details.Id}")).StatusCode);

        using var anna = server.Client(new CookieContainer());
        (await anna.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("anna@example.com", null))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await anna.DeleteAsync($"/api/v1/tokens/{read.Details.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await boris.DeleteAsync($"/api/v1/tokens/{read.Details.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await reader.GetAsync("/api/v1/machines")).StatusCode);
    }
}
