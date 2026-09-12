using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Approvals;
using AI.Investment.Domain.Auditing;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Capital;
using AI.Investment.Domain.Companies;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Normalization;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Operations;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Portfolio;
using AI.Investment.Domain.Retention;
using AI.Investment.Domain.Securities;
using AI.Investment.Domain.Shadow;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.Universe;
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

    /// <summary>
    /// What was asked of a provider and what came back, one row per exchange. Append-only.
    /// </summary>
    /// <remarks>
    /// Separate from the archive because the archive is keyed by content: the two-byte <c>[]</c>
    /// payload answers 324 different requests, and a content-keyed sidecar can describe only the
    /// first writer.
    /// </remarks>
    public DbSet<ProviderExchange> ProviderExchanges => Set<ProviderExchange>();

    /// <summary>Tradable instruments, identified by a surrogate and never by a symbol. Stage D.</summary>
    public DbSet<Security> Securities => Set<Security>();

    /// <summary>Trading venues as reference data, each scoped to the interval it is true for. Stage D.</summary>
    public DbSet<Venue> Venues => Set<Venue>();

    /// <summary>
    /// The (security, venue) pairings. Carries no status: what a listing did is in its events.
    /// Stage D.
    /// </summary>
    public DbSet<Listing> Listings => Set<Listing>();

    /// <summary>
    /// Listing transitions, append-only and always venue-qualified. The temporal core of the
    /// reference model. Stage D.
    /// </summary>
    public DbSet<ListingEvent> ListingEvents => Set<ListingEvent>();

    /// <summary>
    /// Sealed universe versions. One row per seal, identified by its content fingerprint. Stage D.
    /// </summary>
    public DbSet<Universe> Universes => Set<Universe>();

    /// <summary>
    /// Which securities were in which sealed universe version, and at which cohort cuts. The span
    /// is derived on read and is deliberately not stored. Stage D.
    /// </summary>
    public DbSet<UniverseMembership> UniverseMemberships => Set<UniverseMembership>();

    /// <summary>
    /// Derived values, recorded so they can be reproduced rather than trusted. The sibling to
    /// <see cref="Observations"/>, and the only place a non-Fact claim may live. Stage E.
    /// </summary>
    public DbSet<Determination> Determinations => Set<Determination>();

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

    /// <summary>
    /// The warrants permitting unattended execution. Phase 8.
    /// </summary>
    /// <remarks>
    /// Expected to be empty for as long as the measured evidence does not justify one, which is the
    /// current state. An empty table here is the platform working, not the platform unfinished.
    /// </remarks>
    public DbSet<PromotionWarrant> PromotionWarrants => Set<PromotionWarrant>();

    /// <summary>
    /// Written decisions by two named people that a venue may move real money. Phase 8.
    /// </summary>
    /// <remarks>
    /// The most consequential table in this model, and one that has never held a row. It exists so
    /// that the day somebody wants to activate a venue, the record they must create already has a
    /// shape, two signature columns and an expiry - rather than being designed under the pressure of
    /// wanting the answer to be yes.
    /// </remarks>
    public DbSet<LiveVenueAuthorization> LiveVenueAuthorizations => Set<LiveVenueAuthorization>();

    /// <summary>The transactional outbox. Phase 6.</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>
    /// The record of how each fill moved a holding. Append-only, one row per venue reference.
    /// </summary>
    /// <remarks>
    /// There is no positions table. A holding is replayed from these rows, exactly as a balance is
    /// projected from ledger entries - a stored quantity can be wrong while every event behind it is
    /// right, and nothing in the data would say so.
    /// </remarks>
    public DbSet<PositionEvent> PositionEvents => Set<PositionEvent>();

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
            .Where(IsSeamBookkeeping)
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

        // THIRD. A position event is the record of how a fill moved a holding, and a holding is
        // replayed from nothing else. Editing one would edit a quantity, a cost and a realised
        // profit at once, silently and with no counter-entry anywhere - the capital ledger beside
        // it cannot be edited, so this must not be either. Unlike the seam's own bookkeeping it is
        // NOT exempt from needing an authorisation to be created: a fill moves money, and a write
        // that no decision authorised is exactly what the guard below exists to refuse. Block 3.
        var rewrittenPositions = ChangeTracker
            .Entries()
            .Where(e => e.State is EntityState.Modified or EntityState.Deleted)
            .Where(IsPositionRecord)
            .Select(Describe)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (rewrittenPositions.Count > 0)
        {
            throw new UnauthorizedWriteException(
                "Position events are append-only: a holding is replayed from them, so modifying or " +
                "deleting one rewrites a quantity, a cost and a realised profit at once. " +
                $"Attempted: {string.Join(", ", rewrittenPositions)}.");
        }

        // FOURTH, and for the fourth time for the same reason. A promotion warrant and a live-venue
        // authorisation are the records of what somebody permitted and why; they are withdrawn by
        // setting a revocation on the row, never by removing it. A deletion here would erase the
        // account of a privilege that was once in force, which is the only account anybody would
        // have afterwards. Phase 8.
        var erasedPrivileges = ChangeTracker
            .Entries()
            .Where(e => e.State == EntityState.Deleted)
            .Where(e => IsPrivilegeRecord(e))
            .Select(Describe)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (erasedPrivileges.Count > 0)
        {
            throw new UnauthorizedWriteException(
                "Promotion warrants and live-venue authorisations are withdrawn by revoking them, " +
                "never by deleting them. The record of a privilege that was once in force is the " +
                $"only account anybody has of it. Attempted: {string.Join(", ", erasedPrivileges)}.");
        }

        // FIFTH, and for the same reason as the position events above. A listing event is the record
        // of what one venue said about one security on one date, and the point-in-time state of a
        // listing is replayed from nothing else. Editing one would rewrite what was knowable on a
        // past date, which is the single failure the reference model was built to remove -
        // Company.ChangeListing overwrites the current ticker and exchange, and that is precisely
        // why no historical question can be answered from it.
        //
        // Like a position event and unlike the seam's own bookkeeping, it is NOT exempt from needing
        // an authorisation to be created: reference data is a domain write, and the guard below is
        // the right place for it to be refused. Stage D.
        var rewrittenReferenceData = ChangeTracker
            .Entries()
            .Where(e => e.State is EntityState.Modified or EntityState.Deleted)
            .Where(IsReferenceDataRecord)
            .Select(Describe)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (rewrittenReferenceData.Count > 0)
        {
            throw new UnauthorizedWriteException(
                "Listing events, sealed universes and their memberships are append-only: a past " +
                "date's listing status and a past version's population are replayed from them, so " +
                "modifying or deleting one rewrites what was knowable then. " +
                $"Attempted: {string.Join(", ", rewrittenReferenceData)}.");
        }

        // SIXTH. A determination is the record of what a named, versioned rule concluded from a named
        // set of inputs at a stated instant. Editing one would change the platform's own past
        // conclusion while leaving the rule, the version and the input hashes saying it had concluded
        // something else - which is an unreproducible claim wearing the clothes of evidence. A
        // correction is a new row under a new rule version, never an edit.
        //
        // Like a position event and a listing event, and unlike the seam's own bookkeeping, it is NOT
        // exempt from needing an authorisation to be created. Stage E.
        var rewrittenDeterminations = ChangeTracker
            .Entries()
            .Where(e => e.State is EntityState.Modified or EntityState.Deleted)
            .Where(IsDeterminationRecord)
            .Select(Describe)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (rewrittenDeterminations.Count > 0)
        {
            throw new UnauthorizedWriteException(
                "Determinations are append-only: a determination is reproducible from its rule, its " +
                "version, its as-of instant and its input hashes, so editing one makes the record " +
                "disagree with what it says it computed. " +
                $"Attempted: {string.Join(", ", rewrittenDeterminations)}.");
        }

        if (_writeAuthorization.IsAuthorized)
        {
            return;
        }

        var unauthorised = ChangeTracker
            .Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Where(e => !IsSeamBookkeeping(e))
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
            ChangeTracker.Entries().Any(e => e.State == EntityState.Added && IsSeamBookkeeping(e)))
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
    /// <see cref="ProviderExchange"/> joins them, and the argument is the same one that already
    /// admits <see cref="IngestionRun"/>. It is the record of what was asked of a vendor and what
    /// came back: an append-only fact about an exchange that has already happened, carrying no
    /// belief, no financial effect and no domain state. It is written by the same gateway, about
    /// the same run, at the same moment - <c>IngestionGateway.RecordExchangesAsync</c> runs
    /// immediately after the run is recorded, deliberately outside the effect so that a provenance
    /// row can never point at a run nobody recorded. That timing is correct and is not what
    /// changed; what was wrong is that a record which must be writable exactly there was not
    /// classified as one of the records that may be.
    /// <em>This was found by a live pilot, not by a test.</em> The run was recorded, the payload
    /// was archived, and the provenance insert was refused with "Pending changes without an
    /// authorised execution: ProviderExchange:Added" - so the platform kept the bytes and lost the
    /// account of how it asked for them, which is the exact failure the record exists to prevent.
    /// </para>
    /// <para>
    /// Listing it here narrows as much as it permits. The append-only rule above now refuses any
    /// modification or deletion of a provenance row, inside an authorisation window or out, and the
    /// single-path rule below still requires it to be written through
    /// <c>EfProviderExchangeStore</c> rather than by a bare <c>SaveChangesAsync</c>. The exemption
    /// is from needing a window to CREATE one; it is not permission to do anything else to one.
    /// </para>
    /// <para>
    /// <see cref="Observation"/> is deliberately <em>not</em> exempt. An observation is something
    /// the platform believes, and beliefs are precisely what the seam exists to audit.
    /// </para>
    /// </remarks>
    private static bool IsSeamBookkeeping(EntityEntry entry) =>
        entry.Entity is AuditRecord or ActionExecution or ProcessedAction or IngestionRun
            or QuarantinedPayload or ProviderExchange ||
        IsSeamBookkeepingType(RootOwnerType(entry));

    /// <summary>
    /// The aggregates above, by type, for an owned entry that walked up to one of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An owned value is tracked as its own entry, so an <see cref="IngestionRun"/>'s
    /// <c>IngestionRequest</c> - and that request's own <c>IngestionSubject</c> - arrive here as
    /// separate rows rather than as part of the run. Matching on an entry's own CLR type alone saw
    /// <c>IngestionRun:Added</c> as exempt and its two owned rows as ordinary domain writes, so
    /// recording a run outside an authorisation window failed with
    /// <c>IngestionRequest:Added, IngestionSubject:Added</c> - the one situation the exemption
    /// exists for.
    /// </para>
    /// <para>
    /// This grants nothing new. An owned row cannot exist without the aggregate that owns it, and
    /// that aggregate is already exempt; the walk only stops the guard from splitting one record
    /// into an exempt part and a refused part. <see cref="IsOperationsRecord"/>,
    /// <see cref="IsPrivilegeRecord"/> and <see cref="IsPositionRecord"/> each already walk
    /// ownership for the same reason - this category was the one that never did.
    /// </para>
    /// <para>
    /// It also makes the append-only rule above cover owned rows, which it previously did not: an
    /// attempt to rewrite an audit record's owned value is now refused alongside the record itself.
    /// </para>
    /// </remarks>
    private static bool IsSeamBookkeepingType(Type? type) =>
        type == typeof(AuditRecord) ||
        type == typeof(ActionExecution) ||
        type == typeof(ProcessedAction) ||
        type == typeof(IngestionRun) ||
        type == typeof(QuarantinedPayload) ||
        type == typeof(ProviderExchange);

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
    /// the privilege itself and a watch is a standing instruction to spend money; creating either
    /// is ordinary domain state and goes through the seam like anything else. Only a watch's record
    /// of having fired is progress, and that appears in <see cref="IsProgressUpdate"/> alone.
    /// </para>
    /// </remarks>
    private static bool IsOperationsRecord(EntityEntry entry) =>
        entry.Entity is OperatingCycle or Escalation or ShadowDecision or OutboxMessage ||
        IsOperationsType(RootOwnerType(entry));

    /// <summary>
    /// The records of what somebody permitted: revocable, never deletable.
    /// </summary>
    /// <remarks>
    /// Deliberately narrower than the operations category. A warrant may be revoked and an
    /// authorisation withdrawn - both of which are ordinary modifications of an existing row - so
    /// only deletion is refused here. What must survive is the fact that the privilege existed.
    /// </remarks>
    private static bool IsPrivilegeRecord(EntityEntry entry) =>
        entry.Entity is PromotionWarrant or LiveVenueAuthorization ||
        RootOwnerType(entry) == typeof(PromotionWarrant) ||
        RootOwnerType(entry) == typeof(LiveVenueAuthorization);

    /// <summary>A position event, or one of its owned money values.</summary>
    private static bool IsPositionRecord(EntityEntry entry) =>
        entry.Entity is PositionEvent || RootOwnerType(entry) == typeof(PositionEvent);

    /// <summary>
    /// The append-only records of the reference model: what a venue said, and when.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Security"/>, <see cref="Venue"/> and <see cref="Listing"/> are deliberately not
    /// here. A security's issuer and a listing's pairing are fixed at creation and the domain
    /// exposes no operation that changes them, so there is nothing for a database-level refusal to
    /// add; a venue is slowly-changing reference data whose corrections are legitimate. The parts
    /// that must never be rewritten are the ones named.
    /// </para>
    /// <para>
    /// <strong><see cref="SecurityIdentifier"/> joined them at G2</strong>, for the same reason a
    /// listing event is here and its listing is not: an identifier assertion records what a source
    /// said and over which interval, and rewriting one would change what the platform believes was
    /// claimed. <c>Security.AssertIdentifier</c> is already append-only by construction - there is
    /// no operation that edits or removes an assertion - and this is the second mechanism, because
    /// the first can be bypassed by a caller holding a tracked entity.
    /// </para>
    /// <para>
    /// A <see cref="Universe"/> and its memberships are here for the same reason a listing event is:
    /// a sealed version is what a backtest cites, and its identity is a digest of its own content, so
    /// an edit would leave a row whose fingerprint no longer describes it. Re-sealing is a new row,
    /// never an update.
    /// </para>
    /// </remarks>
    private static bool IsReferenceDataRecord(EntityEntry entry) =>
        entry.Entity is ListingEvent or Universe or UniverseMembership or SecurityIdentifier ||
        IsReferenceDataType(RootOwnerType(entry));

    /// <summary>
    /// The append-only record of what a rule concluded. Stage E.
    /// </summary>
    /// <remarks>
    /// Its own category rather than reference data, because it is neither: a determination is not a
    /// fact about the world and not a slowly-changing reference row. It is the platform's own
    /// conclusion, and the reason it must never be edited differs in kind from the reason a listing
    /// event must not be - a listing event records what somebody said, and this records what the
    /// platform worked out.
    /// </remarks>
    private static bool IsDeterminationRecord(EntityEntry entry) =>
        entry.Entity is Determination || RootOwnerType(entry) == typeof(Determination);

    private static bool IsReferenceDataType(Type? rootOwner) =>
        rootOwner == typeof(ListingEvent)
        || rootOwner == typeof(Universe)
        || rootOwner == typeof(UniverseMembership);

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
            Escalation => EscalationAnswerFields,
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
    /// The four columns answering an escalation writes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An escalation exists to be answered, and until this list existed it could not be: every
    /// modification of an operations record is refused unless its changed columns are on one of
    /// these allow-lists, and an escalation had none. <c>Acknowledge</c> and <c>Resolve</c> were
    /// implemented in the domain, tested there, and would have thrown at the database. The operator
    /// surface is the first thing to call them, and this is what lets the call commit.
    /// </para>
    /// <para>
    /// Deliberately these four and nothing else. The question that was raised, the capability it was
    /// raised under, when it was raised and when it expires all stay immutable - an escalation whose
    /// expiry could be pushed out is an escalation that is never unhandled, and the count of
    /// unhandled escalations is one of the measurements unattended operation is judged on.
    /// </para>
    /// </remarks>
    private static readonly string[] EscalationAnswerFields =
    [
        nameof(Escalation.AcknowledgedAtUtc),
        nameof(Escalation.AcknowledgedBy),
        nameof(Escalation.ResolvedAtUtc),
        nameof(Escalation.Resolution),
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
