using System.Text.Json;
using AI.Investment.Domain.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>Maps the source registry.</summary>
/// <remarks>
/// <para>
/// Two mapping strategies, chosen per property rather than uniformly.
/// </para>
/// <para>
/// <strong>Single-value wrappers become converted scalars.</strong> <c>SourceId</c> and
/// <c>Region</c> each hold one string, so a converter is lossless and the column stays queryable
/// and indexable. Both read back through their factories - <c>SourceId.Create</c>, not a bypass
/// constructor - so a row that violates a domain rule fails loudly on load rather than becoming an
/// invalid object in memory.
/// </para>
/// <para>
/// <strong>Multi-field value objects become owned types.</strong> Licensing, verification policy
/// and cadence each carry several fields that are genuinely worth querying - "which sources may we
/// redistribute?" is a real question - so they are flattened into real columns rather than
/// serialised into one. EF materialises them through their private constructors, whose parameter
/// names match their properties.
/// </para>
/// <para>
/// Categories are the exception: a set with no useful ordering, queried by containment rather than
/// joined on. <c>jsonb</c> holds it in one column, and PostgreSQL can still index containment if
/// that ever becomes a hot path.
/// </para>
/// </remarks>
public sealed class DataSourceConfiguration : IEntityTypeConfiguration<DataSource>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    public void Configure(EntityTypeBuilder<DataSource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("data_sources");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id)
            .HasColumnName("id")
            .HasMaxLength(SourceId.MaxLength)
            .HasConversion(id => id.Value, value => SourceId.Create(value))
            .ValueGeneratedNever();

        builder.Property(s => s.Name)
            .HasColumnName("name")
            .HasMaxLength(DataSource.MaxNameLength)
            .IsRequired();

        builder.Property(s => s.Type)
            .HasColumnName("type")
            .HasConversion<string>()
            .HasMaxLength(60)
            .IsRequired();

        builder.Property(s => s.Authority)
            .HasColumnName("authority")
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        builder.Property(s => s.Reliability)
            .HasColumnName("reliability")
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        builder.Property(s => s.Region)
            .HasColumnName("region")
            .HasMaxLength(10)
            .HasConversion(region => region.Code, value => Region.Create(value))
            .IsRequired();

        builder.Property(s => s.IsActive).HasColumnName("is_active").IsRequired();

        builder.Property(s => s.Description)
            .HasColumnName("description")
            .HasMaxLength(DataSource.MaxDescriptionLength);

        builder.Property(s => s.RegisteredAtUtc).HasColumnName("registered_at_utc").IsRequired();
        builder.Property(s => s.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();

        // Computed, so there is nothing to store and nothing to set on load.
        builder.Ignore(s => s.IsAuthoritative);
        builder.Ignore(s => s.Categories);

        // Mapped through the backing field, so the aggregate keeps its read-only surface.
        // Serialised by NAME rather than by numeric value: a jsonb document holding "MarketPrices"
        // survives an enum being renumbered, and is legible to anyone reading the table.
        builder.Property<HashSet<DataCategory>>("_categories")
            .HasColumnName("categories")
            .HasColumnType("jsonb")
            .HasConversion(
                categories => SerialiseCategories(categories),
                json => DeserialiseCategories(json),
                new ValueComparer<HashSet<DataCategory>>(
                    (left, right) => left != null && right != null && left.SetEquals(right),
                    set => set.Aggregate(0, (hash, category) => hash ^ category.GetHashCode()),
                    set => new HashSet<DataCategory>(set)))
            .IsRequired();

        builder.OwnsOne(s => s.Cadence, cadence =>
        {
            cadence.Property(c => c.Kind)
                .HasColumnName("cadence_kind")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();

            // Npgsql maps TimeSpan? to interval. Null for event-driven and on-demand sources,
            // which is what "never stale on a timer" looks like in the schema.
            cadence.Property(c => c.ExpectedInterval).HasColumnName("cadence_interval");

            cadence.Ignore(c => c.HasExpectedInterval);
        });

        builder.Navigation(s => s.Cadence).IsRequired();

        builder.OwnsOne(s => s.Licensing, licensing =>
        {
            licensing.Property(l => l.StorageAllowed).HasColumnName("licence_storage_allowed").IsRequired();
            licensing.Property(l => l.RedistributionAllowed).HasColumnName("licence_redistribution_allowed").IsRequired();
            licensing.Property(l => l.AutomatedProcessingAllowed).HasColumnName("licence_processing_allowed").IsRequired();
            licensing.Property(l => l.AttributionRequired).HasColumnName("licence_attribution_required").IsRequired();

            licensing.Property(l => l.Notes)
                .HasColumnName("licence_notes")
                .HasMaxLength(LicensingTerms.MaxNotesLength);
        });

        builder.Navigation(s => s.Licensing).IsRequired();

        builder.OwnsOne(s => s.Verification, verification =>
        {
            verification.Property(v => v.CanConfirmAlone)
                .HasColumnName("verification_can_confirm_alone")
                .IsRequired();

            verification.Property(v => v.RequiredIndependentSources)
                .HasColumnName("verification_required_sources")
                .IsRequired();
        });

        builder.Navigation(s => s.Verification).IsRequired();

        // Ingestion asks "which active sources cover this?" far more often than anything else.
        builder.HasIndex(s => s.IsActive).HasDatabaseName("ix_data_sources_is_active");
        builder.HasIndex(s => s.Region).HasDatabaseName("ix_data_sources_region");
    }

    private static string SerialiseCategories(HashSet<DataCategory> categories) =>
        JsonSerializer.Serialize(
            categories.Select(category => category.ToString()).OrderBy(name => name, StringComparer.Ordinal),
            JsonOptions);

    private static HashSet<DataCategory> DeserialiseCategories(string json)
    {
        var names = JsonSerializer.Deserialize<List<string>>(json, JsonOptions);
        var set = new HashSet<DataCategory>();

        if (names is null)
        {
            return set;
        }

        foreach (var name in names)
        {
            // A category this build does not recognise is skipped rather than crashing the load.
            // The alternative - refusing to materialise the whole source - would mean a rollback
            // that removed a category made every source using it unreadable.
            if (Enum.TryParse<DataCategory>(name, ignoreCase: false, out var category))
            {
                set.Add(category);
            }
        }

        return set;
    }
}
