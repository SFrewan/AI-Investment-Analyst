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

    /// <summary>The plain settings object the application layer takes.</summary>
    public DiscoverySettings ToSettings() => new()
    {
        PriceAttribute = PriceAttribute,
        CurrencyCode = Currency,
        MaxSessions = MaxSessions,
        Rule = new PriceRecoveryParameters(
            MinimumSessions,
            DrawdownRatio,
            HorizonSessions,
            MinimumTrials),
    };
}
