namespace AI.Investment.Domain.Watching;

/// <summary>What kind of thing a watch is waiting for.</summary>
/// <remarks>
/// A closed set on purpose. Every member here is something that can be observed and compared without
/// judgement - a price moved this far, a filing appeared, this much time passed. Nothing in this
/// enum requires an opinion, because a model deciding whether something is worth waking up for is
/// both unreliable and unboundedly expensive: the wake-up is what costs money, so the thing that
/// decides to wake up cannot be the thing that costs money to run.
/// </remarks>
public enum TriggerType
{
    /// <summary>Not determined. A watch in this state never fires.</summary>
    Unknown = 0,

    /// <summary>Simply that an interval has elapsed.</summary>
    Schedule = 1,

    /// <summary>A price moved by at least a stated amount.</summary>
    PriceMove = 2,

    /// <summary>Volume exceeded a stated level.</summary>
    VolumeSpike = 3,

    /// <summary>A new regulatory filing appeared for the subject.</summary>
    NewFiling = 4,

    /// <summary>A news item appeared for the subject.</summary>
    NewsEvent = 5,

    /// <summary>A computed metric crossed a stated threshold.</summary>
    MetricThreshold = 6,

    /// <summary>The data the platform holds for a subject has aged past its freshness policy.</summary>
    StaleData = 7,

    /// <summary>An opportunity's measurement date has arrived.</summary>
    OutcomeDue = 8,

    /// <summary>The safety system refused something, which is itself worth looking at.</summary>
    PolicyBreach = 9,
}
