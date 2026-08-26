using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Auditing;
using AI.Investment.Domain.Companies;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Normalization;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Retention;
using AI.Investment.Domain.Sources;
using Microsoft.EntityFrameworkCore;

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

        if (_writeAuthorization.IsAuthorized)
        {
            return;
        }

        var unauthorised = ChangeTracker
            .Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Where(e => !IsSeamBookkeeping(e.Entity))
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
}
