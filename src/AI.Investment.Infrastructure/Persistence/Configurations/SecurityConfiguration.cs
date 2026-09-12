using AI.Investment.Domain.Companies;
using AI.Investment.Domain.Securities;
using AI.Investment.Domain.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="Security"/> to <c>securities</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no ticker column, and that is the design.</strong> The Security Master's
/// invariant is that a ticker is never a key; a nullable ticker column here would become one within
/// a release, because it is the field everything already joins on. Symbols live in identifier
/// assertions, which carry an interval and a source.
/// </para>
/// <para>
/// <strong>The issuer is a foreign key, not a navigation.</strong> <c>Company</c> is configured by
/// its own class and is not touched here: the relationship is declared from this side only, with no
/// navigation on either end, so nothing about the company aggregate changes shape because a security
/// now points at it.
/// </para>
/// <para>
/// <strong><see cref="Security.Identifiers"/> is mapped as of G2, and was not before.</strong> It
/// was ignored while nothing could fill it - "a table that no writer fills and no reader trusts" -
/// and the stage that seals the evidence and builds the writer is the stage that maps it. The
/// uniqueness on (kind, value) is what makes population idempotent: an identifier that already
/// exists says the security carrying it already exists, and the database refuses a second one
/// independently of whether the writer remembered to look.
/// </para>
/// <para>
/// <strong>The interval is part of the assertion, not decoration.</strong> <c>valid_from</c> is
/// required because the Security Master's invariant is that every identifier assertion carries an
/// interval and a source; <c>valid_to</c> is nullable because an assertion with no observed end is
/// a different statement from one that runs forever.
/// </para>
/// </remarks>
public sealed class SecurityConfiguration : IEntityTypeConfiguration<Security>
{
    public void Configure(EntityTypeBuilder<Security> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("securities");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => SecurityId.Create(value))
            .ValueGeneratedNever();

        builder.Property(s => s.CompanyId)
            .HasColumnName("company_id")
            .HasConversion(id => id.Value, value => CompanyId.Create(value))
            .IsRequired();

        builder.Property(s => s.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

        builder.OwnsMany(s => s.Identifiers, identifier =>
        {
            identifier.ToTable("security_identifiers");

            identifier.WithOwner().HasForeignKey("security_id");

            // A surrogate key, because no combination of the assertion's own fields is stable: the
            // same source may assert the same value over a corrected interval, and that is a new
            // claim rather than an edit of the old one.
            //
            // Generated on add rather than never. SecurityIdentifier is a record with value
            // equality and carries no id of its own, so nothing assigns one - declaring the key as
            // never-generated left every row with the empty GUID, and the second assertion in a
            // context collided with the first before any SQL was sent.
            identifier.Property<Guid>("id").HasColumnName("id").ValueGeneratedOnAdd();
            identifier.HasKey("id");

            identifier.Property(i => i.Kind)
                .HasColumnName("kind")
                .HasConversion<int>()
                .IsRequired();

            identifier.Property(i => i.Value)
                .HasColumnName("value")
                .HasMaxLength(SecurityIdentifier.MaxValueLength)
                .IsRequired();

            identifier.Property(i => i.ValidFrom).HasColumnName("valid_from").IsRequired();

            identifier.Property(i => i.ValidTo).HasColumnName("valid_to");

            identifier.Property(i => i.SourceId)
                .HasColumnName("source_id")
                .HasConversion(id => id.Value, value => SourceId.Create(value))
                .HasMaxLength(SourceId.MaxLength)
                .IsRequired();

            // One symbol names one instrument. Two securities claiming the same vendor key is the
            // ticker-as-key failure arriving through the back door, and it is refused here rather
            // than resolved by whichever row a query returned first.
            identifier.HasIndex(i => new { i.Kind, i.Value })
                .IsUnique()
                .HasDatabaseName("ux_security_identifiers_kind_value");

            identifier.HasIndex("security_id").HasDatabaseName("ix_security_identifiers_security_id");
        });

        builder.Navigation(s => s.Identifiers).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasOne<Company>()
            .WithMany()
            .HasForeignKey(s => s.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => s.CompanyId).HasDatabaseName("ix_securities_company_id");
    }
}
