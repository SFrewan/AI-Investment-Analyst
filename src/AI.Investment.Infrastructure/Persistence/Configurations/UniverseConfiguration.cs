using System.Globalization;
using System.Text.Json;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Universe;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Universe"/> to <c>universes</c>, one row per sealed version.
/// </summary>
/// <remarks>
/// <para>
/// The key is the fingerprint, stored as the string every declaration already spells. There is no
/// surrogate and no "is current" column: selecting a universe is ordering on
/// <c>sealed_at_utc</c> and taking the greatest at or before the reader's instant, and a current
/// flag would be read instead by everything that wanted an easy answer.
/// </para>
/// <para>
/// <c>cohort_cut_dates</c> is <c>jsonb</c> rather than a child table, following the way an
/// <c>IngestionRun</c> holds its artifact hashes. The list is small, fixed at sealing, read whole
/// and never queried element-wise, so a table of five rows per universe would add a join and a
/// second thing to keep consistent without answering any question the array cannot.
/// </para>
/// </remarks>
public sealed class UniverseConfiguration : IEntityTypeConfiguration<Universe>
{
    public void Configure(EntityTypeBuilder<Universe> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("universes");

        builder.HasKey(u => u.Id);

        builder.Property(u => u.Id)
            .HasColumnName("id")
            .HasMaxLength(UniverseId.MaxLength)
            .HasConversion(id => id.Value, value => UniverseId.Create(value))
            .ValueGeneratedNever();

        builder.Property(u => u.WindowFrom).HasColumnName("window_from").IsRequired();

        builder.Property(u => u.WindowTo).HasColumnName("window_to").IsRequired();

        builder.Property(u => u.SealedAtUtc).HasColumnName("sealed_at_utc").IsRequired();

        builder.Property(u => u.ManifestContentHash)
            .HasColumnName("manifest_content_hash")
            .HasMaxLength(ContentHash.HexLength)
            .HasConversion(hash => hash.Value, value => ContentHash.Create(value))
            .IsRequired();

        builder.Property(u => u.PopulationDefinition)
            .HasColumnName("population_definition")
            .HasMaxLength(Universe.MaxRuleTextLength)
            .IsRequired();

        builder.Property(u => u.MembershipRule)
            .HasColumnName("membership_rule")
            .HasMaxLength(Universe.MaxRuleTextLength)
            .IsRequired();

        builder.Property(u => u.RecordedAtUtc).HasColumnName("recorded_at_utc").IsRequired();

        // Derived from the list beside it; there is nothing to store.
        builder.Ignore(u => u.FinalCut);

        builder.Ignore(u => u.CohortCutDates);

        builder.Property<List<DateOnly>>("_cohortCutDates")
            .HasColumnName("cohort_cut_dates")
            .HasColumnType("jsonb")
            .HasConversion(
                dates => Serialise(dates),
                json => Deserialise(json),
                new ValueComparer<List<DateOnly>>(
                    (left, right) => left != null && right != null && left.SequenceEqual(right),
                    list => list.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
                    list => new List<DateOnly>(list)))
            .IsRequired();

        builder.HasIndex(u => u.SealedAtUtc).HasDatabaseName("ix_universes_sealed_at_utc");
    }

    internal static string Serialise(List<DateOnly> dates) =>
        JsonSerializer.Serialize(
            dates.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList());

    internal static List<DateOnly> Deserialise(string json) =>
        (JsonSerializer.Deserialize<List<string>>(json) ?? [])
            .Select(s => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToList();
}
