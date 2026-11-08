using System.Net;
using System.Net.Http.Json;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

public class ServerTest
{
    static async Task<HttpClient> SignIn(TestServer server, string email, string name)
    {
        var client = server.Client();
        var response = await client.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest(email, name));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return client;
    }

    [Fact]
    public async Task VersionIsAnonymousAndTheApiRefusesAnonymousCallers()
    {
        await using var server = await TestServer.StartAsync();
        using var client = server.Client();
        var version = await client.GetFromJsonAsync<VersionInfo>("/api/v1/version");
        Assert.Equal(("server", ProtocolVersion.Current), (version!.Component, version.ProtocolVersion));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/missing")).StatusCode);
    }

    [Fact]
    public async Task FirstSignedInUserIsAdminAndEveryUserSeesTheOthers()
    {
        await using var server = await TestServer.StartAsync();
        using var boris = await SignIn(server, "boris@example.com", "Boris");
        using var anna = await SignIn(server, "Anna@Example.com", "Anna");
        Assert.Equal(new UserDto(1, "Boris", "boris@example.com", UserRole.Admin),
            await boris.GetFromJsonAsync<UserDto>("/api/v1/me"));
        Assert.Equal(UserRole.Member, (await anna.GetFromJsonAsync<UserDto>("/api/v1/me"))!.Role);
        var users = await anna.GetFromJsonAsync<UserDto[]>("/api/v1/users");
        Assert.Equal(["Anna", "Boris"], users!.Select(u => u.Name));

        // Signing in again updates the same person instead of creating another one.
        using var again = await SignIn(server, "anna@example.com", "Anna Nová");
        Assert.Equal(2ul, (await again.GetFromJsonAsync<UserDto>("/api/v1/me"))!.Id);
        Assert.Equal(2, (await again.GetFromJsonAsync<UserDto[]>("/api/v1/users"))!.Length);
    }

    [Fact]
    public async Task UsersSurviveARestart()
    {
        await using var server = await TestServer.StartAsync();
        using (await SignIn(server, "boris@example.com", "Boris")) { }
        await server.RestartAsync(); // the restarted server restores the database from its published history
        using var anna = await SignIn(server, "anna@example.com", "Anna");
        var users = await anna.GetFromJsonAsync<UserDto[]>("/api/v1/users");
        Assert.Equal([new UserDto(2, "Anna", "anna@example.com", UserRole.Member),
            new UserDto(1, "Boris", "boris@example.com", UserRole.Admin)], users!);
    }
}
