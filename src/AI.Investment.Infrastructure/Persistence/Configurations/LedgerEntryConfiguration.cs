using AI.Investment.Domain.Capital;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>Maps the double-entry ledger.</summary>
/// <remarks>
/// <para>
/// Both accounts are stored as a name and a kind rather than as a foreign key to an accounts table.
/// The kind is what fixes an account's sign convention, and storing it on the entry means a balance
/// computed from these rows is correct even if somebody later edits an accounts table - which is
/// the sort of edit that silently inverts a year of history.
/// </para>
/// <para>
/// <strong>There is no balance column, here or anywhere.</strong> Balances are projections of these
/// rows. A stored balance can be wrong while every entry behind it is right, and nothing in the
/// data would say so.
/// </para>
/// </remarks>
public sealed class LedgerEntryConfiguration : IEntityTypeConfiguration<LedgerEntry>
{
    public void Configure(EntityTypeBuilder<LedgerEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("ledger_entries");

        builder.HasKey(e => e.LedgerEntryId);

        builder.Property(e => e.LedgerEntryId).HasColumnName("id").ValueGeneratedNever();

        builder.Property(e => e.OccurredAtUtc).HasColumnName("occurred_at_utc").IsRequired();

        builder.Property(e => e.Description)
            .HasColumnName("description")
            .HasMaxLength(LedgerEntry.MaxDescriptionLength)
            .IsRequired();

        // An explicit converter over the non-nullable type: EF composes the nullability itself,
        // whereas lambdas written against the nullable form bind to the wrong overload.
        builder.Property(e => e.OpportunityId)
            .HasColumnName("opportunity_id")
            .HasConversion(new ValueConverter<OpportunityId, Guid>(
                id => id.Value,
                value => new OpportunityId(value)));

        builder.Property(e => e.ExecutionId).HasColumnName("execution_id");

        builder.OwnsOne(e => e.Debit, account =>
        {
            account.Property(a => a.Name)
                .HasColumnName("debit_account")
                .HasMaxLength(LedgerAccount.MaxNameLength)
                .IsRequired();

            account.Property(a => a.Kind)
                .HasColumnName("debit_account_kind")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();

            account.Ignore(a => a.IncreasedByDebit);
        });

        builder.Navigation(e => e.Debit).IsRequired();

        builder.OwnsOne(e => e.Credit, account =>
        {
            account.Property(a => a.Name)
                .HasColumnName("credit_account")
                .HasMaxLength(LedgerAccount.MaxNameLength)
                .IsRequired();

            account.Property(a => a.Kind)
                .HasColumnName("credit_account_kind")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();

            account.Ignore(a => a.IncreasedByDebit);
        });

        builder.Navigation(e => e.Credit).IsRequired();

        builder.OwnsOne(e => e.Amount, amount =>
        {
            amount.Property(m => m.Amount)
                .HasColumnName("amount")
                .HasPrecision(18, 4)
                .IsRequired();

            amount.Property(m => m.Currency)
                .HasColumnName("currency")
                .HasMaxLength(3)
                .HasConversion(currency => currency.Code, value => Currency.Create(value))
                .IsRequired();

            amount.Ignore(m => m.IsZero);
            amount.Ignore(m => m.IsPositive);
            amount.Ignore(m => m.IsNegative);
        });

        builder.Navigation(e => e.Amount).IsRequired();

        builder.HasIndex(e => e.OccurredAtUtc).HasDatabaseName("ix_ledger_entries_occurred_at_utc");
        builder.HasIndex(e => e.OpportunityId).HasDatabaseName("ix_ledger_entries_opportunity");
    }
}
