using BoSoFa.Protocol;
using BTDB.ODBLayer;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Domain;

public class User
{
    [PrimaryKey(1)] public ulong Id { get; set; }

    /// <summary>OIDC issuer and subject identify a person across sign-ins; email and name may change.</summary>
    [SecondaryKey("Identity", Order = 1)] public string Issuer { get; set; } = "";

    [SecondaryKey("Identity", Order = 2)] public string Subject { get; set; } = "";

    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public UserRole Role { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastSignInAt { get; set; }

    public UserDto ToDto() => new(Id, Name, Email, Role);
}

public interface IUserTable : IRelation<User>
{
    void Insert(User user);
    void Update(User user);
    User? FindByIdOrDefault(ulong id);
    User? FindByIdentityOrDefault(string issuer, string subject);
}

/// <summary>A person signed in through OIDC (or the development login). The first user ever becomes an admin.</summary>
public class UserSignedIn : EventWithResult<SignedInUser>
{
    public string Issuer { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
}

[BTDB.Generate]
public class SignedInUser
{
    public ulong UserId { get; set; }
    public bool Created { get; set; }
}

public sealed class UserSignedInHandler : IEventHandler<UserSignedIn, SignedInUser>
{
    public SignedInUser? Validate(UserSignedIn ev)
    {
        if (string.IsNullOrWhiteSpace(ev.Issuer) || string.IsNullOrWhiteSpace(ev.Subject))
            throw new ArgumentException("Sign-in needs an issuer and a subject.");
        return null;
    }

    public SignedInUser Consume(UserSignedIn ev)
    {
        var users = Ambient.Relation<IUserTable>();
        if (users.FindByIdentityOrDefault(ev.Issuer, ev.Subject) is { } existing)
        {
            existing.Name = ev.Name;
            existing.Email = ev.Email;
            existing.LastSignInAt = ev.When;
            users.Update(existing);
            return new() { UserId = existing.Id };
        }
        var user = new User
        {
            Id = Ambient.AllocateId(), Issuer = ev.Issuer, Subject = ev.Subject, Name = ev.Name, Email = ev.Email,
            Role = users.Count == 0 ? UserRole.Admin : UserRole.Member, CreatedAt = ev.When, LastSignInAt = ev.When
        };
        users.Insert(user);
        return new() { UserId = user.Id, Created = true };
    }
}

public static class Domain
{
    /// <summary>Raise this with every change to persisted relations: a node of a higher generation takes over from older
    /// nodes and publishes its schema, and older builds can never lead again (BTDB <c>Doc/ReplicationHosting.md</c>).</summary>
    public const ulong ApplicationGeneration = 6;

    /// <summary>Every relation of the server database; InitializeRelations registers them under leader authority.</summary>
    public static readonly Type[] Relations =
    [
        typeof(IUserTable), typeof(IEnrollmentTable), typeof(IMachineTable), typeof(ISessionTable),
        typeof(ISessionItemTable), typeof(IRuntimeRequestTable), typeof(IMachineCommandTable),
        typeof(IMachinePresenceTable), typeof(IHarnessAccountTable), typeof(IHarnessAccountUsageTable),
        typeof(IHarnessReleaseTable), typeof(IMachineHarnessTable), typeof(IMcpServerTable),
        typeof(ISkillBundleTable), typeof(IProfileTable), typeof(IApiTokenTable), typeof(IAuditEntryTable),
        typeof(IDaemonReleaseTable), typeof(IDesktopReleaseTable), typeof(IMachineUpdatePolicyTable), typeof(IDaemonRolloutTable), typeof(IDaemonRolloutMachineTable), typeof(IDesktopSessionTable),
        typeof(IAgentRunnerTable)
    ];

    public static EventDispatcher CreateDispatcher(IDomainSignals signals) => new EventDispatcher()
        .Register(new UserSignedInHandler())
        .Register(new EnrollmentStartedHandler())
        .Register(new EnrollmentApprovedHandler())
        .Register(new MachineRenamedHandler())
        .Register(new SessionCreatedHandler(signals))
        .Register(new SessionMessageSentHandler(signals))
        .Register(new SessionInterruptRequestedHandler(signals))
        .Register(new SessionStopRequestedHandler(signals))
        .Register(new RuntimeRequestAnsweredHandler(signals))
        .Register(new MachineCommandsAckedHandler())
        .Register(new DaemonReportsReceivedHandler(signals))
        .Register(new MachineConnectedHandler(signals))
        .Register(new MachineDisconnectedHandler(signals))
        .Register(new NodePresenceExpiredHandler(signals))
        .Register(new HarnessAccountAddedHandler())
        .Register(new HarnessAccountRemovedHandler())
        .Register(new HarnessReleasePublishedHandler())
        .Register(new HarnessInstallRequestedHandler(signals))
        .Register(new MachineInventoryReportedHandler())
        .Register(new McpServerPublishedHandler())
        .Register(new SkillBundlePublishedHandler())
        .Register(new ProfileSavedHandler())
        .Register(new ApiTokenCreatedHandler())
        .Register(new ApiTokenRevokedHandler())
        .Register(new ActionAuditedHandler())
        .Register(new DaemonReleasePublishedHandler())
        .Register(new DesktopReleasePublishedHandler())
        .Register(new MachineUpdatePolicySetHandler())
        .Register(new DaemonRolloutStartedHandler())
        .Register(new DaemonRolloutHaltRequestedHandler())
        .Register(new DaemonRolloutTickedHandler(signals))
        .Register(new DesktopSessionStartedHandler())
        .Register(new DesktopSessionRefreshedHandler())
        .Register(new DesktopSessionRevokedHandler())
        .Register(new AgentItemsRecordedHandler(signals))
        .Register(new AgentApprovalRaisedHandler(signals))
        .Register(new AgentRunFinishedHandler(signals))
        .Register(new AgentRunClaimedHandler(signals));
}
