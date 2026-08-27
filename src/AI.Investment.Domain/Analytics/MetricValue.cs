using System.Globalization;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Analytics;

/// <summary>
/// A measured number together with what it means.
/// </summary>
/// <remarks>
/// <para>
/// The number alone is not a measurement. 0.184 is a ratio, a percent, a count of nothing in
/// particular, or a sum of money whose currency was lost - and only one of those is true. Carrying
/// the unit with the amount is what lets a later comparison refuse to subtract dollars from days
/// rather than silently producing a figure.
/// </para>
/// <para>
/// A plain <see cref="decimal"/> rather than <see cref="Percentage"/>: that type guards a
/// presentation range which a legitimate analytic result can exceed, and a calculation must not
/// throw because a company's revenue grew unusually fast.
/// </para>
/// </remarks>
public sealed record MetricValue
{
    private MetricValue(decimal amount, UnitOfMeasure unit, Currency? currency)
    {
        Amount = amount;
        Unit = unit;
        Currency = currency;
    }

    public decimal Amount { get; }

    public UnitOfMeasure Unit { get; }

    /// <summary>Set when, and only when, <see cref="Unit"/> is <see cref="UnitOfMeasure.Money"/>.</summary>
    public Currency? Currency { get; }

    /// <summary>A dimensionless proportion, where 0.184 means 18.4%.</summary>
    public static MetricValue Ratio(decimal amount) => Create(amount, UnitOfMeasure.Ratio);

    /// <summary>A proportion in percentage points, where 18.4 means 18.4%.</summary>
    public static MetricValue Percent(decimal amount) => Create(amount, UnitOfMeasure.Percent);

    public static MetricValue Money(decimal amount, Currency currency) =>
        Create(amount, UnitOfMeasure.Money, currency);

    public static MetricValue Count(decimal amount) => Create(amount, UnitOfMeasure.Count);

    public static MetricValue Days(decimal amount) => Create(amount, UnitOfMeasure.Days);

    public static MetricValue Create(decimal amount, UnitOfMeasure unit, Currency? currency = null)
    {
        if (!Enum.IsDefined(unit) || unit == UnitOfMeasure.Unknown)
        {
            throw new DomainValidationException(
                nameof(unit),
                $"'{unit}' is not a unit a measurement may be stored with. A number whose unit is " +
                "unknown cannot be compared with anything.");
        }

        if (unit == UnitOfMeasure.Money && currency is null)
        {
            throw new DomainValidationException(
                nameof(currency),
                "An amount of money must state its currency. A figure recorded without one cannot " +
                "be compared, converted or added later without guessing.");
        }

        if (unit != UnitOfMeasure.Money && currency is not null)
        {
            throw new DomainValidationException(
                nameof(currency),
                $"A {unit} is not money, so a currency on it would be meaningless.");
        }

        return new MetricValue(amount, unit, currency);
    }

    /// <summary>Whether this value can be compared with <paramref name="other"/> at all.</summary>
    public bool IsComparableWith(MetricValue other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return Unit == other.Unit && Equals(Currency, other.Currency);
    }

    public override string ToString() => Unit switch
    {
        UnitOfMeasure.Percent => string.Create(CultureInfo.InvariantCulture, $"{Amount:0.####}%"),
        UnitOfMeasure.Money => string.Create(CultureInfo.InvariantCulture, $"{Amount:0.##} {Currency}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{Amount:0.####} ({Unit})"),
    };
}
