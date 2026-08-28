using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>Maps autonomy grants.</summary>
/// <remarks>
/// <para>
/// The lookup index matches the resolver's question exactly - capability, environment, expiry -
/// because that query runs before every unattended action and a scan there would put the safety
/// control on the critical path of everything.
/// </para>
/// <para>
/// There is no unique constraint on capability plus environment, deliberately. Two equally specific
/// grants are a configuration mistake, and the resolver refuses them rather than resolving them
/// arbitrarily; a database constraint would instead make the second grant unwritable, which hides
/// the mistake at the point where it is easiest to see.
/// </para>
/// </remarks>
public sealed class AutonomyGrantConfiguration : IEntityTypeConfiguration<AutonomyGrant>
{
    public void Configure(EntityTypeBuilder<AutonomyGrant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("autonomy_grants");

        builder.HasKey(g => g.AutonomyGrantId);

        builder.Property(g => g.AutonomyGrantId).HasColumnName("id").ValueGeneratedNever();

        builder.Property(g => g.Capability)
            .HasColumnName("capability")
            .HasConversion<string>()
            .HasMaxLength(60)
            .IsRequired();

        builder.Property(g => g.ActionType).HasColumnName("action_type").HasMaxLength(100);

        builder.Property(g => g.EnvironmentName)
            .HasColumnName("environment")
            .HasMaxLength(60)
            .IsRequired();

        builder.Property(g => g.GrantedMode)
            .HasColumnName("granted_mode")
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        builder.Property(g => g.DemotedMode)
            .HasColumnName("demoted_mode")
            .HasConversion<string>()
            .HasMaxLength(40);

        builder.Property(g => g.MaxRiskTier)
            .HasColumnName("max_risk_tier")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(g => g.LimitSetName)
            .HasColumnName("limit_set")
            .HasMaxLength(AutonomyGrant.MaxLimitSetLength)
            .IsRequired();

        builder.Property(g => g.GrantedBy)
            .HasColumnName("granted_by")
            .HasMaxLength(AutonomyGrant.MaxGrantedByLength)
            .IsRequired();

        builder.Property(g => g.GrantedAtUtc).HasColumnName("granted_at_utc").IsRequired();
        builder.Property(g => g.ExpiresAtUtc).HasColumnName("expires_at_utc").IsRequired();
        builder.Property(g => g.RevokedAtUtc).HasColumnName("revoked_at_utc");

        builder.Property(g => g.RevocationReason)
            .HasColumnName("revocation_reason")
            .HasMaxLength(AutonomyGrant.MaxReasonLength);

        builder.Property(g => g.DemotedAtUtc).HasColumnName("demoted_at_utc");

        builder.Property(g => g.DemotionReason)
            .HasColumnName("demotion_reason")
            .HasMaxLength(AutonomyGrant.MaxReasonLength);

        builder.Property(g => g.DemotionCount).HasColumnName("demotion_count").IsRequired();

        builder.Ignore(g => g.IsRevoked);
        builder.Ignore(g => g.EffectiveMode);

        builder.OwnsOne(g => g.MaxExposure, exposure =>
        {
            exposure.Property(m => m.Amount)
                .HasColumnName("max_exposure")
                .HasPrecision(18, 4)
                .IsRequired();

            exposure.Property(m => m.Currency)
                .HasColumnName("max_exposure_currency")
                .HasMaxLength(3)
                .HasConversion(currency => currency.Code, value => Currency.Create(value))
                .IsRequired();

            exposure.Ignore(m => m.IsZero);
            exposure.Ignore(m => m.IsPositive);
            exposure.Ignore(m => m.IsNegative);
        });

        builder.Navigation(g => g.MaxExposure).IsRequired();

        builder.HasIndex(g => new { g.Capability, g.EnvironmentName, g.ExpiresAtUtc })
            .HasDatabaseName("ix_autonomy_grants_lookup");
    }
}
