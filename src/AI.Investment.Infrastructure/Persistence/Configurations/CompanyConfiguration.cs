using AI.Investment.Domain.Companies;
using AI.Investment.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>Maps the <see cref="Company"/> aggregate.</summary>
/// <remarks>
/// Value objects are stored as their primitive representation through converters. Reading goes
/// back through the factory - <c>Ticker.Create</c>, not a bypass constructor - so a row that
/// somehow violates a domain rule fails loudly on load rather than becoming an invalid object
/// in memory. Slower by a negligible amount; the alternative is a domain type whose invariants
/// hold only for objects the application created.
/// </remarks>
public sealed class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("companies");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new CompanyId(value))
            .ValueGeneratedNever();

        builder.Property(c => c.Name)
            .HasColumnName("name")
            .HasMaxLength(Company.MaxNameLength)
            .IsRequired();

        builder.Property(c => c.Ticker)
            .HasColumnName("ticker")
            .HasMaxLength(Ticker.MaxLength)
            .HasConversion(t => t.Value, value => Ticker.Create(value))
            .IsRequired();

        builder.Property(c => c.Exchange)
            .HasColumnName("exchange")
            .HasMaxLength(Exchange.MaxLength)
            .HasConversion(e => e!.Code, value => Exchange.Create(value));

        builder.Property(c => c.Sector)
            .HasColumnName("sector")
            .HasMaxLength(Company.MaxClassificationLength);

        builder.Property(c => c.Industry)
            .HasColumnName("industry")
            .HasMaxLength(Company.MaxClassificationLength);

        builder.Property(c => c.Country)
            .HasColumnName("country")
            .HasMaxLength(Company.MaxClassificationLength);

        builder.Property(c => c.Description)
            .HasColumnName("description")
            .HasMaxLength(Company.MaxDescriptionLength);

        builder.Property(c => c.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(c => c.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();

        // One company per ticker. Enforced by the database, not only by the handler's check:
        // two concurrent creates would both pass an application-level check and both insert.
        builder.HasIndex(c => c.Ticker).IsUnique().HasDatabaseName("ix_companies_ticker");
        builder.HasIndex(c => c.Name).HasDatabaseName("ix_companies_name");
    }
}
