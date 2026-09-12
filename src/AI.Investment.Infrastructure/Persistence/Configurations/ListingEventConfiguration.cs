using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Securities;
using AI.Investment.Domain.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="ListingEvent"/> to <c>listing_events</c>, append-only.
/// </summary>
/// <remarks>
/// <para>
/// This table is in the write guard's append-only set. Nothing here may be updated or deleted, for
/// the same reason the audit and ingestion tables may not be: a listing history that can be edited
/// answers historical questions with today's opinion.
/// </para>
/// <para>
/// <c>effective_date</c> is a <c>date</c> because a transition takes effect on a day, and
/// <c>published_at_utc</c> is a timestamp because knowability is an instant. Two indexes exist for
/// the one query this model is built to serve - the status of a pairing as at a date, using only
/// what had been published - and they are composite for that reason rather than one per column.
/// </para>
/// </remarks>
public sealed class ListingEventConfiguration : IEntityTypeConfiguration<ListingEvent>
{
    public void Configure(EntityTypeBuilder<ListingEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("listing_events");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(e => e.SecurityId)
            .HasColumnName("security_id")
            .HasConversion(id => id.Value, value => SecurityId.Create(value))
            .IsRequired();

        builder.Property(e => e.VenueId)
            .HasColumnName("venue_id")
            .HasMaxLength(VenueId.MaxLength)
            .HasConversion(id => id.Value, value => VenueId.Create(value))
            .IsRequired();

        builder.Property(e => e.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(e => e.EffectiveDate).HasColumnName("effective_date").IsRequired();

        builder.Property(e => e.ReasonCode)
            .HasColumnName("reason_code")
            .HasMaxLength(ListingEvent.MaxReasonCodeLength)
            .IsRequired();

        builder.Property(e => e.SourceId)
            .HasColumnName("source_id")
            .HasMaxLength(SourceId.MaxLength)
            .HasConversion(id => id.Value, value => SourceId.Create(value))
            .IsRequired();

        builder.Property(e => e.EvidenceContentHash)
            .HasColumnName("evidence_content_hash")
            .HasMaxLength(ContentHash.HexLength)
            .HasConversion(
                hash => hash == null ? null : hash.Value,
                value => value == null ? null : ContentHash.Create(value));

        builder.Property(e => e.PublishedAtUtc).HasColumnName("published_at_utc").IsRequired();

        builder.Property(e => e.RecordedAtUtc).HasColumnName("recorded_at_utc").IsRequired();

        builder.HasIndex(e => new { e.SecurityId, e.VenueId, e.EffectiveDate })
            .HasDatabaseName("ix_listing_events_pairing_effective");

        builder.HasIndex(e => e.PublishedAtUtc)
            .HasDatabaseName("ix_listing_events_published_at_utc");
    }
}
