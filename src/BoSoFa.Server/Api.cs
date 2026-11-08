using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server;

public static class Api
{
    static readonly string Version =
        typeof(Api).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static void MapApiEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");
        api.MapGet("/version", () => new VersionInfo("server", Version, ProtocolVersion.Current));

        var authorized = api.MapGroup("").RequireAuthorization();
        authorized.MapGet("/me", async Task<Results<Ok<UserDto>, UnauthorizedHttpResult>> (HttpContext http, BtdbNode node) =>
        {
            var publisher = await node.Ready.WaitAsync(http.RequestAborted);
            using var query = publisher.StartQuery();
            return http.User.UserId() is { } id && Ambient.Relation<IUserTable>().FindByIdOrDefault(id) is { } user
                ? TypedResults.Ok(user.ToDto())
                : TypedResults.Unauthorized();
        });
        authorized.MapGet("/users", async (HttpContext http, BtdbNode node) =>
        {
            var publisher = await node.Ready.WaitAsync(http.RequestAborted);
            using var query = publisher.StartQuery();
            return Ambient.Relation<IUserTable>().Select(user => user.ToDto()).OrderBy(user => user.Name).ToArray();
        });
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(DevLoginRequest))]
[JsonSerializable(typeof(AuthProviders))]
[JsonSerializable(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))]
internal sealed partial class ServerJsonContext : JsonSerializerContext;
