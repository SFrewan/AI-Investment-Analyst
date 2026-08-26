using System.Globalization;

namespace AI.Investment.Domain.Freshness;

/// <summary>
/// What was concluded about a source's currency, and which rule concluded it.
/// </summary>
/// <remarks>
/// Carries the rule id for the same reason every other decision in this system does: a conclusion
/// that cannot be traced to a named, versioned rule cannot be explained later, and "why did nothing
/// refresh this?" is a question asked months after the fact.
/// </remarks>
/// <param name="State">The conclusion.</param>
/// <param name="RuleId">The versioned rule that reached it.</param>
/// <param name="LastRefreshedAtUtc">
/// When the last successful run completed, or null when there has never been one.
/// </param>
/// <param name="Elapsed">
/// Time since that run, or null when there has never been one. Null is not zero: zero would mean
/// "just refreshed", which is the opposite of what never having run means.
/// </param>
public sealed record FreshnessAssessment(
    FreshnessState State,
    string RuleId,
    DateTime? LastRefreshedAtUtc,
    TimeSpan? Elapsed)
{
    /// <summary>Whether this source should be refreshed now.</summary>
    /// <remarks>
    /// True for both <see cref="FreshnessState.Overdue"/> and
    /// <see cref="FreshnessState.NeverIngested"/>. The two are reported separately because they
    /// mean different things, but they call for the same action.
    /// </remarks>
    public bool NeedsRefresh =>
        State is FreshnessState.Overdue or FreshnessState.NeverIngested;

    public override string ToString() =>
        Elapsed is { } elapsed
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{State} [{RuleId}] last refreshed {elapsed:d\\.hh\\:mm\\:ss} ago")
            : $"{State} [{RuleId}]";
}
