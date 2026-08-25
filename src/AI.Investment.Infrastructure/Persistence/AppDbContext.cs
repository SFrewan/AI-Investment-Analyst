using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Auditing;
using AI.Investment.Domain.Companies;
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
/// Three entity types are exempt: <see cref="AuditRecord"/>, <see cref="ActionExecution"/> and
/// <see cref="ProcessedAction"/>. They are the seam's own bookkeeping - the record of what was
/// decided, what was attempted and which keys are claimed - and they must be writable precisely
/// when nothing is authorised, because that is the situation a denial creates. All three are
/// append-only, so exempting them grants no ability to change domain state.
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
        if (_writeAuthorization.IsAuthorized)
        {
            return;
        }

        ChangeTracker.DetectChanges();

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

        // A modification or deletion of seam bookkeeping is never legitimate, authorised or not.
        // These tables are append-only by design; permitting an update here would make the audit
        // trail rewritable, which is the one thing it must never be.
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

        if (!_internalWrite && ChangeTracker.Entries().Any(e => e.State == EntityState.Added))
        {
            // Reached when application code adds an exempt entity directly and calls the public
            // SaveChangesAsync. Audit and execution records must go through their stores so the
            // seam stays the single path.
            throw new UnauthorizedWriteException(
                "Audit, execution and idempotency records must be written through their stores, " +
                "not by calling SaveChangesAsync directly.");
        }
    }

    private static bool IsSeamBookkeeping(object entity) =>
        entity is AuditRecord or ActionExecution or ProcessedAction;
}
