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

    /// <summary>Entra's object and tenant ids when the provider gives them (<c>oid</c>, <c>tid</c>); empty otherwise. They find a
    /// person again when the server moves to another sign-in provider (docs/skillshare.md §2.5).</summary>
    public string ObjectId { get; set; } = "";
    public string TenantId { get; set; } = "";

    public UserDto ToDto() => new(Id, Name, Email, Role);
}

public interface IUserTable : IRelation<User>
{
    void Insert(User user);
    void Update(User user);
    User? FindByIdOrDefault(ulong id);
    User? FindByIdentityOrDefault(string issuer, string subject);
}

/// <summary>A person signed in through OIDC (or the development login). The first user ever becomes an admin, unless the provider
/// says who is one (<see cref="ProviderSaysRole"/>).</summary>
public class UserSignedIn : EventWithResult<SignedInUser>
{
    public string Issuer { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string ObjectId { get; set; } = "";
    public string TenantId { get; set; } = "";

    /// <summary>The role follows the provider at every sign-in: SkillShare's <c>skillshare_admin</c> claim.</summary>
    public bool ProviderSaysRole { get; set; }
    public bool ProviderSaysAdmin { get; set; }

    /// <summary>The issuer the server signed people in through before; a person it does not know yet is looked for among that
    /// issuer's users, by object id and then by email, and takes that user over. Empty for none.</summary>
    public string MigrateFromIssuer { get; set; } = "";

    /// <summary>The SkillShare grant made by this sign-in: its refresh token as Data Protection ciphertext (purpose
    /// <c>BoSoFa.SkillShareGrant.v1</c>, sub-purpose <see cref="GrantId"/>); empty without one.</summary>
    public string GrantId { get; set; } = "";
    public string ProtectedGrant { get; set; } = "";
    public string GrantScopes { get; set; } = "";
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
        var created = false;
        var user = users.FindByIdentityOrDefault(ev.Issuer, ev.Subject);
        if (user == null && ev.MigrateFromIssuer.Length > 0 && ev.MigrateFromIssuer != ev.Issuer && MigrationSource(users, ev) is { } previous)
        {
            Audit.Record(ev.When, previous.Id, "user.migrated", previous.Email, $"from {previous.Issuer} to {ev.Issuer}");
            previous.Issuer = ev.Issuer;
            previous.Subject = ev.Subject;
            user = previous;
        }
        if (user == null)
        {
            user = new User
            {
                Id = Ambient.AllocateId(), Issuer = ev.Issuer, Subject = ev.Subject,
                Role = users.Count == 0 ? UserRole.Admin : UserRole.Member, CreatedAt = ev.When
            };
            created = true;
        }
        user.Name = ev.Name;
        user.Email = ev.Email;
        user.LastSignInAt = ev.When;
        if (ev.ObjectId.Length > 0) user.ObjectId = ev.ObjectId;
        if (ev.TenantId.Length > 0) user.TenantId = ev.TenantId;
        if (ev.ProviderSaysRole)
        {
            var role = ev.ProviderSaysAdmin ? UserRole.Admin : UserRole.Member;
            if (!created && user.Role != role) Audit.Record(ev.When, user.Id, "user.role", user.Email, $"{role}, as in the sign-in provider");
            user.Role = role;
        }
        if (created) users.Insert(user);
        else users.Update(user);
        if (ev.ProtectedGrant.Length > 0)
            Ambient.Relation<ISkillShareGrantTable>().Upsert(new()
            {
                UserId = user.Id, GrantId = ev.GrantId, ProtectedRefreshToken = ev.ProtectedGrant, Scopes = ev.GrantScopes, GrantedAt = ev.When,
                Status = SkillShareGrantStatus.Active
            });
        return new() { UserId = user.Id, Created = created };
    }

    /// <summary>The user of the previous issuer this person was: the same object id, or else the one user with the same email.</summary>
    static User? MigrationSource(IUserTable users, UserSignedIn ev)
    {
        var previous = users.Where(user => user.Issuer == ev.MigrateFromIssuer).ToList();
        if (ev.ObjectId.Length > 0 && previous.FirstOrDefault(user => user.ObjectId == ev.ObjectId) is { } byObject) return byObject;
        if (ev.Email.Length == 0) return null;
        var byEmail = previous.Where(user => string.Equals(user.Email, ev.Email, StringComparison.OrdinalIgnoreCase)).ToList();
        // An old user with an object id is that person only if it is this one's.
        return byEmail is [{ ObjectId: var objectId } single] && (objectId.Length == 0 || ev.ObjectId.Length == 0) ? single : null;
    }
}

public static class Domain
{
    /// <summary>Raise this with every change to persisted relations: a node of a higher generation takes over from older
    /// nodes and publishes its schema, and older builds can never lead again (BTDB <c>Doc/ReplicationHosting.md</c>).</summary>
    public const ulong ApplicationGeneration = 21;

