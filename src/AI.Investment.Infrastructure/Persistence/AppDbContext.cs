using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Approvals;
using AI.Investment.Domain.Auditing;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Capital;
using AI.Investment.Domain.Companies;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Normalization;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Operations;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Retention;
using AI.Investment.Domain.Shadow;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.Watching;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AI.Investment.Infrastructure.Persistence;

/// <summary>
/// The database context, and the second independent enforcement point of the safety seam.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="SaveChangesAsync(CancellationToken)"/> refuses to commit unless an
/// authorised action execution is open.</strong> The domain enforces the same rule from the
/// other side - <c>ActionExecution.Start</c> rejects a decision that does not authorise its
/// proposal - and the two are deliberately independent. A developer who adds a repository call
/// and a save outside <c>IActionGateway</c> does not get a quiet write; they get an exception
/// naming the rule they broke.
/// </para>
/// <para>
/// Five entity types are exempt: <see cref="AuditRecord"/>, <see cref="ActionExecution"/>,
/// <see cref="ProcessedAction"/>, <see cref="IngestionRun"/> and
/// <see cref="QuarantinedPayload"/>. They are the platform's own bookkeeping -
/// the record of what was decided, what was attempted, which keys are claimed, which retrievals
/// were made or refused, and which payloads could not be read - and they must be writable precisely
/// when nothing is authorised, because that is the situation a denial creates. All five are
/// append-only, so exempting them grants no ability to change domain state.
/// </para>
/// <para>
/// <see cref="DataSource"/> and <see cref="IngestionRun"/> joined the model in the persistence
/// stage. Only the latter is exempt: the registry is ordinary domain state, so registering or
/// activating a source is a side effect that must pass through the seam like any other.
/// </para>
/// <para>
/// <strong>Phase 6 adds a second, narrower category.</strong> An operating cycle, an escalation, a
/// shadow decision and a queued message are the platform's account of its own unattended running,
/// and they have the same problem the audit trail has: the moment they most need to be writable is
/// the moment policy has refused something, when by definition nothing is authorised. They are
/// therefore creatable without a window - but unlike the five above they are not simply exempt.
/// Each has an explicit, per-type list of the fields that may change afterwards, every other field
/// is frozen, and none of them may be deleted. See <see cref="IsPermittedOperationsUpdate"/>: the
/// point of that method is that "the platform may record its own progress" never widens into "the
/// platform may edit what it recorded".
/// </para>
/// </remarks>
public sealed class AppDbContext : DbContext
{
    private readonly IWriteAuthorization _writeAuthorization;
    private bool _internalWrite;

    public AppDbContext(DbContextOptions<AppDbContext> options, IWriteAuthorization writeAuthorization)
        : base(options)
    {
        _writeAuthorization = writeAuthorization ?? throw new ArgumentNullException(nameof(writeAuthorization));
    }

    public DbSet<Company> Companies => Set<Company>();

    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    public DbSet<ActionExecution> ActionExecutions => Set<ActionExecution>();

    public DbSet<ProcessedAction> ProcessedActions => Set<ProcessedAction>();

    /// <summary>The source registry: where information may come from and on what terms.</summary>
    public DbSet<DataSource> DataSources => Set<DataSource>();

    /// <summary>The append-only ingestion ledger, including refusals.</summary>
    public DbSet<IngestionRun> IngestionRuns => Set<IngestionRun>();

    /// <summary>Payloads deleted under a source's licence, and why.</summary>
    public DbSet<UnreplayableEvidence> UnreplayableEvidence => Set<UnreplayableEvidence>();

    /// <summary>What the platform knows: one subject, one attribute, one value, one provenance.</summary>
    public DbSet<Observation> Observations => Set<Observation>();

    /// <summary>Payloads that were archived but could not be turned into observations.</summary>
    public DbSet<QuarantinedPayload> QuarantinedPayloads => Set<QuarantinedPayload>();

    /// <summary>Opportunities, from discovery through to a recorded outcome. Phase 5.</summary>
    public DbSet<Opportunity> Opportunities => Set<Opportunity>();

