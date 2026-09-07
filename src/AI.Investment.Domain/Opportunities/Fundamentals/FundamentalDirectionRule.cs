using System.Globalization;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Opportunities.Fundamentals;

/// <summary>One reported financial figure, as it was published.</summary>
/// <param name="Attribute">The figure's attribute, such as <c>financials.revenue</c>.</param>
/// <param name="PeriodEndUtc">The end of the period the figure describes.</param>
/// <param name="PublishedAtUtc">When the figure became public.</param>
/// <param name="Value">The reported amount.</param>
/// <remarks>
/// <para>
/// Both dates are carried because a restated figure is the same period published twice, and which
/// value was known on a given date depends entirely on which publication had happened by then.
/// </para>
/// <para>
/// Deliberately not <c>Analytics.Financial.ReportedFigure</c>, which carries a unit and a full
/// evidence claim because the calculators need them. This rule needs four fields and nothing else,
/// and borrowing the heavier type would make every caller construct provenance it never reads.
/// </para>
/// </remarks>
public sealed record FiledFigure(
    string Attribute,
    DateTime PeriodEndUtc,
    DateTime PublishedAtUtc,
    decimal Value);

/// <summary>Why the rule declined to make a claim.</summary>
public enum DirectionRefusal
{
    /// <summary>It made one.</summary>
    None = 0,

    /// <summary>Fewer prior period-to-period transitions than the rule requires.</summary>
    NotEnoughPriorTransitions = 1,

    /// <summary>Nothing had been published yet, so there is no value to compare against.</summary>
    NoCurrentFigure = 2,

    /// <summary>The series handed in contains something the rule will not read.</summary>
    MalformedSeries = 3,
}

/// <summary>What the rule concluded at one decision point.</summary>
public sealed record DirectionVerdict
{
    private DirectionVerdict(
        bool fired,
        DirectionRefusal refusal,
        decimal probability,
        int priorTransitions,
        int priorIncreases,
        DateTime currentPeriodEndUtc,
        decimal currentValue)
    {
        Fired = fired;
        Refusal = refusal;
        Probability = probability;
        PriorTransitions = priorTransitions;
        PriorIncreases = priorIncreases;
        CurrentPeriodEndUtc = currentPeriodEndUtc;
        CurrentValue = currentValue;
    }

    /// <summary>True when the rule stated a probability.</summary>
    public bool Fired { get; }

    public DirectionRefusal Refusal { get; }

    /// <summary>The stated probability that the next reported figure is at least the current one.</summary>
    public decimal Probability { get; }

    /// <summary>How many period-to-period transitions were visible at the decision point.</summary>
    public int PriorTransitions { get; }

    /// <summary>How many of those were increases.</summary>
    public int PriorIncreases { get; }

    /// <summary>The latest period whose figure was public at the decision point.</summary>
    public DateTime CurrentPeriodEndUtc { get; }

    /// <summary>The value that was public for that period at the decision point.</summary>
    public decimal CurrentValue { get; }

    internal static DirectionVerdict Silent(DirectionRefusal refusal) =>
        new(false, refusal, 0m, 0, 0, default, 0m);

    internal static DirectionVerdict Claim(
        decimal probability,
        int priorTransitions,
        int priorIncreases,
        DateTime currentPeriodEndUtc,
        decimal currentValue) =>
        new(
            true,
            DirectionRefusal.None,
            probability,
            priorTransitions,
            priorIncreases,
            currentPeriodEndUtc,
            currentValue);

    public override string ToString() =>
        Fired
            ? string.Create(CultureInfo.InvariantCulture, $"claims {Probability:0.0000} from {PriorIncreases}/{PriorTransitions}")
            : Refusal.ToString();
}