    /// <summary>Every relation of the server database; InitializeRelations registers them under leader authority.</summary>
    public static readonly Type[] Relations =
    [
        typeof(IUserTable), typeof(IEnrollmentTable), typeof(IMachineTable), typeof(ISessionTable),
        typeof(ISessionItemTable), typeof(IRuntimeRequestTable), typeof(IMachineCommandTable),
        typeof(IMachinePresenceTable), typeof(IHarnessAccountTable), typeof(IHarnessAccountUsageTable),
        typeof(IHarnessReleaseTable), typeof(IMachineHarnessTable), typeof(IMcpServerTable),
        typeof(ISkillBundleTable), typeof(IProfileTable), typeof(IApiTokenTable), typeof(IAuditEntryTable),
        typeof(IDesktopSessionTable),
        typeof(IAgentRunnerTable), typeof(ISessionDraftTable), typeof(ISecretTable), typeof(ISecretVersionTable),
        typeof(ISecretUseTable), typeof(ISecretEnvironmentTable), typeof(IEnvironmentApprovalTable), typeof(IShellCommandRunTable), typeof(IDraftItemTable),
        typeof(ISkillShareGrantTable), typeof(ISkillShareSkillTable), typeof(ISkillShareSyncTable), typeof(ISkillShareMcpChoiceTable), .. BTDB.BackendActions.BackendActions.RelationTypes
    ];

    /// <summary>The domain's handlers and those of <paramref name="actions"/>, whose action types must all be registered.</summary>
    public static EventDispatcher CreateDispatcher(IDomainSignals signals, BTDB.BackendActions.BackendActions actions)
    {
        var dispatcher = Handlers(signals, actions);
        actions.RegisterEvents(dispatcher);
        return dispatcher;
    }

    static readonly RetiredReleaseHandlers Retired = new();

    static EventDispatcher Handlers(IDomainSignals signals, BTDB.BackendActions.BackendActions actions) => new EventDispatcher()
        .Register(new UserSignedInHandler())
        .Register(new EnrollmentStartedHandler())
        .Register(new EnrollmentApprovedHandler())
        .Register(new MachineRenamedHandler())
        .Register(new MachineRemovedHandler(signals, actions))
        .Register(new SessionDraftSavedHandler(actions))
        .Register(new SessionDraftDiscardedHandler(actions))
        .Register(new SessionCreatedHandler(signals, actions))
        .Register(new SessionMessageSentHandler(signals, actions))
        .Register(new SessionInterruptRequestedHandler(signals, actions))
        .Register(new SessionCompactRequestedHandler(signals, actions))
        .Register(new SessionClearRequestedHandler(signals))
        .Register(new SessionSideQuestionAskedHandler(signals, actions))
        .Register(new SessionShellCommandRunHandler(signals, actions))
        .Register(new SessionShellCommandStopRequestedHandler(actions))
        .Register(new DraftShellCommandRunHandler(signals, actions))
        .Register(new DraftShellCommandStopRequestedHandler(actions))
        .Register(new SessionStopRequestedHandler(signals, actions))
        .Register(new RuntimeRequestAnsweredHandler(signals, actions))
        .Register(new MachineCommandsAckedHandler())
        .Register(new DaemonReportsReceivedHandler(signals))
        .Register(new MachineConnectedHandler(signals))
        .Register(new MachineDisconnectedHandler(signals))
        .Register(new NodePresenceExpiredHandler(signals))
        .Register(new HarnessAccountAddedHandler(signals))
        .Register(new HarnessAccountRemovedHandler())
        .Register(new SecretSetHandler(signals))
        .Register(new SecretDeletedHandler())
        .Register(new SecretSettingsChangedHandler())
        .Register(new SecretsUsedHandler())
        .Register(new EnvironmentSavedHandler())
        .Register(new EnvironmentDeletedHandler())
        .Register(new EnvironmentApprovalAnsweredHandler(signals, actions))
        .Register(new HarnessReleasePublishedHandler())
        .Register(new HarnessInstallRequestedHandler(signals))
        .Register(new MachineInventoryReportedHandler())
        .Register(new McpServerPublishedHandler())
        .Register(new McpValuesMovedHandler(signals))
        .Register(new SkillBundlePublishedHandler())
        .Register(new ProfileSavedHandler())
        .Register(new SkillShareGrantExpiredHandler())
        .Register(new SkillShareCatalogSyncedHandler())
        .Register(new SkillShareMcpChoiceSavedHandler())
        .Register(new ApiTokenCreatedHandler())
        .Register(new ApiTokenRevokedHandler())
        .Register(new ActionAuditedHandler())
        .Register<DaemonReleasePublished, SessionCommandResult>(Retired)
        .Register<DesktopReleasePublished, SessionCommandResult>(Retired)
        .Register<MachineUpdatePolicySet, SessionCommandResult>(Retired)
        .Register<DaemonRolloutStarted, DaemonRolloutResult>(Retired)
        .Register<DaemonRolloutHaltRequested, DaemonRolloutResult>(Retired)
        .Register<DaemonRolloutTicked>(Retired)
        .Register(new DesktopSessionStartedHandler())
        .Register(new DesktopSessionRefreshedHandler())
        .Register(new DesktopSessionRevokedHandler())
        .Register(new AgentItemsRecordedHandler(signals))
        .Register(new AgentApprovalRaisedHandler(signals))
        .Register(new AgentRunFinishedHandler(signals))
        .Register(new AgentRunAttachedHandler(signals, actions))
        .Register(new AgentRunsResumedHandler(signals, actions))
        .Register(new AgentRunClaimedHandler(signals));
}
