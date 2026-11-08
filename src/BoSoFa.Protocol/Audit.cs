namespace BoSoFa.Protocol;

/// <summary>One line of the audit log. <c>ActorName</c> is empty when no person did it.</summary>
public sealed record AuditEntryDto(ulong Id, DateTime At, ulong ActorId, string ActorName, string Action, string Target, string Detail);
