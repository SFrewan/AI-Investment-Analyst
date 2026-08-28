using AI.Investment.Domain.Approvals;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>Maps approval tokens.</summary>
/// <remarks>
/// <para>
/// The index on <c>consumed_at_utc</c> exists for the conditional update that consumes a token: the
/// store updates the row only while it is still unconsumed, so single use is enforced by the
/// database and not merely by the aggregate. The in-memory check cannot see a concurrent caller,
/// and this is the one place where losing that race spends real money twice.
/// </para>
/// <para>
/// There is no status column. Whether a token is usable is derived from what has happened to it, so
/// there is no field that a bad write could leave in a permissive state.
/// </para>
/// </remarks>
public sealed class ApprovalTokenConfiguration : IEntityTypeConfiguration<ApprovalToken>
{
    public void Configure(EntityTypeBuilder<ApprovalToken> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("approval_tokens");

        builder.HasKey(t => t.ApprovalTokenId);

        builder.Property(t => t.ApprovalTokenId).HasColumnName("id").ValueGeneratedNever();

        builder.Property(t => t.OpportunityId)
            .HasColumnName("opportunity_id")
            .HasConversion(id => id.Value, value => new OpportunityId(value))
            .IsRequired();

        builder.Property(t => t.ProposalId).HasColumnName("proposal_id").IsRequired();

        builder.Property(t => t.Fingerprint)
            .HasColumnName("action_fingerprint")
            .HasMaxLength(64)
            .HasConversion(fingerprint => fingerprint.Value, value => ActionFingerprint.Parse(value))
            .IsRequired();

        builder.Property(t => t.ApprovedBy)
            .HasColumnName("approved_by")
            .HasMaxLength(ApprovalToken.MaxApproverLength)
            .IsRequired();

        builder.Property(t => t.IssuedAtUtc).HasColumnName("issued_at_utc").IsRequired();
        builder.Property(t => t.ExpiresAtUtc).HasColumnName("expires_at_utc").IsRequired();
        builder.Property(t => t.ConsumedAtUtc).HasColumnName("consumed_at_utc");
        builder.Property(t => t.RevokedAtUtc).HasColumnName("revoked_at_utc");

        builder.Property(t => t.RevocationReason)
            .HasColumnName("revocation_reason")
            .HasMaxLength(ApprovalToken.MaxReasonLength);

        builder.Ignore(t => t.IsConsumed);
        builder.Ignore(t => t.IsRevoked);

        builder.OwnsOne(t => t.MaxAmount, amount =>
        {
            amount.Property(m => m.Amount)
                .HasColumnName("max_amount")
                .HasPrecision(18, 4)
                .IsRequired();

            amount.Property(m => m.Currency)
                .HasColumnName("max_amount_currency")
                .HasMaxLength(3)
                .HasConversion(currency => currency.Code, value => Currency.Create(value))
                .IsRequired();

            amount.Ignore(m => m.IsZero);
            amount.Ignore(m => m.IsPositive);
            amount.Ignore(m => m.IsNegative);
        });

        builder.Navigation(t => t.MaxAmount).IsRequired();

        // The consuming update filters on this column; without the index it is a scan on the one
        // operation that must not be slow enough for a concurrent caller to overlap.
        builder.HasIndex(t => new { t.OpportunityId, t.ConsumedAtUtc })
            .HasDatabaseName("ix_approval_tokens_opportunity");
    }
}
