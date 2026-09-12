using System.Text.Json;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Determination"/> to <c>determinations</c>, append-only.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is not one foreign key in this table, and that is deliberate.</strong> Inputs are
/// named by content hash, never by row reference: a foreign key resolves to whatever that row says
/// now, and a hash resolves to the bytes that were actually used. This table is content-addressed
/// rather than relationally linked, which is what lets a determination be reproduced years later
/// against evidence that has since had corrections appended beside it.
/// </para>
/// <para>
/// <c>inputs</c> keeps the ordered hash list as <c>jsonb</c> - following the way an
/// <c>IngestionRun</c> holds its artifacts - and <c>inputs_hash</c> is the order-sensitive digest of
/// that same list. The list proves <em>which</em> inputs; the digest is what the unique index can
/// actually compare. Both are stored because neither alone does both jobs.
/// </para>
/// <para>
/// The unique index is the reproducibility key: rule, version, as-of instant and input digest. Two
/// rows agreeing on all four would be one determination recorded twice, and if their outputs
/// disagreed one of them would be wrong - so the database refuses the second rather than leaving a
/// reader to discover the contradiction.
/// </para>
/// </remarks>
public sealed class DeterminationConfiguration : IEntityTypeConfiguration<Determination>
{
    public void Configure(EntityTypeBuilder<Determination> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("determinations");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => DeterminationId.Create(value))
            .ValueGeneratedNever();

        builder.Property(d => d.Kind)
            .HasColumnName("kind")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(d => d.RuleId)
            .HasColumnName("rule_id")
            .HasMaxLength(Determination.MaxRuleIdLength)
            .IsRequired();

        builder.Property(d => d.RuleVersion)
            .HasColumnName("rule_version")
            .HasMaxLength(Determination.MaxRuleVersionLength)
            .IsRequired();

        builder.Property(d => d.OutputCanonical)
            .HasColumnName("output_canonical")
            .HasMaxLength(Determination.MaxOutputLength)
            .IsRequired();

        builder.Property(d => d.OutputContentHash)
            .HasColumnName("output_content_hash")
            .HasMaxLength(ContentHash.HexLength)
            .HasConversion(hash => hash.Value, value => ContentHash.Create(value))
            .IsRequired();

        builder.Property(d => d.AsOfUtc).HasColumnName("as_of_utc").IsRequired();

        builder.Property(d => d.Confidence)
            .HasColumnName("confidence")
            .HasConversion(
                confidence => confidence == null ? (decimal?)null : confidence.Value,
                value => value == null ? null : Confidence.Create(value.Value));

        builder.Property(d => d.RecordedAtUtc).HasColumnName("recorded_at_utc").IsRequired();

        builder.Ignore(d => d.Inputs);

        builder.Property<List<ContentHash>>("_inputs")
            .HasColumnName("inputs")
            .HasColumnType("jsonb")
            .HasConversion(
                hashes => Serialise(hashes),
                json => Deserialise(json),
                new ValueComparer<List<ContentHash>>(
                    (left, right) => left != null && right != null && left.SequenceEqual(right),
                    list => list.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.Value)),
                    list => new List<ContentHash>(list)))
            .IsRequired();

        // Computed from the list above, and persisted because it is what the unique index compares.
        builder.Property(d => d.InputsHash)
            .HasColumnName("inputs_hash")
            .HasMaxLength(ContentHash.HexLength)
            .IsRequired();

        builder.HasIndex(d => new { d.RuleId, d.RuleVersion, d.AsOfUtc, d.InputsHash })
            .IsUnique()
            .HasDatabaseName("ix_determinations_reproducibility");

        builder.HasIndex(d => d.AsOfUtc).HasDatabaseName("ix_determinations_as_of_utc");

        builder.HasIndex(d => d.RuleId).HasDatabaseName("ix_determinations_rule_id");
    }

    private static string Serialise(List<ContentHash> hashes) =>
        JsonSerializer.Serialize(hashes.Select(h => h.Value).ToList());

    private static List<ContentHash> Deserialise(string json) =>
        (JsonSerializer.Deserialize<List<string>>(json) ?? [])
            .Select(ContentHash.Create)
            .ToList();
}
