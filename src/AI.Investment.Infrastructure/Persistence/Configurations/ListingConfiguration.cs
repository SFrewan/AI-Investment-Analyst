using AI.Investment.Domain.Securities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Listing"/> to <c>listings</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The key is the pair, and there is no status column.</strong> A listing is the join
/// between a security and a venue; what it did is in <c>listing_events</c>. A <c>status</c> column
/// here would have to be overwritten on every transition, which is the mutation this whole model
/// exists to replace - and it would immediately become the column every query reads, leaving the
/// event history correct and unused.
/// </para>
/// <para>
/// The composite key also makes the duplicate impossible to express: one security cannot hold two
/// listing rows on one venue, so there is no question of which of them the events belong to.
/// </para>
/// </remarks>
public sealed class ListingConfiguration : IEntityTypeConfiguration<Listing>
{
    public void Configure(EntityTypeBuilder<Listing> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("listings");

        builder.HasKey(l => new { l.SecurityId, l.VenueId });

        builder.Property(l => l.SecurityId)
            .HasColumnName("security_id")
            .HasConversion(id => id.Value, value => SecurityId.Create(value));

        builder.Property(l => l.VenueId)
            .HasColumnName("venue_id")
            .HasMaxLength(VenueId.MaxLength)
            .HasConversion(id => id.Value, value => VenueId.Create(value));

        builder.Property(l => l.OpenedAtUtc).HasColumnName("opened_at_utc").IsRequired();

        builder.HasMany(l => l.Events)
            .WithOne()
            .HasForeignKey(e => new { e.SecurityId, e.VenueId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.Navigation(l => l.Events).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasOne<Security>()
            .WithMany()
            .HasForeignKey(l => l.SecurityId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Venue>()
            .WithMany()
            .HasForeignKey(l => l.VenueId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(l => l.VenueId).HasDatabaseName("ix_listings_venue_id");
    }
}
