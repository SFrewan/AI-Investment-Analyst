using System.Text.Json;
using AI.Investment.Domain.Auditing;
using AI.Investment.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>Maps the append-only audit trail.</summary>
/// <remarks>
/// <para>
/// <c>Details</c> is stored as PostgreSQL <c>jsonb</c>. The set of useful detail keys grows with
/// every phase - agent identity, model, prompt version, approval, measured outcome - and a
/// migration per new key would be friction with no benefit, because these values are read for
/// investigation rather than joined on. The fields that ARE queried - correlation, capability,
/// outcome, risk tier - are proper indexed columns.
/// </para>
/// <para>
/// Mapped through the backing field rather than the <c>IReadOnlyDictionary</c> property, so the
/// domain type keeps its immutable surface.
/// </para>
/// </remarks>
public sealed class AuditRecordConfiguration : IEntityTypeConfiguration<AuditRecord>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    public void Configure(EntityTypeBuilder<AuditRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("audit_records");

        builder.HasKey(a => a.AuditRecordId);

        builder.Property(a => a.AuditRecordId).HasColumnName("id").ValueGeneratedNever();

        builder.Property(a => a.CorrelationId)
            .HasColumnName("correlation_id")
            .HasMaxLength(CorrelationId.MaxLength)
            .HasConversion(c => c.Value, value => CorrelationId.Create(value))
            .IsRequired();

        builder.Property(a => a.OccurredAtUtc).HasColumnName("occurred_at_utc").IsRequired();

        builder.Property(a => a.EventType)
            .HasColumnName("event_type")
            .HasConversion<string>()
            .HasMaxLength(60)
            .IsRequired();

        builder.Property(a => a.Actor).HasColumnName("actor").HasMaxLength(120).IsRequired();

        builder.Property(a => a.ActorKind)
            .HasColumnName("actor_kind")
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        builder.Property(a => a.Summary)
            .HasColumnName("summary")
            .HasMaxLength(AuditRecord.MaxSummaryLength)
            .IsRequired();

        builder.Property(a => a.ProposalId).HasColumnName("proposal_id");
        builder.Property(a => a.DecisionId).HasColumnName("decision_id");
        builder.Property(a => a.ExecutionId).HasColumnName("execution_id");

        builder.Property(a => a.Capability)
            .HasColumnName("capability")
            .HasConversion<string>()
            .HasMaxLength(60);

        builder.Property(a => a.ActionType).HasColumnName("action_type").HasMaxLength(100);

        builder.Property(a => a.Outcome)
            .HasColumnName("outcome")
            .HasConversion<string>()
            .HasMaxLength(40);

        builder.Property(a => a.RiskTier)
            .HasColumnName("risk_tier")
            .HasConversion<string>()
            .HasMaxLength(40);

        builder.Property<Dictionary<string, string>>("_details")
            .HasColumnName("details")
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, JsonOptions),
                json => JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
                        ?? new Dictionary<string, string>(StringComparer.Ordinal),
                new ValueComparer<Dictionary<string, string>>(
                    (left, right) => left != null && right != null && left.SequenceEqual(right),
                    dictionary => dictionary.Aggregate(
                        0,
                        (hash, pair) => HashCode.Combine(hash, pair.Key.GetHashCode(StringComparison.Ordinal))),
                    dictionary => new Dictionary<string, string>(dictionary, StringComparer.Ordinal)))
            .IsRequired();

        builder.HasIndex(a => a.CorrelationId).HasDatabaseName("ix_audit_records_correlation_id");
        builder.HasIndex(a => a.OccurredAtUtc).HasDatabaseName("ix_audit_records_occurred_at_utc");
        builder.HasIndex(a => a.ProposalId).HasDatabaseName("ix_audit_records_proposal_id");
    }
}
