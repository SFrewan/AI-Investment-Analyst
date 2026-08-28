using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Limits;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Limits;

/// <summary>
/// What the limit engine is given to compare against. A snapshot that mixes currencies, or that
/// records a loss with the wrong sign, produces ceilings that never bind.
/// </summary>
public sealed class ExposureSnapshotTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private static Money Usd(decimal amount) => Money.Create(amount, Currency.Usd);

    [Fact]
    public void A_flat_snapshot_holds_nothing_and_has_lost_nothing()
    {
        var snapshot = ExposureSnapshot.Flat(Currency.Usd, Usd(10_000m));

        Assert.True(snapshot.TotalExposure.IsZero);
        Assert.True(snapshot.RealisedLossToday.IsZero);
        Assert.True(snapshot.CycleCost.IsZero);
        Assert.True(snapshot.Drawdown.IsZero);
        Assert.Null(snapshot.LastRealisedLossAtUtc);
        Assert.Equal(0, snapshot.ActionsToday(Capability.SimulatedExecution));
        Assert.True(snapshot.ExposureTo("AAPL").IsZero);
    }

    [Fact]
    public void Drawdown_is_the_fall_from_peak_and_never_negative()
    {
        var down = ExposureSnapshot.Create(
            Currency.Usd, Usd(0m), Usd(10_000m), Usd(8_500m), Usd(0m), Usd(0m));

        var up = ExposureSnapshot.Create(
            Currency.Usd, Usd(0m), Usd(10_000m), Usd(12_000m), Usd(0m), Usd(0m));

        Assert.Equal(1_500m, down.Drawdown.Amount);
        Assert.True(up.Drawdown.IsZero);
    }

    [Fact]
    public void An_amount_in_another_currency_is_refused()
    {
        Assert.Throws<DomainValidationException>(() =>
            ExposureSnapshot.Create(
                Currency.Usd,
                Money.Create(1m, Currency.Create("EUR")),
                Usd(0m),
                Usd(0m),
                Usd(0m),
                Usd(0m)));
    }

    [Fact]
    public void A_realised_loss_is_recorded_as_a_positive_amount()
    {
        var error = Assert.Throws<DomainValidationException>(() =>
            ExposureSnapshot.Create(
                Currency.Usd, Usd(0m), Usd(0m), Usd(0m), Usd(-1m), Usd(0m)));

        Assert.Contains("positive amount", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Per_instrument_exposure_is_looked_up_without_regard_to_case()
    {
        var snapshot = ExposureSnapshot.Create(
            Currency.Usd,
            Usd(500m),
            Usd(1_000m),
            Usd(1_000m),
            Usd(0m),
            Usd(0m),
            exposureByInstrument: new Dictionary<string, Money> { ["AAPL"] = Usd(500m) });

        Assert.Equal(500m, snapshot.ExposureTo("aapl").Amount);
        Assert.True(snapshot.ExposureTo("MSFT").IsZero);
    }

    [Fact]
    public void Action_counts_are_kept_per_capability()
    {
        var snapshot = ExposureSnapshot.Create(
            Currency.Usd,
            Usd(0m),
            Usd(0m),
            Usd(0m),
            Usd(0m),
            Usd(0m),
            actionsToday: new Dictionary<Capability, int> { [Capability.SimulatedExecution] = 3 });

        Assert.Equal(3, snapshot.ActionsToday(Capability.SimulatedExecution));
        Assert.Equal(0, snapshot.ActionsToday(Capability.DataIngestion));
    }

    [Fact]
    public void A_last_loss_timestamp_must_be_utc()
    {
        Assert.Throws<DomainValidationException>(() =>
            ExposureSnapshot.Create(
                Currency.Usd,
                Usd(0m),
                Usd(0m),
                Usd(0m),
                Usd(0m),
                Usd(0m),
                lastRealisedLossAtUtc: DateTime.SpecifyKind(Now, DateTimeKind.Local)));
    }
}
