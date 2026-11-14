using System.Globalization;
using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BoSoFa.Server.Machines;
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
        api.MapPut("/{scope}/{name}", (SecretScope scope, string name, SetSecretRequest request, BtdbNode node, IDataProtectionProvider protection,
            HttpContext http) => SetAsync(Owner.Of(scope, http), name, request, node, protection, http));
        api.MapPut("/{scope}/{name}/settings", (SecretScope scope, string name, SecretSettingsRequest request, BtdbNode node, HttpContext http) =>
            ChangeSettingsAsync(Owner.Of(scope, http), name, request, node, http));
        api.MapDelete("/{scope}/{name}", (SecretScope scope, string name, BtdbNode node, HttpContext http) =>
            DeleteAsync(Owner.Of(scope, http), name, node, http));
        app.MapGet("/api/daemon/v1/sessions/{id}/secrets", ListForSessionAsync).RequireAuthorization(DaemonTokens.Scheme);
    }

    /// <summary>The names a session on this machine can refer to, for its agent's tool (docs/secrets.md §4.3); never a value.</summary>
    static async Task<Results<Ok<SessionSecretName[]>, NotFound, ForbidHttpResult>> ListForSessionAsync(ulong id, BtdbNode node,
        IDataProtectionProvider protection, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        if (Ambient.Relation<ISessionTable>().FindByIdOrDefault(id) is not { } session) return TypedResults.NotFound();
        if (session.MachineId != http.User.MachineId() || session.Status == SessionStatus.Stopped) return TypedResults.Forbid();
        return TypedResults.Ok(SecretResolver.NamesFor(session, protection));
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

    /// <summary>Must run inside a query scope. Where a value went is shown to whoever may change it.</summary>
    internal static SecretDto ToDto(SecretEntry secret, ulong userId)
    {
        var environment = SecretEnvironments.Find(secret.OwnerId);
        var scope = secret.OwnerId == 0 ? SecretScope.Shared : environment != null ? SecretScope.Environment : SecretScope.Personal;
        var machines = Ambient.Relation<IMachineTable>();
        SecretUseDto[]? usedOn = null;
        if (BoSoFa.Server.Domain.Secrets.MayChange(userId, secret.OwnerId))
            usedOn = Ambient.Relation<ISecretUseTable>().ListById(secret.OwnerId, secret.Name).OrderByDescending(use => use.At)
                .Select(use => new SecretUseDto(use.MachineId, use.MachineId == 0 ? "the server" : machines.FindByIdOrDefault(use.MachineId)?.Name ?? "a removed machine",
                    use.Version, use.At)).ToArray();
        return new(secret.Kind, scope, secret.Name, secret.Version, secret.Kind == SecretKind.Secret ? secret.Hint : null,
            secret.Kind == SecretKind.Variable ? secret.Value : null,
            secret.SetById == 0 ? "the server" : Ambient.Relation<IUserTable>().FindByIdOrDefault(secret.SetById)?.Name ?? "", secret.SetAt,
            secret.HostOnly, environment?.Name, BoSoFa.Server.Domain.Secrets.Ids(secret.Machines), secret.RotateEveryDays,
            secret.RotateEveryDays > 0 ? secret.SetAt.AddDays(secret.RotateEveryDays) : null, secret.RotationReason, usedOn);
    }

    /// <summary>Whose a value is, from the route: the caller's, the shared ones, or an environment's by its name.</summary>
    internal readonly record struct Owner(ulong? Id, string? Environment = null)
    {
        public static Owner Of(SecretScope scope, HttpContext http) => scope switch
        {
            SecretScope.Shared => new(0),
            SecretScope.Personal => new(http.User.UserId() ?? 0),
            _ => new(null) // an environment's values have routes of their own
        };

        /// <summary>The owner id; null when the environment does not exist. Must run inside a query scope.</summary>
        public ulong? Resolve() => Environment == null ? Id : SecretEnvironments.Find(Environment)?.Id;
    }

    /// <summary>The caller's own values, then the shared ones.</summary>
    static async Task<SecretDto[]> ListAsync(BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        var table = Ambient.Relation<ISecretTable>();
        var userId = http.User.UserId() ?? 0;
        return table.ListById(userId).Concat(userId == 0 ? [] : table.ListById(0))
            .Select(secret => ToDto(secret, userId)).OrderBy(secret => secret.Scope).ThenBy(secret => secret.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>What is wrong with a list of machines for values of <paramref name="owner"/>, or null: they exist, and a person's own
    /// values go to their own machines. Must run inside a query scope.</summary>
    static string? MachinesProblem(ulong owner, ulong userId, ulong[] machines)
    {
        var table = Ambient.Relation<IMachineTable>();
        foreach (var id in machines)
        {
            if (table.FindByIdOrDefault(id) is not { } machine) return $"There is no machine {id}.";
            if (owner == userId && machine.OwnerId != userId) return $"{machine.Name} is not your machine: your values go to your machines only.";
        }
        return null;
    }

    static string? RotationProblem(int? days) =>
        days is < 0 or > BoSoFa.Server.Domain.Secrets.MaxRotateEveryDays ? $"Rotate every 0 (never) to {BoSoFa.Server.Domain.Secrets.MaxRotateEveryDays} days." : null;

    internal static async Task<Results<Ok<SecretDto>, ForbidHttpResult, BadRequest<string>, Conflict<string>, NotFound>> SetAsync(Owner owner,
        string name, SetSecretRequest request, BtdbNode node, IDataProtectionProvider protection, HttpContext http)
    {
        if (owner.Id == null && owner.Environment == null)
            return TypedResults.BadRequest("An environment's values are set under /api/v1/environments/{environment}/secrets.");
        if (!SecretNames.IsValid(name))
            return TypedResults.BadRequest("A name is letters, digits and _, not starting with a digit, at most 64 characters.");
        var value = request.Value.Trim();
        if (value.Length == 0) return TypedResults.BadRequest("A value is required.");
        if (value.Length > SecretNames.MaxValueLength) return TypedResults.BadRequest($"A value is at most {SecretNames.MaxValueLength / 1024} KB.");
        if (request.HostOnly && request.Kind != SecretKind.Secret) return TypedResults.BadRequest("Only a secret can be for MCP servers only.");
        if (RotationProblem(request.RotateEveryDays) is { } rotation) return TypedResults.BadRequest(rotation);
        var userId = http.User.UserId() ?? 0;
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        // A rotation is the next version of what is there; two at once make one of them try again.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            string secretId, machines;
            int version, rotateEveryDays;
            ulong ownerId;
            using (publisher.StartQuery())
            {
                if (owner.Resolve() is not { } found) return TypedResults.NotFound();
                ownerId = found;
                var current = Ambient.Relation<ISecretTable>().FindByIdOrDefault(ownerId, name);
                if (current != null && current.Kind != request.Kind)
                    return TypedResults.BadRequest($"{name} is a {Word(current.Kind)}: delete it to set a {Word(request.Kind)} of that name.");
                if (request.Machines != null && MachinesProblem(ownerId, userId, request.Machines) is { } problem) return TypedResults.BadRequest(problem);
                secretId = current?.SecretId ?? Guid.NewGuid().ToString("N");
                version = (current?.Version ?? 0) + 1;
                machines = request.Machines != null ? BoSoFa.Server.Domain.Secrets.Json(request.Machines) : current?.Machines ?? "[]";
                rotateEveryDays = request.RotateEveryDays ?? current?.RotateEveryDays ?? 0;
            }
            var secret = request.Kind == SecretKind.Secret;
            var result = await publisher.PublishAndWaitAsync(new SecretSet
            {
                UserId = userId, OwnerId = ownerId, Name = name, Kind = request.Kind, SecretId = secretId, Version = version,
                ProtectedValue = secret ? Protect(protection, secretId, version, value) : "", Value = secret ? "" : value,
                Hint = secret ? AccountSecrets.Hint(value) : "", HostOnly = request.HostOnly, Machines = machines, RotateEveryDays = rotateEveryDays
            }, http.RequestAborted);
            if (result.Conflict) continue;
            switch (result.Outcome)
            {
                case CommandOutcome.Forbidden: return TypedResults.Forbid();
                case CommandOutcome.Ok: break;
                default: return TypedResults.BadRequest("The value could not be set.");
            }
            using var query = publisher.StartQuery();
            return TypedResults.Ok(ToDto(Ambient.Relation<ISecretTable>().FindByIdOrDefault(ownerId, name)!, userId));
        }
        return TypedResults.Conflict($"{name} was changed meanwhile; try again.");
    }

    /// <summary>Which machines get a value, whether it is for MCP servers only, and when it is due for rotation; its value and
    /// version stay.</summary>
    internal static async Task<Results<Ok<SecretDto>, ForbidHttpResult, BadRequest<string>, NotFound>> ChangeSettingsAsync(Owner owner,
        string name, SecretSettingsRequest request, BtdbNode node, HttpContext http)
    {
        if (owner.Id == null && owner.Environment == null) return TypedResults.NotFound();
        if (RotationProblem(request.RotateEveryDays) is { } rotation) return TypedResults.BadRequest(rotation);
        var userId = http.User.UserId() ?? 0;
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        ulong ownerId;
        using (publisher.StartQuery())
        {
            if (owner.Resolve() is not { } found) return TypedResults.NotFound();
            ownerId = found;
            if (Ambient.Relation<ISecretTable>().FindByIdOrDefault(ownerId, name) is not { } current) return TypedResults.NotFound();
            if (request.HostOnly && current.Kind != SecretKind.Secret) return TypedResults.BadRequest("Only a secret can be for MCP servers only.");
            if (MachinesProblem(ownerId, userId, request.Machines) is { } problem) return TypedResults.BadRequest(problem);
        }
        var result = await publisher.PublishAndWaitAsync(new SecretSettingsChanged
        {
            UserId = userId, OwnerId = ownerId, Name = name, HostOnly = request.HostOnly,
            Machines = BoSoFa.Server.Domain.Secrets.Json(request.Machines), RotateEveryDays = request.RotateEveryDays
        }, http.RequestAborted);
        switch (result.Outcome)
        {
            case CommandOutcome.Forbidden: return TypedResults.Forbid();
            case CommandOutcome.NotFound: return TypedResults.NotFound();
            case CommandOutcome.Ok: break;
            default: return TypedResults.BadRequest("The settings could not be changed.");
        }
        using var query = publisher.StartQuery();
        return TypedResults.Ok(ToDto(Ambient.Relation<ISecretTable>().FindByIdOrDefault(ownerId, name)!, userId));
    }

    static string Word(SecretKind kind) => kind == SecretKind.Secret ? "secret" : "variable";

    internal static async Task<Results<NoContent, NotFound, ForbidHttpResult>> DeleteAsync(Owner owner, string name, BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        ulong ownerId;
        using (publisher.StartQuery())
        {
            if (owner.Resolve() is not { } found) return TypedResults.NotFound();
            ownerId = found;
        }
        var result = await publisher.PublishAndWaitAsync(new SecretDeleted { UserId = http.User.UserId() ?? 0, OwnerId = ownerId, Name = name },
            http.RequestAborted);
        return result.Outcome switch
        {
            CommandOutcome.Ok => TypedResults.NoContent(),
            CommandOutcome.Forbidden => TypedResults.Forbid(),
            _ => TypedResults.NotFound()
        };
    }
}
