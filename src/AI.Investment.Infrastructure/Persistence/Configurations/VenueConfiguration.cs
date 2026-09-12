using AI.Investment.Domain.Securities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Venue"/> to <c>venues</c>.
/// </summary>
/// <remarks>
/// <para>
/// The key is the venue code, converted to a scalar the same way <c>SourceId</c> keys
/// <c>data_sources</c> - a single-value wrapper stored as the string it wraps, read back through its
/// own factory so that a row cannot become an instance the factory would have refused.
/// </para>
/// <para>
/// <c>valid_from</c> and <c>valid_to</c> are <c>date</c>, not timestamps. A venue's validity is
/// stated in days by every source that states it at all, and widening it to an instant would invite
/// a time-of-day that no evidence supports.
/// </para>
/// </remarks>
public sealed class VenueConfiguration : IEntityTypeConfiguration<Venue>
{
    public void Configure(EntityTypeBuilder<Venue> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("venues");

        builder.HasKey(v => v.Id);

        builder.Property(v => v.Id)
            .HasColumnName("id")
            .HasMaxLength(VenueId.MaxLength)
            .HasConversion(id => id.Value, value => VenueId.Create(value))
            .ValueGeneratedNever();

        builder.Property(v => v.Name)
            .HasColumnName("name")
            .HasMaxLength(Venue.MaxNameLength)
            .IsRequired();

        builder.Property(v => v.CountryCode)
            .HasColumnName("country_code")
            .HasMaxLength(Venue.CountryCodeLength);

        builder.Property(v => v.ValidFrom).HasColumnName("valid_from").IsRequired();

        builder.Property(v => v.ValidTo).HasColumnName("valid_to");

        builder.Property(v => v.RecordedAtUtc).HasColumnName("recorded_at_utc").IsRequired();

        builder.HasIndex(v => v.Name).HasDatabaseName("ix_venues_name");
    }
}
