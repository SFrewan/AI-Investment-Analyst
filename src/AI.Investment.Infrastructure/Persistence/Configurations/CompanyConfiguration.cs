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

        builder.Property(c => c.Cik)
            .HasColumnName("cik")
            .HasMaxLength(Cik.Digits)
            .HasConversion(c => c!.Value, value => Cik.Create(value));

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

        builder.HasIndex(c => c.Name).HasDatabaseName("ix_companies_name");

        // Unique WHEN PRESENT, which is a different constraint from unique.
        //
        // A plain unique index would be wrong in PostgreSQL for the opposite of the obvious reason:
        // it permits many NULLs already, so it would appear to work. The filter is here to say what
        // is actually meant - at most one company may claim a given SEC filer, and a company with no
        // SEC identity is ordinary rather than a row awaiting a value. Writing the intent down is
        // what stops the next person reading the absence of a filter as an oversight and "fixing" it
        // into a required column.
        builder.HasIndex(c => c.Cik)
            .IsUnique()
            .HasFilter("cik IS NOT NULL")
            .HasDatabaseName("ix_companies_cik");
    }
}
