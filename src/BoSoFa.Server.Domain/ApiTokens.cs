using BoSoFa.Protocol;
using BTDB.ODBLayer;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Domain;

/// <summary>A token for scripts (plan §4). Only its SHA-256 is stored; tokens are long random strings, so no salt.</summary>
public class ApiToken
{
    [PrimaryKey(1)] public ulong Id { get; set; }
    [SecondaryKey("Hash")] public string Hash { get; set; } = "";
    public ulong OwnerId { get; set; }
    public string Name { get; set; } = "";
    public ApiTokenScope Scope { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

public interface IApiTokenTable : IRelation<ApiToken>
{
    void Insert(ApiToken token);
    ApiToken? FindByIdOrDefault(ulong id);
    ApiToken? FindByHashOrDefault(string hash);
    bool RemoveById(ulong id);
}

[BTDB.Generate]
public class ApiTokenCommandResult
{
    public CommandOutcome Outcome { get; set; }
    public ulong TokenId { get; set; }
}

public class ApiTokenCreated : EventWithResult<ApiTokenCommandResult>
{
    public ulong OwnerId { get; set; }
    public string Name { get; set; } = "";
    public ApiTokenScope Scope { get; set; }
    public string Hash { get; set; } = "";
    public int? ExpiresInDays { get; set; }
}

public class ApiTokenRevoked : EventWithResult<ApiTokenCommandResult>
{
    public ulong TokenId { get; set; }
    public ulong UserId { get; set; }
}

public sealed class ApiTokenCreatedHandler : IEventHandler<ApiTokenCreated, ApiTokenCommandResult>
{
    public ApiTokenCommandResult? Validate(ApiTokenCreated ev) =>
        string.IsNullOrWhiteSpace(ev.Name) || ev.Name.Length > 100 || ev.Hash.Length != 64 || ev.ExpiresInDays is <= 0 or > 3650
            ? new() { Outcome = CommandOutcome.Invalid }
            : null;

    public ApiTokenCommandResult Consume(ApiTokenCreated ev)
    {
        if (Ambient.Relation<IUserTable>().FindByIdOrDefault(ev.OwnerId) == null) return new() { Outcome = CommandOutcome.NotFound };
        var token = new ApiToken
        {
            Id = Ambient.AllocateId(), Hash = ev.Hash, OwnerId = ev.OwnerId, Name = ev.Name.Trim(), Scope = ev.Scope, CreatedAt = ev.When,
            ExpiresAt = ev.ExpiresInDays is { } days ? ev.When.AddDays(days) : null
        };
        Ambient.Relation<IApiTokenTable>().Insert(token);
        return new() { Outcome = CommandOutcome.Ok, TokenId = token.Id };
    }
}

public sealed class ApiTokenRevokedHandler : IEventHandler<ApiTokenRevoked, ApiTokenCommandResult>
{
    public ApiTokenCommandResult? Validate(ApiTokenRevoked ev) => null;

    public ApiTokenCommandResult Consume(ApiTokenRevoked ev)
    {
        var tokens = Ambient.Relation<IApiTokenTable>();
        var token = tokens.FindByIdOrDefault(ev.TokenId);
        if (token == null) return new() { Outcome = CommandOutcome.NotFound };
        if (token.OwnerId != ev.UserId) return new() { Outcome = CommandOutcome.Forbidden };
        tokens.RemoveById(token.Id);
        return new() { Outcome = CommandOutcome.Ok, TokenId = token.Id };
    }
}
