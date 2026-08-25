using AI.Investment.Domain.Actions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>Maps claimed idempotency keys.</summary>
/// <remarks>
/// The key is the primary key, so the uniqueness that makes deduplication correct is enforced by
/// the database rather than by application code that would race under concurrent retries.
/// </remarks>
public sealed class ProcessedActionConfiguration : IEntityTypeConfiguration<ProcessedAction>
{
    public void Configure(EntityTypeBuilder<ProcessedAction> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("processed_actions");

        builder.HasKey(p => p.IdempotencyKey);

        builder.Property(p => p.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(ActionProposal.MaxIdempotencyKeyLength)
            .ValueGeneratedNever();

        builder.Property(p => p.ProposalId).HasColumnName("proposal_id").IsRequired();
        builder.Property(p => p.ClaimedAtUtc).HasColumnName("claimed_at_utc").IsRequired();

        builder.HasIndex(p => p.ClaimedAtUtc).HasDatabaseName("ix_processed_actions_claimed_at_utc");
    }
}
