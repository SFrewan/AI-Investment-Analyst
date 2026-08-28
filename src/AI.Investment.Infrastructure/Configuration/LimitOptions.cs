using System.ComponentModel.DataAnnotations;

namespace AI.Investment.Infrastructure.Configuration;

/// <summary>The configured ceilings.</summary>
/// <remarks>
/// <para>
/// Every value is nullable and absent means "not configured", which the limit engine reads as "this
/// ceiling does not bind". That is a deliberate and uncomfortable default, so it is stated here: an
/// installation that configures nothing has no limits, and the way to have limits is to say what
/// they are. The alternative - inventing defaults - would produce ceilings nobody chose, which
/// people then work around rather than reason about.
/// </para>
/// <para>
/// The failure path is different from the empty one. If this section cannot be read at all,
/// <c>ConfiguredLimitProvider</c> returns a set that refuses everything.
/// </para>
/// </remarks>
public sealed class LimitOptions
{
    public const string SectionName = "Limits";

    /// <summary>The currency every money ceiling here is denominated in.</summary>
    [Required]
    [MinLength(3)]
    [MaxLength(3)]
    public string CurrencyCode { get; init; } = "USD";

    public decimal? MaxPositionSize { get; init; }

    public decimal? MaxTotalExposure { get; init; }

    public decimal? MaxDailyLoss { get; init; }

    public decimal? MaxDrawdown { get; init; }

    public decimal? MaxCostPerCycle { get; init; }

    public int? MaxActionsPerCapabilityPerDay { get; init; }

    /// <summary>The share of total exposure one instrument may hold, between 0 and 1.</summary>
    public decimal? MaxConcentration { get; init; }

    public int? CooldownAfterLossMinutes { get; init; }

    /// <summary>Instruments that may be acted on. Empty means no restriction.</summary>
    public IReadOnlyList<string> AllowedInstruments { get; init; } = [];
}
