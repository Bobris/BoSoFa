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
    public static readonly string Version =
        typeof(Api).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static void MapApiEndpoints(this WebApplication app, ServerOptions options)
    {
        var api = app.MapGroup("/api/v1");
        api.MapGet("/version", () => new VersionInfo("server", Version, ProtocolVersion.Current));
        // Without a sign-in: the sign-in page and the install page show it too.
        var branding = new Branding(options.Branding.Name?.Trim() is { Length: > 0 } name ? name : Brand.Name, options.Branding.Tagline?.Trim() ?? "",
            Brand.Daemon);
        api.MapGet("/branding", () => branding);

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

/// <summary>What this server calls itself in the UI (configuration <c>BoSoFa:Branding</c>).</summary>
/// <param name="Name">The short product name.</param>
/// <param name="Tagline">Shown beside the name; empty for none.</param>
/// <param name="Daemon">The daemon's command, fixed by the build's brand.</param>
public sealed record Branding(string Name, string Tagline, string Daemon);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(DevLoginRequest))]
[JsonSerializable(typeof(AuthProviders))]
[JsonSerializable(typeof(Branding))]
[JsonSerializable(typeof(BoSoFa.Server.Agents.ConversationItem))]
[JsonSerializable(typeof(BoSoFa.Server.Agents.AgentInstructionsRecord))]
[JsonSerializable(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))]
internal sealed partial class ServerJsonContext : JsonSerializerContext;
