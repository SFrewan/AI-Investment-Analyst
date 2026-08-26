using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Normalization;
using AI.Investment.Domain.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>Maps payloads that were archived but could not be read.</summary>
/// <remarks>
/// <para>
/// Keyed by content hash, which makes the record idempotent for free: the same bytes fail the same
/// way, so a second attempt collides with the first rather than adding a row saying the same thing
/// with a later timestamp.
/// </para>
/// <para>
/// The reason column is bounded and holds no excerpt of the payload - see
/// <see cref="QuarantinedPayload.Reason"/> for why. The bytes stay in the archive, which is the one
/// place designed to hold them.
/// </para>
/// </remarks>
public sealed class QuarantinedPayloadConfiguration : IEntityTypeConfiguration<QuarantinedPayload>
{
    public void Configure(EntityTypeBuilder<QuarantinedPayload> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("quarantined_payloads");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .HasColumnName("content_hash")
            .HasMaxLength(ContentHash.HexLength)
            .HasConversion(hash => hash.Value, value => ContentHash.Create(value))
            .ValueGeneratedNever();

        builder.Property(p => p.SourceId)
            .HasColumnName("source_id")
            .HasMaxLength(SourceId.MaxLength)
            .HasConversion(id => id.Value, value => SourceId.Create(value))
            .IsRequired();

        builder.Property(p => p.Category)
            .HasColumnName("category")
            .HasConversion<string>()
            .HasMaxLength(60)
            .IsRequired();

        builder.Property(p => p.RuleId)
            .HasColumnName("rule_id")
            .HasMaxLength(QuarantinedPayload.MaxRuleIdLength)
            .IsRequired();

        builder.Property(p => p.Reason)
            .HasColumnName("reason")
            .HasMaxLength(QuarantinedPayload.MaxReasonLength)
            .IsRequired();

        builder.Property(p => p.QuarantinedAtUtc).HasColumnName("quarantined_at_utc").IsRequired();

        // The operator queue, newest first; then "what is failing from this source?" and "how many
        // payloads did this rule reject?" - the two questions a schema change actually raises.
        builder.HasIndex(p => p.QuarantinedAtUtc)
            .HasDatabaseName("ix_quarantined_payloads_quarantined_at_utc");

        builder.HasIndex(p => p.SourceId).HasDatabaseName("ix_quarantined_payloads_source_id");

        builder.HasIndex(p => p.RuleId).HasDatabaseName("ix_quarantined_payloads_rule_id");
    }
}
