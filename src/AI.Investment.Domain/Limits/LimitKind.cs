namespace AI.Investment.Domain.Limits;

/// <summary>
/// The kinds of ceiling the limit engine enforces before anything executes.
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is zero and is never a configurable limit. A limit whose kind was never set
/// would otherwise be silently interpreted as whichever kind happened to be first, which is the sort
/// of defect that only shows up when the limit fails to stop something.
/// </remarks>
public enum LimitKind
{
    /// <summary>Never valid on a configured limit.</summary>
    Unknown = 0,

    /// <summary>The largest exposure a single action may create.</summary>
    MaxPositionSize = 1,

    /// <summary>The largest total exposure across everything currently open.</summary>
    MaxTotalExposure = 2,

    /// <summary>The most that may be lost in one day before everything stops.</summary>
    MaxDailyLoss = 3,

    /// <summary>The largest fall from peak equity that may be tolerated.</summary>
    MaxDrawdown = 4,

    /// <summary>How many actions one capability may take in a day.</summary>
    MaxActionsPerCapabilityPerDay = 5,

    /// <summary>How much one operating cycle may spend on providers and models.</summary>
    MaxCostPerCycle = 6,

    /// <summary>The share of total exposure any single instrument may represent.</summary>
    MaxConcentration = 7,

    /// <summary>How long to stand down after a realised loss.</summary>
    CooldownAfterLoss = 8,

    /// <summary>Only instruments on the list may be acted on.</summary>
    InstrumentAllowList = 9,
}
