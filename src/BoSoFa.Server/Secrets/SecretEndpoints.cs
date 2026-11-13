using System.Globalization;
using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;

namespace BoSoFa.Server.Secrets;

/// <summary>
/// Secrets and variables (docs/secrets.md): people set, rotate and delete their own, admins the shared ones. A secret's value
/// is Data Protection ciphertext at rest, bound to its id and version, and never goes back to a browser; a variable's value is
/// shown.
/// </summary>
public static class SecretEndpoints
{
    const string Purpose = "BoSoFa.Secret.v1";

    public static void MapSecretEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1/secrets").RequireAuthorization();
        api.MapGet("", ListAsync);
        api.MapPut("/{scope}/{name}", SetAsync);
        api.MapDelete("/{scope}/{name}", DeleteAsync);
    }

    static IDataProtector Protector(IDataProtectionProvider protection, string secretId, int version) =>
        protection.CreateProtector(Purpose, secretId, version.ToString(CultureInfo.InvariantCulture));

    internal static string Protect(IDataProtectionProvider protection, string secretId, int version, string value) =>
        Protector(protection, secretId, version).Protect(value);

    /// <summary>A value of a secret's version, as <see cref="Protect"/> made it.</summary>
    internal static string Open(IDataProtectionProvider protection, string secretId, int version, string protectedValue) =>
        Protector(protection, secretId, version).Unprotect(protectedValue);

    /// <summary>The value of a secret or a variable, for resolving a session; it stays in memory on this node.</summary>
    internal static string ValueOf(SecretEntry secret, IDataProtectionProvider protection) => secret.Kind == SecretKind.Secret
        ? Protector(protection, secret.SecretId, secret.Version).Unprotect(secret.ProtectedValue)
        : secret.Value;

    /// <summary>Must run inside a query scope.</summary>
    static SecretDto ToDto(SecretEntry secret) => new(secret.Kind, secret.OwnerId == 0 ? SecretScope.Shared : SecretScope.Personal, secret.Name,
        secret.Version, secret.Kind == SecretKind.Secret ? secret.Hint : null, secret.Kind == SecretKind.Variable ? secret.Value : null,
        secret.SetById == 0 ? "the server" : Ambient.Relation<IUserTable>().FindByIdOrDefault(secret.SetById)?.Name ?? "", secret.SetAt,
        secret.HostOnly);

    static ulong OwnerOf(SecretScope scope, HttpContext http) => scope == SecretScope.Shared ? 0 : http.User.UserId() ?? 0;

    /// <summary>The caller's own values, then the shared ones.</summary>
    static async Task<SecretDto[]> ListAsync(BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        var table = Ambient.Relation<ISecretTable>();
        var userId = http.User.UserId() ?? 0;
        return table.ListById(userId).Concat(userId == 0 ? [] : table.ListById(0))
            .Select(ToDto).OrderBy(secret => secret.Scope).ThenBy(secret => secret.Name, StringComparer.Ordinal).ToArray();
    }

    static async Task<Results<Ok<SecretDto>, ForbidHttpResult, BadRequest<string>, Conflict<string>>> SetAsync(SecretScope scope, string name,
        SetSecretRequest request, BtdbNode node, IDataProtectionProvider protection, HttpContext http)
    {
        if (!SecretNames.IsValid(name))
            return TypedResults.BadRequest("A name is letters, digits and _, not starting with a digit, at most 64 characters.");
        var value = request.Value.Trim();
        if (value.Length == 0) return TypedResults.BadRequest("A value is required.");
        if (value.Length > SecretNames.MaxValueLength) return TypedResults.BadRequest($"A value is at most {SecretNames.MaxValueLength / 1024} KB.");
        if (request.HostOnly && request.Kind != SecretKind.Secret) return TypedResults.BadRequest("Only a secret can be for MCP servers only.");
        var owner = OwnerOf(scope, http);
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        // A rotation is the next version of what is there; two at once make one of them try again.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            string secretId;
            int version;
            using (publisher.StartQuery())
            {
                var current = Ambient.Relation<ISecretTable>().FindByIdOrDefault(owner, name);
                if (current != null && current.Kind != request.Kind)
                    return TypedResults.BadRequest($"{name} is a {Word(current.Kind)}: delete it to set a {Word(request.Kind)} of that name.");
                secretId = current?.SecretId ?? Guid.NewGuid().ToString("N");
                version = (current?.Version ?? 0) + 1;
            }
            var secret = request.Kind == SecretKind.Secret;
            var result = await publisher.PublishAndWaitAsync(new SecretSet
            {
                UserId = http.User.UserId() ?? 0, OwnerId = owner, Name = name, Kind = request.Kind, SecretId = secretId, Version = version,
                ProtectedValue = secret ? Protect(protection, secretId, version, value) : "", Value = secret ? "" : value,
                Hint = secret ? AccountSecrets.Hint(value) : "", HostOnly = request.HostOnly
            }, http.RequestAborted);
            if (result.Conflict) continue;
            switch (result.Outcome)
            {
                case CommandOutcome.Forbidden: return TypedResults.Forbid();
                case CommandOutcome.Ok: break;
                default: return TypedResults.BadRequest("The value could not be set.");
            }
            using var query = publisher.StartQuery();
            return TypedResults.Ok(ToDto(Ambient.Relation<ISecretTable>().FindByIdOrDefault(owner, name)!));
        }
        return TypedResults.Conflict($"{name} was changed meanwhile; try again.");
    }

    static string Word(SecretKind kind) => kind == SecretKind.Secret ? "secret" : "variable";

    static async Task<Results<NoContent, NotFound, ForbidHttpResult>> DeleteAsync(SecretScope scope, string name, BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        var result = await publisher.PublishAndWaitAsync(new SecretDeleted
        {
            UserId = http.User.UserId() ?? 0, OwnerId = OwnerOf(scope, http), Name = name
        }, http.RequestAborted);
        return result.Outcome switch
        {
            CommandOutcome.Ok => TypedResults.NoContent(),
            CommandOutcome.Forbidden => TypedResults.Forbid(),
            _ => TypedResults.NotFound()
        };
    }
}
