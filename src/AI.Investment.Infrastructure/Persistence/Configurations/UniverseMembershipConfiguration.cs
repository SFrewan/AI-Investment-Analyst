using AI.Investment.Domain.Securities;
using AI.Investment.Domain.Universe;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="UniverseMembership"/> to <c>universe_memberships</c>, append-only.
/// </summary>
/// <remarks>
/// <para>
/// Two foreign keys and no third: the universe version and the security. There is deliberately no
/// path to <c>companies</c> from here. The sealed manifests key members by CIK, and a CIK identifies
/// a legal entity that may have issued several securities - joining membership to a company would
/// make the membership ambiguous in exactly the cases that matter.
/// </para>
/// <para>
/// The unique index on (universe, security) is the statement that a security is either in a sealed
/// version or is not. Being in one twice with different cuts would mean the seal disagreed with
/// itself, and the database should refuse to hold that rather than leave a reader to notice.
/// </para>
/// <para>
/// No span column: the span is derived from these cuts and the universe's window on read.
/// </para>
/// </remarks>
public sealed class UniverseMembershipConfiguration : IEntityTypeConfiguration<UniverseMembership>
{
    public void Configure(EntityTypeBuilder<UniverseMembership> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("universe_memberships");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(m => m.UniverseId)
            .HasColumnName("universe_id")
            .HasMaxLength(UniverseId.MaxLength)
            .HasConversion(id => id.Value, value => UniverseId.Create(value))
            .IsRequired();

        builder.Property(m => m.SecurityId)
            .HasColumnName("security_id")
            .HasConversion(id => id.Value, value => SecurityId.Create(value))
            .IsRequired();

        builder.Property(m => m.RecordedAtUtc).HasColumnName("recorded_at_utc").IsRequired();

        builder.Ignore(m => m.CohortCuts);

        builder.Property<List<DateOnly>>("_cohortCuts")
            .HasColumnName("cohort_cuts")
            .HasColumnType("jsonb")
            .HasConversion(
                dates => UniverseConfiguration.Serialise(dates),
                json => UniverseConfiguration.Deserialise(json),
                new ValueComparer<List<DateOnly>>(
                    (left, right) => left != null && right != null && left.SequenceEqual(right),
                    list => list.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
                    list => new List<DateOnly>(list)))
            .IsRequired();

        builder.HasOne<Universe>()
            .WithMany()
            .HasForeignKey(m => m.UniverseId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Security>()
            .WithMany()
            .HasForeignKey(m => m.SecurityId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(m => new { m.UniverseId, m.SecurityId })
            .IsUnique()
            .HasDatabaseName("ix_universe_memberships_universe_security");

        builder.HasIndex(m => m.SecurityId)
            .HasDatabaseName("ix_universe_memberships_security_id");
    }
}
