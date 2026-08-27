using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Analytics.Financial;

/// <summary>
/// Two periods of the same subject, ordered, for the measures that describe a change.
/// </summary>
/// <remarks>
/// The invariants here are what keep a growth figure meaningful: the two periods must belong to the
/// same subject, be denominated in the same currency, and be in the right order. A "growth" computed
/// from two different companies, or with the periods swapped, is a number with a plausible shape and
/// no meaning at all.
/// </remarks>
public sealed class FigureComparison
{
    private FigureComparison(ReportedFigures current, ReportedFigures prior)
    {
        Current = current;
        Prior = prior;
    }

    public ReportedFigures Current { get; }

    public ReportedFigures Prior { get; }

    public static FigureComparison Create(ReportedFigures current, ReportedFigures prior)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(prior);

        if (!current.Subject.Equals(prior.Subject))
        {
            throw new DomainRuleViolationException(
                "FigureComparison.SubjectMismatch",
                $"A period-over-period comparison must be about one subject. Received " +
                $"{current.Subject} and {prior.Subject}.");
        }

        if (!current.Currency.Equals(prior.Currency))
        {
            throw new DomainRuleViolationException(
                "FigureComparison.CurrencyMismatch",
                $"The two periods are reported in {current.Currency} and {prior.Currency}. " +
                "Comparing them without conversion would measure the exchange rate as growth.");
        }

        if (prior.PeriodEndUtc >= current.PeriodEndUtc)
        {
            throw new DomainRuleViolationException(
                "FigureComparison.PeriodOrder",
                $"The prior period ({prior.PeriodEndUtc:O}) must end before the current one " +
                $"({current.PeriodEndUtc:O}). Reversed, every growth figure changes sign.");
        }

        return new FigureComparison(current, prior);
    }

    public override string ToString() =>
        $"{Current.Subject}: {Prior.PeriodEndUtc:yyyy-MM-dd} -> {Current.PeriodEndUtc:yyyy-MM-dd}";
}
