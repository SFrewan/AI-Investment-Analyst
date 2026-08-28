using AI.Investment.Domain.Shadow;
using AI.Investment.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>Maps shadow measurements.</summary>
/// <remarks>
/// Append-only in the database as well as in the model: the write guard refuses every modification
/// and every deletion of one of these rows. This is the evidence a promotion to a higher autonomy
/// level would eventually be argued from, and evidence that can be edited afterwards is not evidence.
/// </remarks>
public sealed class ShadowDecisionConfiguration : IEntityTypeConfiguration<ShadowDecision>
{
    public void Configure(EntityTypeBuilder<ShadowDecision> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("shadow_decisions");

        builder.HasKey(s => s.ShadowDecisionId);

        builder.Property(s => s.ShadowDecisionId).HasColumnName("id").ValueGeneratedNever();
        builder.Property(s => s.CycleId).HasColumnName("cycle_id");
        builder.Property(s => s.ProposalId).HasColumnName("proposal_id").IsRequired();

        builder.Property(s => s.Capability)
            .HasColumnName("capability")
            .HasConversion<string>()
            .HasMaxLength(60)
            .IsRequired();

        builder.Property(s => s.ActionType)
            .HasColumnName("action_type")
            .HasMaxLength(ShadowDecision.MaxActionTypeLength)
            .IsRequired();

        builder.Property(s => s.RiskTier)
            .HasColumnName("risk_tier")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(s => s.ActualMode)
            .HasColumnName("actual_mode")
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        builder.Property(s => s.ActualOutcome)
            .HasColumnName("actual_outcome")
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(s => s.ShadowMode)
            .HasColumnName("shadow_mode")
            .HasConversion<string>()
            .HasMaxLength(40)
            .IsRequired();

        builder.Property(s => s.ShadowOutcome)
            .HasColumnName("shadow_outcome")
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(s => s.Reason)
            .HasColumnName("reason")
            .HasMaxLength(ShadowDecision.MaxReasonLength)
            .IsRequired();

        builder.Property(s => s.RecordedAtUtc).HasColumnName("recorded_at_utc").IsRequired();

        builder.Ignore(s => s.WouldHaveExecuted);
        builder.Ignore(s => s.Agreed);

        builder.OwnsOne(s => s.Exposure, exposure =>
        {
            exposure.Property(m => m.Amount)
                .HasColumnName("exposure")
                .HasPrecision(18, 4)
                .IsRequired();

            exposure.Property(m => m.Currency)
                .HasColumnName("exposure_currency")
                .HasMaxLength(3)
                .HasConversion(currency => currency.Code, value => Currency.Create(value))
                .IsRequired();

            exposure.Ignore(m => m.IsZero);
            exposure.Ignore(m => m.IsPositive);
            exposure.Ignore(m => m.IsNegative);
        });

        builder.Navigation(s => s.Exposure).IsRequired();

        builder.HasIndex(s => s.RecordedAtUtc).HasDatabaseName("ix_shadow_decisions_recorded");
    }
}
