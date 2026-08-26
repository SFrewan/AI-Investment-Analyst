using System.Text.Json;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>Maps what the platform knows.</summary>
/// <remarks>
/// <para>
/// The table every later phase reads from, so its indexes are chosen for the two questions that
/// actually get asked: everything known about a subject as at a date, and the latest value of one
/// attribute as at a date. Both filter on <c>published_at_utc</c> - never on the period a value
/// describes - because filtering on the wrong one produces look-ahead bias that cannot be corrected
/// afterwards.
/// </para>
/// <para>
/// Values are stored as a kind plus one canonical, culture-invariant string. A column per type
/// would grow with every normaliser, and a number round-trips through <c>decimal</c> without loss.
/// </para>
/// </remarks>
public sealed class ObservationConfiguration : IEntityTypeConfiguration<Observation>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    public void Configure(EntityTypeBuilder<Observation> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("observations");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new ObservationId(value))
            .ValueGeneratedNever();

        builder.Property(o => o.Attribute)
            .HasColumnName("attribute")
            .HasMaxLength(Observation.MaxAttributeLength)
            .IsRequired();

        builder.Property(o => o.Kind)
            .HasColumnName("claim_kind")
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        // Null on every fact, which is every observation this phase produces. Converters are not
        // applied to nulls, so the nullable column needs no special handling.
        builder.Property(o => o.Confidence)
            .HasColumnName("confidence")
            .HasPrecision(5, 4)
            .HasConversion(
                confidence => confidence!.Value,
                value => Domain.ValueObjects.Confidence.Create(value));

        builder.Ignore(o => o.PublishedAtUtc);
        builder.Ignore(o => o.Caveats);

        builder.Property<List<string>>("_caveats")
            .HasColumnName("caveats")
            .HasColumnType("jsonb")
            .HasConversion(
                caveats => JsonSerializer.Serialize(caveats, JsonOptions),
                json => JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new List<string>(),
                new ValueComparer<List<string>>(
                    (left, right) => left != null && right != null && left.SequenceEqual(right),
                    list => list.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
                    list => new List<string>(list)))
            .IsRequired();

        builder.OwnsOne(o => o.Subject, subject =>
        {
            subject.Property(s => s.Kind)
                .HasColumnName("subject_kind")
                .HasMaxLength(IngestionSubject.MaxKindLength)
                .IsRequired();

            subject.Property(s => s.Identifier)
                .HasColumnName("subject_identifier")
                .HasMaxLength(IngestionSubject.MaxIdentifierLength);

            subject.Ignore(s => s.IsSpecific);

            // "Everything about this subject." Declared inside the owned builder because that is
            // where the properties live; owned types share the owner's table, so the index lands
            // on `observations`.
            subject.HasIndex(s => new { s.Kind, s.Identifier })
                .HasDatabaseName("ix_observations_subject");
        });

        builder.Navigation(o => o.Subject).IsRequired();

        builder.OwnsOne(o => o.Value, value =>
        {
            value.Property(v => v.Kind)
                .HasColumnName("value_kind")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();

            value.Property(v => v.Canonical)
                .HasColumnName("value")
                .HasMaxLength(ObservationValue.MaxTextLength)
                .IsRequired();
        });

        builder.Navigation(o => o.Value).IsRequired();

        builder.OwnsOne(o => o.Provenance, provenance =>
        {
            provenance.Property(p => p.SourceId)
                .HasColumnName("source_id")
                .HasMaxLength(SourceId.MaxLength)
                .HasConversion(id => id.Value, value => SourceId.Create(value))
                .IsRequired();

            provenance.Property(p => p.SourceRecordId)
                .HasColumnName("source_record_id")
                .HasMaxLength(Provenance.MaxSourceRecordIdLength);

            provenance.Property(p => p.SourceUrl)
                .HasColumnName("source_url")
                .HasMaxLength(2000)
                .HasConversion(uri => uri!.ToString(), value => new Uri(value));

            provenance.Property(p => p.AsOfUtc).HasColumnName("as_of_utc").IsRequired();
            provenance.Property(p => p.PublishedAtUtc).HasColumnName("published_at_utc").IsRequired();
            provenance.Property(p => p.RetrievedAtUtc).HasColumnName("retrieved_at_utc").IsRequired();

            // Every historical read filters on publication, never on the period a value describes.
            provenance.HasIndex(p => p.PublishedAtUtc)
                .HasDatabaseName("ix_observations_published_at_utc");
        });

        builder.Navigation(o => o.Provenance).IsRequired();

        // "The latest value of this attribute for this subject, as at date X." An equality match,
        // combined with the subject and publication indexes above.
        builder.HasIndex(o => o.Attribute).HasDatabaseName("ix_observations_attribute");
    }
}
