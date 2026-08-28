using AI.Investment.Domain.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>Maps escalations.</summary>
/// <remarks>
/// The index is on resolution and expiry together, because the question asked of this table by the
/// unattended-operation criterion is exactly "which of these expired without an answer" - and a
/// measurement that is expensive to take is a measurement that stops being taken.
/// </remarks>
public sealed class EscalationConfiguration : IEntityTypeConfiguration<Escalation>
{
    public void Configure(EntityTypeBuilder<Escalation> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("escalations");

        builder.HasKey(e => e.EscalationId);

        builder.Property(e => e.EscalationId).HasColumnName("id").ValueGeneratedNever();
        builder.Property(e => e.CycleId).HasColumnName("cycle_id");
        builder.Property(e => e.ProposalId).HasColumnName("proposal_id");

        builder.Property(e => e.Capability)
            .HasColumnName("capability")
            .HasConversion<string>()
            .HasMaxLength(60)
            .IsRequired();

        builder.Property(e => e.Reason)
            .HasColumnName("reason")
            .HasConversion<string>()
            .HasMaxLength(60)
            .IsRequired();

        builder.Property(e => e.Explanation)
            .HasColumnName("explanation")
            .HasMaxLength(Escalation.MaxExplanationLength)
            .IsRequired();

        builder.Property(e => e.RaisedAtUtc).HasColumnName("raised_at_utc").IsRequired();
        builder.Property(e => e.ExpiresAtUtc).HasColumnName("expires_at_utc").IsRequired();
        builder.Property(e => e.AcknowledgedAtUtc).HasColumnName("acknowledged_at_utc");

        builder.Property(e => e.AcknowledgedBy)
            .HasColumnName("acknowledged_by")
            .HasMaxLength(Escalation.MaxActorLength);

        builder.Property(e => e.ResolvedAtUtc).HasColumnName("resolved_at_utc");

        builder.Property(e => e.Resolution)
            .HasColumnName("resolution")
            .HasMaxLength(Escalation.MaxResolutionLength + Escalation.MaxActorLength + 2);

        builder.Ignore(e => e.IsResolved);
        builder.Ignore(e => e.IsAcknowledged);

        builder.HasIndex(e => new { e.ResolvedAtUtc, e.ExpiresAtUtc })
            .HasDatabaseName("ix_escalations_outstanding");
    }
}
