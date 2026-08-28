using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>Maps promotion warrants.</summary>
/// <remarks>
/// <para>
/// The lookup index matches the only question anything asks of this table - which active warrants
/// cover this capability in this environment - because that query runs before a grant of unattended
/// execution is written and a scan there would put a safety control on a slow path.
/// </para>
/// <para>
/// No unique constraint on capability plus environment. Two warrants for the same capability are a
/// perfectly ordinary thing (a narrower one issued while a wider one runs), and the grant names the
/// one it was issued under rather than the database picking.
/// </para>
/// </remarks>
public sealed class PromotionWarrantConfiguration : IEntityTypeConfiguration<PromotionWarrant>
{
    public void Configure(EntityTypeBuilder<PromotionWarrant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("promotion_warrants");

        builder.HasKey(w => w.PromotionWarrantId);

        builder.Property(w => w.PromotionWarrantId).HasColumnName("id").ValueGeneratedNever();

        builder.Property(w => w.Capability)
            .HasColumnName("capability")
            .HasConversion<string>()
            .HasMaxLength(60)
            .IsRequired();

        builder.Property(w => w.ActionType).HasColumnName("action_type").HasMaxLength(100);

        builder.Property(w => w.EnvironmentName)
            .HasColumnName("environment")
            .HasMaxLength(60)
            .IsRequired();

        builder.Property(w => w.MaxMode)
            .HasColumnName("max_mode")
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        builder.Property(w => w.MaxRiskTier)
            .HasColumnName("max_risk_tier")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(w => w.ValidationRunId).HasColumnName("validation_run_id").IsRequired();

        builder.Property(w => w.BenchmarkFingerprint)
            .HasColumnName("benchmark_fingerprint")
            .HasMaxLength(PromotionWarrant.MaxFingerprintLength)
            .IsRequired();

        builder.Property(w => w.IssuedBy)
            .HasColumnName("issued_by")
            .HasMaxLength(PromotionWarrant.MaxIssuedByLength)
            .IsRequired();

        builder.Property(w => w.Justification)
            .HasColumnName("justification")
            .HasMaxLength(PromotionWarrant.MaxJustificationLength)
            .IsRequired();

        builder.Property(w => w.IssuedAtUtc).HasColumnName("issued_at_utc").IsRequired();
        builder.Property(w => w.ExpiresAtUtc).HasColumnName("expires_at_utc").IsRequired();
        builder.Property(w => w.RevokedAtUtc).HasColumnName("revoked_at_utc");

        builder.Property(w => w.RevocationReason)
            .HasColumnName("revocation_reason")
            .HasMaxLength(AutonomyGrant.MaxReasonLength);

        builder.Ignore(w => w.IsRevoked);

        builder.OwnsOne(w => w.MaxExposure, exposure =>
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

        builder.Navigation(w => w.MaxExposure).IsRequired();

        builder.HasIndex(w => new { w.Capability, w.EnvironmentName, w.ExpiresAtUtc })
            .HasDatabaseName("ix_promotion_warrants_lookup");
    }
}

/// <summary>Maps live-venue authorisations.</summary>
/// <remarks>
/// The unique index on venue and environment is the one constraint here that is a rule rather than a
/// performance decision: one venue in one environment has at most one authorisation at a time. Two
/// would mean two different sets of signatures and ceilings applied to the same real money, and the
/// gate would have to pick one.
/// </remarks>
public sealed class LiveVenueAuthorizationConfiguration : IEntityTypeConfiguration<LiveVenueAuthorization>
{
    public void Configure(EntityTypeBuilder<LiveVenueAuthorization> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("live_venue_authorizations");

        builder.HasKey(a => a.LiveVenueAuthorizationId);

        builder.Property(a => a.LiveVenueAuthorizationId).HasColumnName("id").ValueGeneratedNever();

        builder.Property(a => a.VenueId)
            .HasColumnName("venue_id")
            .HasMaxLength(LiveVenueAuthorization.MaxVenueIdLength)
            .IsRequired();

        builder.Property(a => a.EnvironmentName)
            .HasColumnName("environment")
            .HasMaxLength(60)
            .IsRequired();

        builder.Property(a => a.PromotionWarrantId).HasColumnName("promotion_warrant_id").IsRequired();

        builder.Property(a => a.AuthorisedBy)
            .HasColumnName("authorised_by")
            .HasMaxLength(LiveVenueAuthorization.MaxSignatoryLength)
            .IsRequired();

        builder.Property(a => a.CounterSignedBy)
            .HasColumnName("counter_signed_by")
            .HasMaxLength(LiveVenueAuthorization.MaxSignatoryLength)
            .IsRequired();

        builder.Property(a => a.Justification)
            .HasColumnName("justification")
            .HasMaxLength(LiveVenueAuthorization.MaxJustificationLength)
            .IsRequired();

        builder.Property(a => a.AuthorisedAtUtc).HasColumnName("authorised_at_utc").IsRequired();
        builder.Property(a => a.ExpiresAtUtc).HasColumnName("expires_at_utc").IsRequired();
        builder.Property(a => a.WithdrawnAtUtc).HasColumnName("withdrawn_at_utc");

        builder.Property(a => a.WithdrawalReason)
            .HasColumnName("withdrawal_reason")
            .HasMaxLength(AutonomyGrant.MaxReasonLength);

        builder.Ignore(a => a.IsWithdrawn);

        builder.OwnsOne(a => a.ExposureCeiling, ceiling =>
        {
            ceiling.Property(m => m.Amount)
                .HasColumnName("exposure_ceiling")
                .HasPrecision(18, 4)
                .IsRequired();

            ceiling.Property(m => m.Currency)
                .HasColumnName("exposure_ceiling_currency")
                .HasMaxLength(3)
                .HasConversion(currency => currency.Code, value => Currency.Create(value))
                .IsRequired();

            ceiling.Ignore(m => m.IsZero);
            ceiling.Ignore(m => m.IsPositive);
            ceiling.Ignore(m => m.IsNegative);
        });

        builder.Navigation(a => a.ExposureCeiling).IsRequired();

        builder.HasIndex(a => new { a.VenueId, a.EnvironmentName })
            .IsUnique()
            .HasDatabaseName("ux_live_venue_authorizations_venue");
    }
}