    /// <summary>Human approvals of exact actions. Single-use, and consumed conditionally. Phase 5.</summary>
    public DbSet<ApprovalToken> ApprovalTokens => Set<ApprovalToken>();

    /// <summary>The double-entry capital ledger. Append-only; balances are projections. Phase 5.</summary>
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    /// <summary>The durable half of the kill switch. Phase 5.</summary>
    public DbSet<KillSwitchFlag> KillSwitchFlags => Set<KillSwitchFlag>();

    /// <summary>What a human has permitted a capability to do unattended, and until when. Phase 6.</summary>
    public DbSet<AutonomyGrant> AutonomyGrants => Set<AutonomyGrant>();

    /// <summary>Standing deterministic instructions to start a cycle. Phase 6.</summary>
    public DbSet<Watch> Watches => Set<Watch>();

    /// <summary>The operating loop, persisted as a resumable state machine. Phase 6.</summary>
    public DbSet<OperatingCycle> OperatingCycles => Set<OperatingCycle>();

    /// <summary>Questions put to a human, with expiry. Phase 6.</summary>
    public DbSet<Escalation> Escalations => Set<Escalation>();

    /// <summary>What a higher autonomy level would have decided. Never acted on. Phase 6.</summary>
    public DbSet<ShadowDecision> ShadowDecisions => Set<ShadowDecision>();

    /// <summary>The transactional outbox. Phase 6.</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>
    /// Commits domain changes. Throws <see cref="UnauthorizedWriteException"/> unless the
    /// Action/Policy seam has opened an authorisation window.
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        GuardWrites();
        return await base.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="SaveChangesAsync(CancellationToken)"/>
    public override int SaveChanges()
    {
        GuardWrites();
        return base.SaveChanges();
    }

