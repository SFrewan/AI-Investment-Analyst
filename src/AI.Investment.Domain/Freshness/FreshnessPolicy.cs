using AI.Investment.Domain.Sources;

namespace AI.Investment.Domain.Freshness;

/// <summary>
/// Decides whether a source's data is current. Pure and total.
/// </summary>
/// <remarks>
/// <para>
/// Pure for the same reason <see cref="Actions.PolicyEngine"/> and
/// <see cref="Retention.RetentionPolicy"/> are: "why was this not refreshed?" is asked long after
/// the fact, and an answer that depended on a clock, a database or a network cannot be
/// reconstructed. Everything this needs is a parameter.
/// </para>
/// <para>
/// <strong>Staleness is meaningless without an expectation.</strong> A quarterly filing three weeks
/// old is perfectly current; a price three weeks old is worthless. That expectation lives on the
/// source as its <see cref="UpdateCadence"/>, which is why this takes a source rather than a
/// duration.
/// </para>
/// <para>
/// <strong>Where this fails, it fails towards refreshing.</strong> That is the opposite of the
/// default elsewhere in the platform, and deliberately so. Every other gate guards an irreversible
/// or consequential act, so uncertainty must deny. Here the two errors are asymmetric in the other
/// direction: wrongly refreshing costs one redundant request, while wrongly reporting stale data as
/// current means every downstream decision is made on data nobody knows is old. The reversible
/// mistake is the one to make.
/// </para>
/// </remarks>
public static class FreshnessPolicy
{
    /// <summary>The source is not active, so nothing is scheduled to refresh it.</summary>
    public const string InactiveRule = "freshness.source-inactive@1";

    /// <summary>Event-driven or on-demand: it publishes when something happens and cannot be late.</summary>
    public const string NoExpectedIntervalRule = "freshness.no-expected-interval@1";

    /// <summary>
    /// A cadence that should carry an interval does not. Treated as needing a refresh.
    /// </summary>
    /// <remarks>
    /// Unreachable through <see cref="UpdateCadence.Every"/>, which requires a positive interval.
    /// It exists for the value that arrives from somewhere else - a future build's cadence kind, a
    /// row written by an older version - because the alternative is a source that silently never
    /// refreshes and never explains why.
    /// </remarks>
    public const string IntervalUnknownRule = "freshness.interval-unknown@1";

    /// <summary>No successful run has ever been recorded.</summary>
    public const string NeverIngestedRule = "freshness.never-ingested@1";

    /// <summary>Elapsed time exceeds the expected interval plus grace.</summary>
    public const string OverdueRule = "freshness.overdue@1";

    /// <summary>Refreshed within its expected interval.</summary>
    public const string CurrentRule = "freshness.current@1";

    /// <summary>
    /// The slack allowed before a source is called late.
    /// </summary>
    /// <remarks>
    /// A provider that publishes "daily" does not publish every 86,400 seconds, and a report that
    /// flagged every source a few minutes after its nominal interval would be noise that trains
    /// its readers to ignore it.
    /// </remarks>
    public static TimeSpan DefaultGrace { get; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Assesses one source against the last time it was successfully ingested.
    /// </summary>
    /// <param name="source">The registered source.</param>
    /// <param name="lastRefreshedAtUtc">
    /// When its last successful run completed, or null if there has never been one.
    /// </param>
    /// <param name="nowUtc">The moment to assess against.</param>
    /// <param name="grace">Slack before lateness is reported. Defaults to <see cref="DefaultGrace"/>.</param>
    public static FreshnessAssessment Assess(
        DataSource source,
        DateTime? lastRefreshedAtUtc,
        DateTime nowUtc,
        TimeSpan? grace = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var elapsed = lastRefreshedAtUtc is { } last ? nowUtc - last : (TimeSpan?)null;

        // Rule 1. An inactive source is switched off, not late. Reporting it as overdue would fill
        // the report with sources nobody intends to refresh and bury the ones that matter.
        if (!source.IsActive)
        {
            return new FreshnessAssessment(
                FreshnessState.NotScheduled, InactiveRule, lastRefreshedAtUtc, elapsed);
        }

        var cadence = source.Cadence;

        // Rule 2. No interval by design.
        if (cadence.Kind is CadenceKind.EventDriven or CadenceKind.OnDemand)
        {
            return new FreshnessAssessment(
                FreshnessState.NotScheduled, NoExpectedIntervalRule, lastRefreshedAtUtc, elapsed);
        }

        // Rule 3. No interval, but a kind that should have one. See IntervalUnknownRule.
        if (!cadence.HasExpectedInterval)
        {
            return new FreshnessAssessment(
                FreshnessState.Overdue, IntervalUnknownRule, lastRefreshedAtUtc, elapsed);
        }

        // Rule 4. Never fetched. Distinct from overdue: this may be a configuration problem rather
        // than a provider one, and the two want different responses.
        if (lastRefreshedAtUtc is not { } lastRefreshed)
        {
            return new FreshnessAssessment(
                FreshnessState.NeverIngested, NeverIngestedRule, null, null);
        }

        // Rule 5. The interval question itself, asked of the type that owns it.
        var effectiveGrace = grace ?? DefaultGrace;

        if (cadence.IsOverdue(lastRefreshed, nowUtc, effectiveGrace))
        {
            return new FreshnessAssessment(
                FreshnessState.Overdue, OverdueRule, lastRefreshed, elapsed);
        }

        // Rule 6. Current, including the case where the last run is timestamped in the future -
        // a clock skew between machines, which is not a staleness problem and must not be reported
        // as one.
        return new FreshnessAssessment(
            FreshnessState.Current, CurrentRule, lastRefreshed, elapsed);
    }
}
