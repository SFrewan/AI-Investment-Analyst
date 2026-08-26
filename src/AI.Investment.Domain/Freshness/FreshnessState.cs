namespace AI.Investment.Domain.Freshness;

/// <summary>
/// What the platform knows about how current a source's data is.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Unknown"/> is the default so that an unset value never reads as "current". Everywhere
/// else in this system the dangerous default is permission; here it is reassurance.
/// </para>
/// <para>
/// <see cref="NeverIngested"/> is deliberately distinct from <see cref="Overdue"/>. A source that
/// has never been fetched and a source whose data has gone stale need different responses - the
/// first may be misconfigured, the second is a provider or scheduling problem - and collapsing
/// them into "not current" would hide which.
/// </para>
/// </remarks>
public enum FreshnessState
{
    /// <summary>Not assessed. Never a conclusion, only an unset value.</summary>
    Unknown = 0,

    /// <summary>Refreshed within its expected interval.</summary>
    Current = 1,

    /// <summary>Past its expected interval plus grace.</summary>
    Overdue = 2,

    /// <summary>No successful run has ever been recorded for it.</summary>
    NeverIngested = 3,

    /// <summary>
    /// Not measured against a clock: inactive, event-driven, or on demand. A source that publishes
    /// when something happens cannot be late.
    /// </summary>
    NotScheduled = 4,
}