/// <summary>
/// A fundamentals strategy: will the next reported figure be at least as large as the last one?
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this strategy exists.</strong> Every price-based claim needs a future price to
/// resolve, and this installation holds one year of prices against twenty years of filings. A claim
/// whose declared event is a <em>reported figure</em> rather than a price move resolves entirely
/// from what is already stored, over two decades rather than one year, at no cost and with no
/// provider call. That is the whole point of it: it lets the admission machinery be exercised
/// against real evidence while the price question waits for a decision.
/// </para>
/// <para>
/// <strong>What it claims.</strong> At the moment a figure for some period becomes public, the rule
/// states the probability that the <em>next</em> period's first reported figure will be at least
/// this one. The event is a growth ratio at or above zero, compared with <c>&gt;=</c>, which is the
/// same comparison the outcome labeller applies, so the declared event and the scored event are one
/// event.
/// </para>
/// <para>
/// <strong>How it forms the probability.</strong> The frequency of increases among the transitions
/// visible at the decision point, Laplace-smoothed. The smoothing is not a tuned parameter and was
/// fixed before anything was scored: an unsmoothed rule seeing three increases out of three would
/// state certainty, and stating certainty from three observations is an overclaim whatever the
/// outcome turns out to be. It has no free parameters beyond
/// <see cref="MinimumPriorTransitions"/>, which is likewise fixed in advance.
/// </para>
/// <para>
/// <strong>Point-in-time.</strong> The rule refuses a series containing anything published after the
/// decision instant, rather than trusting the caller to have filtered it. A look-ahead that has to
/// be prevented by discipline at every call site is one that will eventually happen at one of them.
/// For each period it reads the latest publication at or before the decision, which is what was
/// known then - not the final restated value, which is what a naive query returns.
/// </para>
/// </remarks>
public static class FundamentalDirectionRule
{
    /// <summary>
    /// How many prior transitions must be visible before the rule will speak.
    /// </summary>
    /// <remarks>
    /// Three. Fixed in advance and never varied against the scored evidence: sweeping it and
    /// keeping the value that scored best would be exactly the in-sample fitting the admission gate
    /// refuses by name.
    /// </remarks>
    public const int MinimumPriorTransitions = 3;

    /// <summary>The event: the next figure at or above the current one, so a growth ratio of zero.</summary>
    public static Percentage EventThreshold => Percentage.FromRatio(0m);

    /// <summary>
    /// Reads the figures known at one instant and states a probability about the next one.
    /// </summary>
    /// <param name="knownAtDecision">
    /// Every publication of one company's one attribute that had happened by
    /// <paramref name="decidedAtUtc"/>. Anything later is a defect and is refused, not ignored.
    /// </param>
    /// <param name="decidedAtUtc">The instant the decision is taken.</param>
    /// <param name="minimumPriorTransitions">Overridable for tests only; production uses the constant.</param>
    public static DirectionVerdict Evaluate(
        IReadOnlyList<FiledFigure> knownAtDecision,
        DateTime decidedAtUtc,
        int minimumPriorTransitions = MinimumPriorTransitions)
    {
        ArgumentNullException.ThrowIfNull(knownAtDecision);
        DateRange.EnsureUtc(decidedAtUtc, nameof(decidedAtUtc));

        if (minimumPriorTransitions < 1)
        {
            throw new DomainValidationException(
                nameof(minimumPriorTransitions),
                "A rule that will speak from no history at all has nothing to speak from. One " +
                "transition is the least that can be called a frequency.");
        }

        foreach (var figure in knownAtDecision)
        {
            if (figure is null)
            {
                return DirectionVerdict.Silent(DirectionRefusal.MalformedSeries);
            }

            // The look-ahead guard, enforced here rather than assumed of the caller.
            if (figure.PublishedAtUtc > decidedAtUtc)
            {
                return DirectionVerdict.Silent(DirectionRefusal.MalformedSeries);
            }

            if (figure.PublishedAtUtc < figure.PeriodEndUtc)
            {
                return DirectionVerdict.Silent(DirectionRefusal.MalformedSeries);
            }
        }

        // What was known then, per period: the latest publication at or before the decision. Taking
        // the newest publication outright would import restatements the decision could not see.
        var asKnown = knownAtDecision
            .GroupBy(figure => figure.PeriodEndUtc)
            .Select(group => group
                .OrderByDescending(figure => figure.PublishedAtUtc)
                .First())
            .OrderBy(figure => figure.PeriodEndUtc)
            .ToList();

        if (asKnown.Count == 0)
        {
            return DirectionVerdict.Silent(DirectionRefusal.NoCurrentFigure);
        }

        var transitions = asKnown.Count - 1;

        if (transitions < minimumPriorTransitions)
        {
            return DirectionVerdict.Silent(DirectionRefusal.NotEnoughPriorTransitions);
        }

        var increases = 0;

        for (var i = 1; i < asKnown.Count; i++)
        {
            if (asKnown[i].Value >= asKnown[i - 1].Value)
            {
                increases++;
            }
        }

        // Laplace: an unsmoothed 3/3 would state certainty from three observations.
        var probability = (increases + 1m) / (transitions + 2m);
        var current = asKnown[^1];

        return DirectionVerdict.Claim(
            probability,
            transitions,
            increases,
            current.PeriodEndUtc,
            current.Value);
    }

    /// <summary>
    /// Whether the claim came true: the next figure at or above the one the claim was made against.
    /// </summary>
    /// <remarks>
    /// The comparison is <c>&gt;=</c>, matching <see cref="EventThreshold"/> exactly. If these two
    /// ever disagree the Brier score measures the gap between them rather than the strategy, which
    /// is the mistake the admission gate's event check exists to catch.
    /// </remarks>
    public static bool Occurred(decimal nextValue, decimal currentValue) => nextValue >= currentValue;
}
