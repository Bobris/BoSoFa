using BoSoFa.Protocol;
using BTDB.ODBLayer;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Domain;

/// <summary>A harness account (plan §5.4.1). The secret is stored only as Data Protection ciphertext, made by the
/// server before publishing with <see cref="SecretId"/> as its purpose, so neither the event log nor this row holds it
/// in the clear; only the key ring (from outside) can open it.</summary>
public class HarnessAccount
{
    [PrimaryKey(1)] public ulong Id { get; set; }
    public ulong OwnerId { get; set; }
    public AccountKind Kind { get; set; }
    public string Label { get; set; } = "";
    public string SecretId { get; set; } = "";
    public string ProtectedSecret { get; set; } = "";
    public string SecretHint { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

public interface IHarnessAccountTable : IRelation<HarnessAccount>
{
    void Insert(HarnessAccount account);
    HarnessAccount? FindByIdOrDefault(ulong id);
    bool RemoveById(ulong id);
}

[BTDB.Generate]
public class AccountCommandResult
{
    public CommandOutcome Outcome { get; set; }
    public ulong AccountId { get; set; }
}

public class HarnessAccountAdded : EventWithResult<AccountCommandResult>
{
    public ulong OwnerId { get; set; }
    public AccountKind Kind { get; set; }
    public string Label { get; set; } = "";
    public string SecretId { get; set; } = "";
    public string ProtectedSecret { get; set; } = "";
    public string SecretHint { get; set; } = "";
}

public class HarnessAccountRemoved : EventWithResult<AccountCommandResult>
{
    public ulong AccountId { get; set; }
    public ulong UserId { get; set; }
}

public sealed class HarnessAccountAddedHandler : IEventHandler<HarnessAccountAdded, AccountCommandResult>
{
    /// <summary>A <c>claude setup-token</c> token lasts one year.</summary>
    public static readonly TimeSpan ClaudeTokenLifetime = TimeSpan.FromDays(365);

    public AccountCommandResult? Validate(HarnessAccountAdded ev) =>
        string.IsNullOrWhiteSpace(ev.Label) || ev.Label.Length > 100 || string.IsNullOrEmpty(ev.SecretId) ||
        string.IsNullOrEmpty(ev.ProtectedSecret)
            ? new() { Outcome = CommandOutcome.Invalid }
            : null;

    public AccountCommandResult Consume(HarnessAccountAdded ev)
    {
        if (Ambient.Relation<IUserTable>().FindByIdOrDefault(ev.OwnerId) == null) return new() { Outcome = CommandOutcome.NotFound };
        var account = new HarnessAccount
        {
            Id = Ambient.AllocateId(), OwnerId = ev.OwnerId, Kind = ev.Kind, Label = ev.Label.Trim(), SecretId = ev.SecretId,
            ProtectedSecret = ev.ProtectedSecret, SecretHint = ev.SecretHint, CreatedAt = ev.When,
            ExpiresAt = ev.Kind == AccountKind.ClaudeOAuthToken ? ev.When + ClaudeTokenLifetime : null
        };
        Ambient.Relation<IHarnessAccountTable>().Insert(account);
        return new() { Outcome = CommandOutcome.Ok, AccountId = account.Id };
    }
}

public sealed class HarnessAccountRemovedHandler : IEventHandler<HarnessAccountRemoved, AccountCommandResult>
{
    public AccountCommandResult? Validate(HarnessAccountRemoved ev) => null;

    /// <summary>Removes the row; new leases fail at once. The ciphertext stays in the old event, readable only with the
    /// key ring (plan §5.4.1).</summary>
    public AccountCommandResult Consume(HarnessAccountRemoved ev)
    {
        var accounts = Ambient.Relation<IHarnessAccountTable>();
        var account = accounts.FindByIdOrDefault(ev.AccountId);
        if (account == null) return new() { Outcome = CommandOutcome.NotFound };
        if (account.OwnerId != ev.UserId) return new() { Outcome = CommandOutcome.Forbidden };
        accounts.RemoveById(account.Id);
        return new() { Outcome = CommandOutcome.Ok, AccountId = account.Id };
    }
}
