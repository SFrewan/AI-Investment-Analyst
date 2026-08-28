using AI.Investment.Domain.Common;
using AI.Investment.Domain.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>Maps operating cycles.</summary>
/// <remarks>
/// <para>
/// Three decisions here are load-bearing.
/// </para>
/// <para>
/// <strong>The trigger key is unique.</strong> That single constraint is what turns a trigger storm
/// into one cycle: the same observation delivered twice produces the same key, and the second insert
/// fails. Deduplicating in the database rather than by reading first is the same choice the
/// idempotency store makes, and for the same reason - a read-then-write races exactly when it
/// matters, which is when several workers are handling the same redelivery at once.
/// </para>
/// <para>
/// <strong>The row carries a concurrency token.</strong> The in-memory lease stops two healthy
/// workers from picking up the same cycle; it cannot see a worker in another process holding a stale
/// copy. The token is the arbiter that can: the loser of a race gets a concurrency exception rather
/// than silently overwriting the winner's progress.
/// </para>
/// <para>
/// <strong>Budget and consumption are converted columns rather than owned values.</strong> The write
/// guard's rule for cycles is a list of column names, and that rule has to stay a statement about
/// named columns rather than about the shape of an object graph.
/// </para>
/// </remarks>
public sealed class OperatingCycleConfiguration : IEntityTypeConfiguration<OperatingCycle>
{
    public void Configure(EntityTypeBuilder<OperatingCycle> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("operating_cycles");

        builder.HasKey(c => c.CycleId);

        builder.Property(c => c.CycleId).HasColumnName("id").ValueGeneratedNever();

        builder.Property(c => c.CorrelationId)
            .HasColumnName("correlation_id")
            .HasMaxLength(CorrelationId.MaxLength)
            .HasConversion(id => id.Value, value => CorrelationId.Create(value))
            .IsRequired();

        builder.Property(c => c.Capability)
            .HasColumnName("capability")
            .HasConversion<string>()
            .HasMaxLength(60)
            .IsRequired();

        builder.Property(c => c.TemplateName)
            .HasColumnName("template")
            .HasMaxLength(OperatingCycle.MaxTemplateLength)
            .IsRequired();

        builder.Property(c => c.TriggerKey)
            .HasColumnName("trigger_key")
            .HasMaxLength(OperatingCycle.MaxTriggerKeyLength)
            .IsRequired();

        builder.Property(c => c.WatchId).HasColumnName("watch_id");

        builder.Property(c => c.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(c => c.Stage)
            .HasColumnName("stage")
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        builder.Property(c => c.Budget)
            .HasColumnName("budget")
            .HasColumnType("jsonb")
            .HasConversion(
                budget => OperationsJson.Write(budget),
                value => OperationsJson.ReadBudget(value))
            .IsRequired();

        builder.Property(c => c.Consumption)
            .HasColumnName("consumption")
            .HasColumnType("jsonb")
            .HasConversion(
                consumption => OperationsJson.Write(consumption),
                value => OperationsJson.ReadConsumption(value))
            .IsRequired();

        builder.Property(c => c.StartedAtUtc).HasColumnName("started_at_utc").IsRequired();
        builder.Property(c => c.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();
        builder.Property(c => c.StoppedAtUtc).HasColumnName("stopped_at_utc");

        builder.Property(c => c.StoppedReason)
            .HasColumnName("stopped_reason")
            .HasMaxLength(OperatingCycle.MaxReasonLength);

        builder.Property(c => c.LeaseOwner)
            .HasColumnName("lease_owner")
            .HasMaxLength(OperatingCycle.MaxWorkerLength);

        builder.Property(c => c.LeaseExpiresAtUtc).HasColumnName("lease_expires_at_utc");
        builder.Property(c => c.EscalationCount).HasColumnName("escalation_count").IsRequired();

        builder.Ignore(c => c.IsRunning);
        builder.Ignore(c => c.IsFinished);

        builder.HasIndex(c => c.TriggerKey)
            .IsUnique()
            .HasDatabaseName("ux_operating_cycles_trigger_key");

        builder.HasIndex(c => new { c.Status, c.UpdatedAtUtc })
            .HasDatabaseName("ix_operating_cycles_runnable");

        builder.HasIndex(c => new { c.WatchId, c.StartedAtUtc })
            .HasDatabaseName("ix_operating_cycles_watch");

        // The final arbiter between two workers in different processes. Postgres maintains xmin on
        // every row, so this costs no column and no write amplification.
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .IsRowVersion();
    }
}
