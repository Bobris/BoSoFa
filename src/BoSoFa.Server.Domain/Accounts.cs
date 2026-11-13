using System.Text.Json;
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

/// <summary>The limits an account's latest turn reported, apart from the account because they change with every turn.</summary>
public class HarnessAccountUsage
{
    [PrimaryKey(1)] public ulong AccountId { get; set; }

    /// <summary>An <see cref="AccountUsageDto"/> as JSON, so a new kind of limit window needs no schema change.</summary>
    public string LimitsJson { get; set; } = "";
    public DateTime LimitsAt { get; set; }
}

public interface IHarnessAccountUsageTable : IRelation<HarnessAccountUsage>
{
    HarnessAccountUsage? FindByIdOrDefault(ulong accountId);
    bool RemoveById(ulong accountId);
}

public static class AccountUsages
{
    /// <summary>Sessions of one account report independently, so an older report never replaces a newer one.</summary>
    internal static void Record(ulong accountId, AccountUsageReport report)
    {
        if (accountId == 0 || Ambient.Relation<IHarnessAccountTable>().FindByIdOrDefault(accountId) == null) return;
        var table = Ambient.Relation<IHarnessAccountUsageTable>();
        var row = table.FindByIdOrDefault(accountId) ?? new HarnessAccountUsage { AccountId = accountId };
        if (report.At < row.LimitsAt) return;
        row.LimitsAt = report.At;
        row.LimitsJson = JsonSerializer.Serialize(new AccountUsageDto(report.FiveHour, report.SevenDay, report.At),
            ProtocolJsonContext.Default.AccountUsageDto);
        table.Upsert(row);
    }

    /// <summary>Must run inside a query scope.</summary>
    public static AccountUsageDto? Of(ulong accountId) =>
        Ambient.Relation<IHarnessAccountUsageTable>().FindByIdOrDefault(accountId) is { } row
            ? JsonSerializer.Deserialize(row.LimitsJson, ProtocolJsonContext.Default.AccountUsageDto)
            : null;
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

public sealed class HarnessAccountAddedHandler(IDomainSignals signals) : IEventHandler<HarnessAccountAdded, AccountCommandResult>
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
        Audit.Record(ev.When, ev.OwnerId, "account.added", account.Label, ev.Kind.ToString());
        signals.SecretsChanged();
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
        Ambient.Relation<IHarnessAccountUsageTable>().RemoveById(account.Id);
        Audit.Record(ev.When, ev.UserId, "account.removed", account.Label);
        return new() { Outcome = CommandOutcome.Ok, AccountId = account.Id };
    }
}