    /// <summary>
    /// Commits seam bookkeeping - audit records, execution records, idempotency claims - which
    /// must succeed even when no action is authorised.
    /// </summary>
    /// <remarks>
    /// Internal, so only this assembly's audit sink, execution store and idempotency store can
    /// reach it. The guard below still verifies that the pending changes really are limited to
    /// the exempt types, so this method cannot be used to smuggle a domain write past the seam.
    /// </remarks>
    internal async Task<int> SaveChangesInternalAsync(CancellationToken cancellationToken = default)
    {
        _internalWrite = true;

        try
        {
            GuardWrites();
            return await base.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _internalWrite = false;
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    private void GuardWrites()
    {
        ChangeTracker.DetectChanges();

        // FIRST, and deliberately ahead of the authorisation check below.
        //
        // A modification or deletion of seam bookkeeping is never legitimate, authorised or not.
        // These tables are append-only by design; permitting an update here would make the audit
        // trail rewritable, which is the one thing it must never be.
        //
        // This check previously sat AFTER the "already authorised, nothing to check" early return,
        // which meant it could never run on the path that matters. An authorisation window is open
        // for the whole duration of an action's effect, so the code most able to rewrite the record
        // of what it just did was the code exempted from being stopped. Authorisation permits an
        // effect; it does not permit editing the history of that effect.
        var mutatedBookkeeping = ChangeTracker
            .Entries()
            .Where(e => e.State is EntityState.Modified or EntityState.Deleted)
            .Where(e => IsSeamBookkeeping(e.Entity))
            .Select(e => $"{e.Entity.GetType().Name}:{e.State}")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (mutatedBookkeeping.Count > 0)
        {
            throw new UnauthorizedWriteException(
                "Audit, execution and idempotency records are append-only and may not be modified or " +
                $"deleted. Attempted: {string.Join(", ", mutatedBookkeeping)}.");
        }

        // SECOND, and for the same reason. An operations record may report its own progress and may
        // never be deleted or have its identity rewritten. This runs whether or not an action is
        // authorised, because an authorisation window permits an effect - it has never permitted
        // editing the account of what the platform did.
        var tamperedOperations = ChangeTracker
            .Entries()
            .Where(IsOperationsRecord)
            .Where(e => e.State is EntityState.Deleted ||
                (e.State == EntityState.Modified && !IsProgressUpdate(e)))
            .Select(Describe)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (tamperedOperations.Count > 0)
        {
            throw new UnauthorizedWriteException(
                "Operating cycles, escalations, shadow decisions and queued messages may record " +
                "their own progress but may not be deleted or have their identity rewritten. " +
                $"Attempted: {string.Join(", ", tamperedOperations)}.");
        }

        if (_writeAuthorization.IsAuthorized)
        {
            return;
        }

        var unauthorised = ChangeTracker
            .Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Where(e => !IsSeamBookkeeping(e.Entity))
            .Where(e => !(e.State == EntityState.Added && IsOperationsRecord(e)))
            .Where(e => !(e.State == EntityState.Modified && IsProgressUpdate(e)))
            .Select(e => $"{e.Entity.GetType().Name}:{e.State}")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (unauthorised.Count > 0)
        {
            throw new UnauthorizedWriteException(
                $"Pending changes without an authorised execution: {string.Join(", ", unauthorised)}.");
        }

        // The IsSeamBookkeeping filter is redundant today - the check above has already rejected
        // every other Added entity - but stating it makes this rule independent of that ordering
        // rather than silently correct because of it.
        if (!_internalWrite &&
            ChangeTracker.Entries().Any(e => e.State == EntityState.Added && IsSeamBookkeeping(e.Entity)))
        {
            // Reached when application code adds an exempt entity directly and calls the public
            // SaveChangesAsync. Audit and execution records must go through their stores so the
            // seam stays the single path.
            throw new UnauthorizedWriteException(
                "Audit, execution and idempotency records must be written through their stores, " +
                "not by calling SaveChangesAsync directly.");
        }
    }

    /// <summary>
    /// The append-only ledgers, exempt from the authorisation requirement and protected from
    /// modification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IngestionRun"/> belongs here for the same reason <see cref="AuditRecord"/> does:
    /// a refused run must be recordable precisely when nothing is authorised, because refusal is
    /// the situation in which no authorisation exists. Without the exemption, the platform
    /// declining to ingest something would be unable to write down that it had declined.
    /// </para>
    /// <para>
    /// <see cref="QuarantinedPayload"/> joins them for the same reason. A policy denial is one of
    /// the things worth quarantining a run over, so the record of "this could not be read" must be
    /// writable in exactly the state where nothing else is. Quarantining creates no belief and
    /// changes no domain state - it records a gap - so the exemption grants nothing beyond that.
    /// </para>
    /// <para>
    /// <see cref="Observation"/> is deliberately <em>not</em> exempt. An observation is something
    /// the platform believes, and beliefs are precisely what the seam exists to audit.
    /// </para>
    /// </remarks>
    private static bool IsSeamBookkeeping(object entity) =>
        entity is AuditRecord or ActionExecution or ProcessedAction or IngestionRun
            or QuarantinedPayload;

    /// <summary>
    /// The platform's account of its own unattended running: creatable without a window, never
    /// deletable, and modifiable only where <see cref="IsProgressUpdate"/> says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Creatable without an authorisation window, because the situation they exist to describe is
    /// most often the situation in which nothing is authorised: a cycle that was refused, an
    /// escalation raised because of that refusal, a shadow measurement of a decision that denied,
    /// and the queued message telling somebody about it.
    /// </para>
    /// <para>
    /// <see cref="AutonomyGrant"/> is deliberately absent, and so is <see cref="Watch"/>. A grant is
    /// the permission itself and a watch is a standing instruction to spend money; creating either
    /// is ordinary domain state and goes through the seam like anything else. Only a watch's record
    /// of having fired is progress, and that appears in <see cref="IsProgressUpdate"/> alone.
    /// </para>
    /// </remarks>
    private static bool IsOperationsRecord(EntityEntry entry) =>
        entry.Entity is OperatingCycle or Escalation or ShadowDecision or OutboxMessage ||
        IsOperationsType(RootOwnerType(entry));

    private static bool IsOperationsType(Type? type) =>
        type == typeof(OperatingCycle) ||
        type == typeof(Escalation) ||
        type == typeof(ShadowDecision) ||
        type == typeof(OutboxMessage);

    /// <summary>
    /// The type of the aggregate an owned entry ultimately belongs to, or null when it owns itself.
    /// </summary>
    /// <remarks>
    /// An owned value is tracked as its own entry, so a shadow decision's exposure arrives here as
    /// its own <c>Money</c> row rather than as part of the decision. Without this walk it would fall
    /// through to the unauthorised-write check, and a measurement of a denied action - the case that
    /// matters most - could not be recorded.
    /// </remarks>
    private static Type? RootOwnerType(EntityEntry entry)
    {
        var ownership = entry.Metadata.FindOwnership();
        IEntityType? owner = null;

        while (ownership is not null)
        {
            owner = ownership.PrincipalEntityType;
            ownership = owner.FindOwnership();
        }

        return owner?.ClrType;
    }

    /// <summary>
    /// Whether a modification records progress rather than rewriting what a row is about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The narrow half of the Phase 6 exemption, and the reason it is a list of column names rather
    /// than a type. A cycle may advance, spend and stop; a queued message may change its delivery
    /// state; a watch may record that it fired. None of them may change what it is <em>about</em> -
    /// the cycle's trigger key and budget, the message's type and payload, the watch's condition and
    /// cooldown - because that would let the account of a run be rewritten into the account of a
    /// different run, and would let a watch's own firing record loosen the cooldown that produced it.
    /// </para>
    /// <para>
    /// An owned value of one of these records is written when the record is created and never
    /// afterwards, so refusing every modification of one is the correct rule rather than a
    /// restrictive one. A cycle's budget and consumption are stored as single converted columns
    /// precisely so that this method stays a statement about named columns.
    /// </para>
    /// </remarks>
    private static bool IsProgressUpdate(EntityEntry entry)
    {
        var permitted = entry.Entity switch
        {
            OperatingCycle => CycleProgressFields,
            OutboxMessage => OutboxDeliveryFields,
            Watch => WatchFiringFields,
            _ => Array.Empty<string>(),
        };

        if (permitted.Length == 0)
        {
            return false;
        }

        foreach (var property in entry.Properties)
        {
            if (property.IsModified &&
                !permitted.Contains(property.Metadata.Name, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static readonly string[] CycleProgressFields =
    [
        nameof(OperatingCycle.Status),
        nameof(OperatingCycle.Stage),
        nameof(OperatingCycle.UpdatedAtUtc),
        nameof(OperatingCycle.StoppedAtUtc),
        nameof(OperatingCycle.StoppedReason),
        nameof(OperatingCycle.LeaseOwner),
        nameof(OperatingCycle.LeaseExpiresAtUtc),
        nameof(OperatingCycle.EscalationCount),
        nameof(OperatingCycle.Consumption),
    ];

    private static readonly string[] OutboxDeliveryFields =
    [
        nameof(OutboxMessage.Status),
        nameof(OutboxMessage.Attempts),
        nameof(OutboxMessage.NextAttemptAtUtc),
        nameof(OutboxMessage.DispatchedAtUtc),
        nameof(OutboxMessage.LastError),
        nameof(OutboxMessage.LeaseOwner),
        nameof(OutboxMessage.LeaseExpiresAtUtc),
    ];

    /// <summary>
    /// A watch's record of having fired. Not its condition, its cooldown or whether it is enabled -
    /// a firing that could relax the cooldown it was subject to would be no cooldown at all.
    /// </summary>
    private static readonly string[] WatchFiringFields =
    [
        nameof(Watch.LastFiredAtUtc),
        nameof(Watch.FireCount),
    ];

    private static string Describe(EntityEntry entry)
    {
        if (entry.State != EntityState.Modified)
        {
            return $"{entry.Entity.GetType().Name}:{entry.State}";
        }

        var changed = entry.Properties
            .Where(p => p.IsModified)
            .Select(p => p.Metadata.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        return $"{entry.Entity.GetType().Name}:Modified({string.Join("|", changed)})";
    }
}
