using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Retention;
using AI.Investment.Domain.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>Maps the record of payloads deleted under licence.</summary>
/// <remarks>
/// Keyed by content hash, which is also the natural join to anything referencing the payload. Once
/// claims are persisted, "is this claim replayable?" becomes a lookup here rather than a flag that
/// would have to be written onto every claim touching the same bytes.
/// </remarks>
public sealed class UnreplayableEvidenceConfiguration : IEntityTypeConfiguration<UnreplayableEvidence>
{
    public void Configure(EntityTypeBuilder<UnreplayableEvidence> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("unreplayable_evidence");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasColumnName("content_hash")
            .HasMaxLength(ContentHash.HexLength)
            .HasConversion(hash => hash.Value, value => ContentHash.Create(value))
            .ValueGeneratedNever();

        builder.Property(e => e.SourceId)
            .HasColumnName("source_id")
            .HasMaxLength(SourceId.MaxLength)
            .HasConversion(id => id.Value, value => SourceId.Create(value))
            .IsRequired();

        builder.Property(e => e.RuleId)
            .HasColumnName("rule_id")
            .HasMaxLength(UnreplayableEvidence.MaxRuleIdLength)
            .IsRequired();

        builder.Property(e => e.Reason)
            .HasColumnName("reason")
            .HasMaxLength(UnreplayableEvidence.MaxReasonLength)
            .IsRequired();

        builder.Property(e => e.MarkedAtUtc).HasColumnName("marked_at_utc").IsRequired();

        builder.HasIndex(e => e.SourceId).HasDatabaseName("ix_unreplayable_evidence_source_id");
        builder.HasIndex(e => e.MarkedAtUtc).HasDatabaseName("ix_unreplayable_evidence_marked_at_utc");
    }
}
