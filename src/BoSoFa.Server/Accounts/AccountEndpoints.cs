using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BoSoFa.Server.Machines;
using BoSoFa.Server.Tokens;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;

namespace BoSoFa.Server.Accounts;

/// <summary>Harness accounts (plan §5.4.1): people add and remove their own; a daemon leases one account's secret for
/// a session it runs. Secrets are Data Protection ciphertext at rest and never go back to a browser.</summary>
public static class AccountEndpoints
{
    const string Purpose = "BoSoFa.HarnessAccount.v1";

    public static void MapAccountEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1/accounts").RequireAuthorization();
        api.MapGet("", ListAsync);
        api.MapPost("", AddAsync);
        api.MapDelete("/{id}", RemoveAsync);
        app.MapPost("/api/daemon/v1/accounts/{id}/lease", LeaseAsync).RequireAuthorization(DaemonTokens.Scheme);
    }

    /// <summary>Must run inside a query scope.</summary>
    internal static HarnessAccountDto ToDto(HarnessAccount account) => new(account.Id, account.Kind,
        AccountSecrets.HarnessOf(account.Kind), account.Label, account.OwnerId,
        Ambient.Relation<IUserTable>().FindByIdOrDefault(account.OwnerId)?.Name ?? "", account.CreatedAt,
        account.ExpiresAt, account.SecretHint);

    static IDataProtector Protector(IDataProtectionProvider protection, string secretId) =>
        protection.CreateProtector(Purpose, secretId);

    static async Task<HarnessAccountDto[]> ListAsync(BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        var userId = http.User.UserId() ?? 0;
        return Ambient.Relation<IHarnessAccountTable>().Where(account => account.OwnerId == userId)
            .OrderBy(account => account.Label, StringComparer.OrdinalIgnoreCase).Select(ToDto).ToArray();
    }

    static async Task<Results<Ok<HarnessAccountDto>, BadRequest<string>>> AddAsync(CreateHarnessAccountRequest request,
        BtdbNode node, IDataProtectionProvider protection, HttpContext http)
    {
        var secret = request.Secret.Trim();
        if (!AccountSecrets.LooksValid(request.Kind, secret))
            return TypedResults.BadRequest($"That does not look like a {request.Kind} secret.");
        if (string.IsNullOrWhiteSpace(request.Label)) return TypedResults.BadRequest("A label is required.");
        var secretId = Guid.NewGuid().ToString("N");
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        var result = await publisher.PublishAndWaitAsync(new HarnessAccountAdded
        {
            OwnerId = http.User.UserId() ?? 0, Kind = request.Kind, Label = request.Label, SecretId = secretId,
            ProtectedSecret = Protector(protection, secretId).Protect(secret), SecretHint = AccountSecrets.Hint(secret)
        }, http.RequestAborted);
        if (result.Outcome != CommandOutcome.Ok) return TypedResults.BadRequest("The account could not be added.");
        using var query = publisher.StartQuery();
        return TypedResults.Ok(ToDto(Ambient.Relation<IHarnessAccountTable>().FindByIdOrDefault(result.AccountId)!));
    }

    static async Task<Results<NoContent, NotFound, ForbidHttpResult>> RemoveAsync(ulong id, BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        var result = await publisher.PublishAndWaitAsync(
            new HarnessAccountRemoved { AccountId = id, UserId = http.User.UserId() ?? 0 }, http.RequestAborted);
        return result.Outcome switch
        {
            CommandOutcome.Ok => TypedResults.NoContent(),
            CommandOutcome.Forbidden => TypedResults.Forbid(),
            _ => TypedResults.NotFound()
        };
    }

    /// <summary>The secret of an account, for the daemon running a session with it: the session must run on this
    /// machine, use this account, belong to the account's owner and not be stopped.</summary>
    static async Task<Results<Ok<AccountLeaseResponse>, NotFound, ForbidHttpResult>> LeaseAsync(ulong id,
        AccountLeaseRequest request, BtdbNode node, IDataProtectionProvider protection, ILoggerFactory loggers,
        HttpContext http)
    {
        var machineId = http.User.MachineId();
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        HarnessAccount account;
        ulong owner;
        using (publisher.StartQuery())
        {
            if (Ambient.Relation<IHarnessAccountTable>().FindByIdOrDefault(id) is not { } found) return TypedResults.NotFound();
            var session = Ambient.Relation<ISessionTable>().FindByIdOrDefault(request.SessionId);
            if (session == null || session.MachineId != machineId || session.AccountId != id ||
                session.CreatedById != found.OwnerId || session.Status == SessionStatus.Stopped)
                return TypedResults.Forbid();
            account = found;
            owner = session.CreatedById;
        }
        var logger = loggers.CreateLogger("BoSoFa.Server.Accounts");
        logger.LogInformation("Account {AccountId} leased to machine {MachineId} for session {SessionId}", id, machineId, request.SessionId);
        await AuditEndpoints.RecordAsync(publisher, owner, "account.leased", account.Label, $"session {request.SessionId} on machine {machineId}", logger);
        return TypedResults.Ok(new AccountLeaseResponse(account.Kind,
            Protector(protection, account.SecretId).Unprotect(account.ProtectedSecret)));
    }
}
