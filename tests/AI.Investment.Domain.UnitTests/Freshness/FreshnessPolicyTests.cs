using AI.Investment.Domain.Freshness;
using AI.Investment.Domain.Sources;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Freshness;

/// <summary>
/// When the platform is allowed to say its data is current.
/// </summary>
/// <remarks>
/// <para>
/// Every rule is asserted by id rather than only by state. Two rules can reach the same conclusion
/// for different reasons - an inactive source and an event-driven one are both
/// <see cref="FreshnessState.NotScheduled"/> - and a test that checked only the state would pass
/// while the report told an operator the wrong thing about why.
/// </para>
/// <para>
/// The direction of failure matters here and is asserted directly: where this policy is unsure, it
/// must say "refresh", because a redundant fetch costs one request and a wrongly reassuring report
/// costs every decision made downstream of it.
/// </para>
/// </remarks>
public sealed class FreshnessPolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    private static DataSource Source(UpdateCadence cadence, bool active = true)
    {
        var source = DataSource.Register(
            SourceId.Create("test-source"),
            "Test Source",
            SourceType.RegulatoryAuthority,
            SourceAuthority.Primary,
            Region.UnitedStates,
            [DataCategory.CompanyProfile],
            cadence,
            LicensingTerms.OpenData(),
            VerificationPolicy.Authoritative,
            Now.AddYears(-1));

        if (active)
        {
            source.Activate(Now.AddYears(-1));
        }

        return source;
    }

    // ---------- not measured against a clock ----------

    [Fact]
    public void An_inactive_source_is_not_late()
    {
        // Switched off, not overdue. Reporting it as late would fill the queue with sources nobody
        // intends to refresh and bury the ones that matter.
        var assessment = FreshnessPolicy.Assess(
            Source(UpdateCadence.Daily(), active: false),
            Now.AddYears(-5),
            Now);

        Assert.Equal(FreshnessState.NotScheduled, assessment.State);
        Assert.Equal(FreshnessPolicy.InactiveRule, assessment.RuleId);
        Assert.False(assessment.NeedsRefresh);
    }

    [Fact]
    public void An_inactive_source_is_not_late_even_when_it_has_never_run() =>

        // The inactive rule is checked first on purpose: "switched off" explains the absence of
        // data better than "never ingested" does.
        Assert.Equal(
            FreshnessPolicy.InactiveRule,
            FreshnessPolicy.Assess(
                Source(UpdateCadence.Daily(), active: false), null, Now).RuleId);

    [Fact]
    public void An_event_driven_source_cannot_be_late()
    {
        // A regulator publishes when a company files, not on a timer. There is no interval to be
        // late against, and inventing one would report every quiet week as a fault.
        var assessment = FreshnessPolicy.Assess(
            Source(UpdateCadence.EventDriven),
            Now.AddYears(-5),
            Now);

        Assert.Equal(FreshnessState.NotScheduled, assessment.State);
        Assert.Equal(FreshnessPolicy.NoExpectedIntervalRule, assessment.RuleId);
    }

    [Fact]
    public void An_on_demand_source_cannot_be_late() =>
        Assert.Equal(
            FreshnessPolicy.NoExpectedIntervalRule,
            FreshnessPolicy.Assess(Source(UpdateCadence.OnDemand), Now.AddYears(-5), Now).RuleId);

    // ---------- never fetched ----------

    [Fact]
    public void A_source_that_has_never_run_is_reported_as_such()
    {
        var assessment = FreshnessPolicy.Assess(Source(UpdateCadence.Daily()), null, Now);

        // Distinct from overdue: this may be a configuration problem rather than a provider one,
        // and the two want different responses.
        Assert.Equal(FreshnessState.NeverIngested, assessment.State);
        Assert.Equal(FreshnessPolicy.NeverIngestedRule, assessment.RuleId);
        Assert.True(assessment.NeedsRefresh);
    }

    [Fact]
    public void Never_having_run_leaves_elapsed_null_rather_than_zero()
    {
        var assessment = FreshnessPolicy.Assess(Source(UpdateCadence.Daily()), null, Now);

        // Zero would mean "just refreshed", which is the opposite of what never running means.
        Assert.Null(assessment.Elapsed);
        Assert.Null(assessment.LastRefreshedAtUtc);
    }

    // ---------- the interval question ----------

    [Fact]
    public void A_source_refreshed_within_its_interval_is_current()
    {
        var assessment = FreshnessPolicy.Assess(
            Source(UpdateCadence.Daily()),
            Now.AddHours(-2),
            Now);

        Assert.Equal(FreshnessState.Current, assessment.State);
        Assert.Equal(FreshnessPolicy.CurrentRule, assessment.RuleId);
        Assert.False(assessment.NeedsRefresh);
    }

    [Fact]
    public void A_source_past_its_interval_and_grace_is_overdue()
    {
        var assessment = FreshnessPolicy.Assess(
            Source(UpdateCadence.Daily()),
            Now.AddDays(-3),
            Now);

        Assert.Equal(FreshnessState.Overdue, assessment.State);
        Assert.Equal(FreshnessPolicy.OverdueRule, assessment.RuleId);
        Assert.True(assessment.NeedsRefresh);
    }

    [Fact]
    public void Grace_is_applied_before_a_source_is_called_late()
    {
        // Twenty-six hours since a daily refresh. Past the interval, inside the default six-hour
        // grace - a provider that publishes "daily" does not publish every 86,400 seconds, and a
        // report that flagged this would be noise.
        var assessment = FreshnessPolicy.Assess(
            Source(UpdateCadence.Daily()),
            Now.AddHours(-26),
            Now);

        Assert.Equal(FreshnessState.Current, assessment.State);
    }

    [Fact]
    public void An_explicit_grace_overrides_the_default()
    {
        var lastRefreshed = Now.AddHours(-26);
        var source = Source(UpdateCadence.Daily());

        Assert.Equal(
            FreshnessState.Overdue,
            FreshnessPolicy.Assess(source, lastRefreshed, Now, TimeSpan.Zero).State);

        Assert.Equal(
            FreshnessState.Current,
            FreshnessPolicy.Assess(source, lastRefreshed, Now, TimeSpan.FromDays(7)).State);
    }

    [Fact]
    public void The_expectation_comes_from_the_source_not_from_a_fixed_duration()
    {
        // Three weeks old. Worthless for a daily source, perfectly current for a quarterly one.
        // Staleness has no meaning without the expectation it is measured against.
        var threeWeeksAgo = Now.AddDays(-21);

        Assert.Equal(
            FreshnessState.Overdue,
            FreshnessPolicy.Assess(Source(UpdateCadence.Daily()), threeWeeksAgo, Now).State);

        Assert.Equal(
            FreshnessState.Current,
            FreshnessPolicy.Assess(Source(UpdateCadence.Quarterly()), threeWeeksAgo, Now).State);
    }

    [Fact]
    public void A_run_timestamped_in_the_future_is_current_rather_than_overdue()
    {
        // Clock skew between machines. Not a staleness problem, and reporting it as one would send
        // an operator looking for a provider outage that never happened.
        var assessment = FreshnessPolicy.Assess(
            Source(UpdateCadence.Daily()),
            Now.AddHours(1),
            Now);

        Assert.Equal(FreshnessState.Current, assessment.State);
    }

    [Fact]
    public void Elapsed_is_measured_from_the_last_refresh()
    {
        var assessment = FreshnessPolicy.Assess(
            Source(UpdateCadence.Daily()),
            Now.AddHours(-30),
            Now);

        Assert.Equal(TimeSpan.FromHours(30), assessment.Elapsed);
        Assert.Equal(Now.AddHours(-30), assessment.LastRefreshedAtUtc);
    }

    // ---------- refusals and direction of failure ----------

    [Fact]
    public void A_null_source_is_refused() =>
        Assert.Throws<ArgumentNullException>(() => FreshnessPolicy.Assess(null!, Now, Now));

    [Theory]
    [InlineData(CadenceKind.Daily)]
    [InlineData(CadenceKind.Weekly)]
    [InlineData(CadenceKind.Quarterly)]
    public void Every_interval_bearing_cadence_can_be_assessed(CadenceKind kind)
    {
        var cadence = UpdateCadence.Every(kind, TimeSpan.FromDays(1));

        var assessment = FreshnessPolicy.Assess(Source(cadence), Now.AddDays(-30), Now);

        Assert.Equal(FreshnessState.Overdue, assessment.State);
    }

    [Fact]
    public void The_default_state_is_never_a_conclusion() =>

        // Unknown exists so an unset value does not read as reassurance. No rule may produce it.
        Assert.NotEqual(
            FreshnessState.Unknown,
            FreshnessPolicy.Assess(Source(UpdateCadence.Daily()), Now, Now).State);
}
