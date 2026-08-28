using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>Maps the transactional outbox.</summary>
/// <remarks>
/// <para>
/// The unique index on the deduplication key is what makes enqueuing idempotent: the step that
/// queued a message can be retried after a crash without the recipient hearing the same thing twice.
/// </para>
/// <para>
/// The dispatch index covers the only query the dispatcher runs - pending messages whose next
/// attempt is due, oldest first - so a queue that has accumulated a large tail of abandoned or
/// delivered rows does not slow down the delivery of new ones.
/// </para>
/// <para>
/// The concurrency token is what makes two dispatchers safe. The lease stops them from choosing the
/// same message; the token stops the loser of a genuine race from overwriting the winner's record of
/// having delivered it.
/// </para>
/// </remarks>
public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("outbox_messages");

        builder.HasKey(m => m.OutboxMessageId);

        builder.Property(m => m.OutboxMessageId).HasColumnName("id").ValueGeneratedNever();

        builder.Property(m => m.MessageType)
            .HasColumnName("message_type")
            .HasMaxLength(OutboxMessage.MaxMessageTypeLength)
            .IsRequired();

        builder.Property(m => m.Payload)
            .HasColumnName("payload")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(m => m.DedupKey)
            .HasColumnName("dedup_key")
            .HasMaxLength(OutboxMessage.MaxDedupKeyLength)
            .IsRequired();

        builder.Property(m => m.CorrelationId)
            .HasColumnName("correlation_id")
            .HasMaxLength(OutboxMessage.MaxCorrelationLength)
            .IsRequired();

        builder.Property(m => m.CycleId).HasColumnName("cycle_id");
        builder.Property(m => m.OccurredAtUtc).HasColumnName("occurred_at_utc").IsRequired();

        builder.Property(m => m.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(m => m.Attempts).HasColumnName("attempts").IsRequired();
        builder.Property(m => m.NextAttemptAtUtc).HasColumnName("next_attempt_at_utc").IsRequired();
        builder.Property(m => m.DispatchedAtUtc).HasColumnName("dispatched_at_utc");

        builder.Property(m => m.LastError)
            .HasColumnName("last_error")
            .HasMaxLength(OutboxMessage.MaxErrorLength);

        builder.Property(m => m.LeaseOwner)
            .HasColumnName("lease_owner")
            .HasMaxLength(OutboxMessage.MaxWorkerLength);

        builder.Property(m => m.LeaseExpiresAtUtc).HasColumnName("lease_expires_at_utc");

        builder.Ignore(m => m.IsPending);

        builder.HasIndex(m => m.DedupKey)
            .IsUnique()
            .HasDatabaseName("ux_outbox_messages_dedup_key");

        builder.HasIndex(m => new { m.Status, m.NextAttemptAtUtc })
            .HasDatabaseName("ix_outbox_messages_dispatch");

        // The final arbiter between two workers in different processes. Postgres maintains xmin on
        // every row, so this costs no column and no write amplification.
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .IsRowVersion();
    }
}
