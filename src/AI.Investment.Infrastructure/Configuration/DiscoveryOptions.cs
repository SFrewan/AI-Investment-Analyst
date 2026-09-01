using System.ComponentModel.DataAnnotations;
using AI.Investment.Application.Opportunities;
using AI.Investment.Domain.Opportunities.Equity;

namespace AI.Investment.Infrastructure.Configuration;

/// <summary>
/// What the price-recovery screen reads, and how strict it is.
/// </summary>
/// <remarks>
/// <para>
/// The four rule parameters are here rather than as constants in the rule because each of them is a
/// stated judgement that changes what a stored opportunity meant. They sit in configuration under
/// change control, next to the validation window and the benchmark, for the same reason those do: a
/// number that decides what counts as a result should be changed by a reviewable act rather than by
/// whoever is running the process.
/// </para>
/// <para>
/// The defaults are the rule's own, and every one of them errs towards producing nothing. Sixty
/// sessions before the screen looks at all, a ten per cent fall before it says anything, and five
/// past occurrences before it will state a rate.
/// </para>
/// </remarks>
public sealed class DiscoveryOptions
{
    public const string SectionName = "Discovery";

    /// <summary>The observation attribute closing prices are read from.</summary>
    /// <remarks>
    /// The same attribute the market-data normaliser writes and the validation run reads. A test
    /// asserts the three agree, because a mismatch produces an empty report rather than an error.
    /// </remarks>
    [Required]
    public string PriceAttribute { get; init; } = DiscoverySettings.Standard.PriceAttribute;

    /// <summary>The currency a candidate's economics are denominated in.</summary>
    [Required]
    public string Currency { get; init; } = DiscoverySettings.Standard.CurrencyCode;

    /// <summary>The most recent sessions read, and cited as evidence.</summary>
    [Range(2, 2000)]
    public int MaxSessions { get; init; } = DiscoverySettings.Standard.MaxSessions;

    /// <summary>The least history the screen will look at.</summary>
    [Range(2, 2000)]
    public int MinimumSessions { get; init; } = PriceRecoveryParameters.Standard.MinimumSessions;

    /// <summary>How far below its own highest close the latest close must be.</summary>
    [Range(0.0001, 0.9999)]
    public decimal DrawdownRatio { get; init; } = PriceRecoveryParameters.Standard.DrawdownRatio;

    /// <summary>How many sessions a recovery is measured over.</summary>
    [Range(1, 500)]
    public int HorizonSessions { get; init; } = PriceRecoveryParameters.Standard.HorizonSessions;

    /// <summary>How many past occurrences are needed before a rate may be stated at all.</summary>
    [Range(1, 1000)]
    public int MinimumTrials { get; init; } = PriceRecoveryParameters.Standard.MinimumTrials;

    /// <summary>
    /// The registered source a price review acquires from before it screens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This had no key at all until now. <c>DiscoverySettings</c> has carried a
    /// <c>PriceSourceId</c> since the screen was written and <c>ToSettings</c> never mapped it, so
    /// the value in force was always the shipped default whatever an operator put in
    /// configuration - a setting that looked configurable and was not, which is the same class of
    /// defect as a limit that reads as enforced and cannot bind.
    /// </para>
    /// <para>
    /// Blank is a real arrangement rather than a disabled feature: it means acquire nothing and
    /// screen what is already stored, which is what an installation whose data arrives by another
    /// route wants, and what a test seeding observations directly wants.
    /// </para>
    /// </remarks>
    public string PriceSourceId { get; init; } = DiscoverySettings.Standard.PriceSourceId;

    /// <summary>The registered source share splits are acquired from.</summary>
    public string SplitSourceId { get; init; } = DiscoverySettings.Standard.SplitSourceId;

    /// <summary>The observation attribute share splits are read from.</summary>
    [Required]
    public string SplitAttribute { get; init; } = DiscoverySettings.Standard.SplitAttribute;

    /// <summary>
    /// The largest single-session move left unexplained by a known split before the series is
    /// refused rather than screened.
    /// </summary>
    /// <remarks>
    /// Bounded well away from zero: a tolerance near zero refuses every real series, and one at or
    /// above 1 cannot refuse a total collapse, which is the case it exists to catch.
    /// </remarks>
    [Range(0.05, 0.95)]
    public decimal MaxUnexplainedMove { get; init; } = DiscoverySettings.Standard.MaxUnexplainedMove;

    /// <summary>The plain settings object the application layer takes.</summary>
    /// <remarks>
    /// <paramref name="eventThresholdRatio"/> is the validation run's own threshold, handed in
    /// rather than configured here. The screen states a probability of an event and the validation
    /// run scores that event; two settings keys for one number is how they came to describe
    /// different events, and one argument is how that stops being possible.
    /// </remarks>
    public DiscoverySettings ToSettings(
        decimal eventThresholdRatio = 0m) => new()
    {
        PriceAttribute = PriceAttribute,
        PriceSourceId = PriceSourceId,
        SplitSourceId = SplitSourceId,
        SplitAttribute = SplitAttribute,
        MaxUnexplainedMove = MaxUnexplainedMove,
        CurrencyCode = Currency,
        MaxSessions = MaxSessions,
        Rule = new PriceRecoveryParameters(
            MinimumSessions,
            DrawdownRatio,
            HorizonSessions,
            MinimumTrials,
            eventThresholdRatio),
    };
}
