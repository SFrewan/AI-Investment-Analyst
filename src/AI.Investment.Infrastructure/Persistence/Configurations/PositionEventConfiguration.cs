using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Portfolio;
using AI.Investment.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AI.Investment.Infrastructure.Persistence.Configurations;

/// <summary>Maps the append-only record of how fills moved holdings.</summary>
/// <remarks>
/// <para>
/// <strong>The unique index on the venue reference is the idempotency mechanism.</strong> Not a
/// check-then-insert in application code, which two concurrent callers both pass, and not a
/// convention: the database refuses the second row, and the store reports it as "already applied"
/// rather than doubling a holding. It is the one constraint in this table that has to be right.
/// </para>
/// <para>
/// <strong>There is no quantity, cost or profit column</strong>, here or anywhere. Those are
/// projections of these rows, for the same reason balances are projections of ledger entries.
/// </para>
/// </remarks>
public sealed class PositionEventConfiguration : IEntityTypeConfiguration<PositionEvent>
{
    public void Configure(EntityTypeBuilder<PositionEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("position_events");

        builder.HasKey(e => e.PositionEventId);

        builder.Property(e => e.PositionEventId).HasColumnName("id").ValueGeneratedNever();

        builder.Property(e => e.Instrument)
            .HasColumnName("instrument")
            .HasMaxLength(PositionEvent.MaxInstrumentLength)
            .IsRequired();

        builder.Property(e => e.Change)
            .HasColumnName("change")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(e => e.Quantity)
            .HasColumnName("quantity")
            .HasPrecision(18, 8)
            .IsRequired();

        builder.Property(e => e.VenueReference)
            .HasColumnName("venue_reference")
            .HasMaxLength(PositionEvent.MaxVenueReferenceLength)
            .IsRequired();

        builder.Property(e => e.OccurredAtUtc).HasColumnName("occurred_at_utc").IsRequired();

        // An explicit converter over the non-nullable type, as on the ledger: EF composes the
        // nullability itself, whereas a lambda written against the nullable form binds to the
        // wrong overload.
        builder.Property(e => e.OpportunityId)
            .HasColumnName("opportunity_id")
            .HasConversion(new ValueConverter<OpportunityId, Guid>(
                id => id.Value,
                value => new OpportunityId(value)));

        builder.OwnsOne(e => e.Price, money =>
        {
            money.Property(m => m.Amount).HasColumnName("price").HasPrecision(18, 4).IsRequired();

            money.Property(m => m.Currency)
                .HasColumnName("price_currency")
                .HasMaxLength(3)
                .HasConversion(currency => currency.Code, value => Currency.Create(value))
                .IsRequired();

            money.Ignore(m => m.IsZero);
            money.Ignore(m => m.IsPositive);
            money.Ignore(m => m.IsNegative);
        });

        builder.Navigation(e => e.Price).IsRequired();

        builder.OwnsOne(e => e.Fees, money =>
        {
            money.Property(m => m.Amount).HasColumnName("fees").HasPrecision(18, 4).IsRequired();

            money.Property(m => m.Currency)
                .HasColumnName("fees_currency")
                .HasMaxLength(3)
                .HasConversion(currency => currency.Code, value => Currency.Create(value))
                .IsRequired();

            money.Ignore(m => m.IsZero);
            money.Ignore(m => m.IsPositive);
            money.Ignore(m => m.IsNegative);
        });

        builder.Navigation(e => e.Fees).IsRequired();

        builder.Ignore(e => e.Notional);

        // The constraint that makes applying a fill twice impossible.
        builder.HasIndex(e => e.VenueReference)
            .IsUnique()
            .HasDatabaseName("ux_position_events_venue_reference");

        builder.HasIndex(e => e.Instrument).HasDatabaseName("ix_position_events_instrument");
        builder.HasIndex(e => e.OccurredAtUtc).HasDatabaseName("ix_position_events_occurred_at_utc");
    }
}
