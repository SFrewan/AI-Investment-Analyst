using AI.Investment.Application.Freshness;
using AI.Investment.Application.UnitTests.Fakes;
using AI.Investment.Application.UnitTests.Ingestion;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Freshness;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using Xunit;

namespace AI.Investment.Application.UnitTests.Freshness;

/// <summary>
/// Which sources are behind, and why.
/// </summary>
/// <remarks>
/// The judgement lives in <see cref="FreshnessPolicy"/> and is tested there. What is asserted here
/// is the wiring, and two pieces of it carry real weight: only successful runs count as a refresh,
/// and completion rather than start is what dates one. Both are the kind of mistake that makes a
/// report confidently wrong rather than obviously broken.
/// </remarks>
public sealed class FreshnessReportTests
{
    private static readonly DateTime Now = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    private sealed record Harness(
        FreshnessReport Report,
        InMemorySourceRegistry Registry,
        RecordingRunStore Runs);

    private static Harness Build()
    {
        var registry = new InMemorySourceRegistry();
        var runs = new RecordingRunStore();

        return new Harness(
            new FreshnessReport(registry, runs, new FixedClock(Now)),
            registry,
            runs);
    }

    private static DataSource Source(
        string id,
        UpdateCadence? cadence = null,
        bool active = true)
    {
        var source = DataSource.Register(
            SourceId.Create(id),
            $"Source {id}",
            SourceType.RegulatoryAuthority,
            SourceAuthority.Primary,
            Region.UnitedStates,
            [DataCategory.CompanyProfile],
            cadence ?? UpdateCadence.Daily(),
            LicensingTerms.OpenData(),
            VerificationPolicy.Authoritative,
            Now.AddYears(-1));

        if (active)
        {
            source.Activate(Now.AddYears(-1));
        }

        return source;
    }

    /// <summary>Records a run for a source and completes it with the given outcome.</summary>
    private static async Task RunAsync(
        Harness harness,
        string sourceId,
        DateTime startedAt,
        DateTime completedAt,
        bool succeeded = true)
    {
        var request = IngestionRequest.Create(
            SourceId.Create(sourceId),
            DataCategory.CompanyProfile,
            Region.UnitedStates,
            IngestionSubject.Create("Company", "0000320193"),
            CorrelationId.New(),
            startedAt);

        var run = IngestionRun.Start(request, startedAt);

        if (succeeded)
        {
            run.MarkSucceeded(completedAt);
        }
        else
        {
            run.MarkFailed("the provider returned an error", completedAt);
        }

        await harness.Runs.RecordAsync(run);
    }

    // ---------- the wiring ----------

    [Fact]
    public async Task An_empty_registry_reports_nothing()
    {
        var lines = await Build().Report.GetAsync();

        Assert.Empty(lines);
    }

    [Fact]
    public async Task A_source_that_has_never_run_is_reported_as_never_ingested()
    {
        var harness = Build();
        harness.Registry.Add(Source("a"));

        var line = Assert.Single(await harness.Report.GetAsync());

        Assert.Equal(FreshnessState.NeverIngested, line.Assessment.State);
        Assert.True(line.NeedsRefresh);
    }

    [Fact]
    public async Task A_recent_successful_run_makes_a_source_current()
    {
        var harness = Build();
        harness.Registry.Add(Source("a"));
        await RunAsync(harness, "a", Now.AddHours(-3), Now.AddHours(-2));

        var line = Assert.Single(await harness.Report.GetAsync());

        Assert.Equal(FreshnessState.Current, line.Assessment.State);
        Assert.Equal(Now.AddHours(-2), line.Assessment.LastRefreshedAtUtc);
    }

    [Fact]
    public async Task Only_successful_runs_count_as_a_refresh()
    {
        var harness = Build();
        harness.Registry.Add(Source("a"));

        // Fetched three days ago, and failing ever since. A report that read the latest run of any
        // outcome would call this current - which is exactly the failure it exists to catch.
        await RunAsync(harness, "a", Now.AddDays(-3), Now.AddDays(-3));
        await RunAsync(harness, "a", Now.AddMinutes(-10), Now.AddMinutes(-5), succeeded: false);

        var line = Assert.Single(await harness.Report.GetAsync());

        Assert.Equal(FreshnessState.Overdue, line.Assessment.State);
    }

    [Fact]
    public async Task Freshness_is_dated_from_completion_not_from_start()
    {
        var harness = Build();
        harness.Registry.Add(Source("a"));

        // Began four days ago, finished an hour ago. The data arrived when the run finished;
        // dating it from the start would claim the platform had it three days before it did.
        await RunAsync(harness, "a", Now.AddDays(-4), Now.AddHours(-1));

        var line = Assert.Single(await harness.Report.GetAsync());

        Assert.Equal(FreshnessState.Current, line.Assessment.State);
        Assert.Equal(Now.AddHours(-1), line.Assessment.LastRefreshedAtUtc);
    }

    [Fact]
    public async Task An_inactive_source_is_reported_rather_than_hidden()
    {
        var harness = Build();
        harness.Registry.Add(Source("a", active: false));

        // A source someone deactivated and forgot is a real cause of missing data. Filtering it
        // out would make that invisible.
        var line = Assert.Single(await harness.Report.GetAsync());

        Assert.Equal(FreshnessState.NotScheduled, line.Assessment.State);
        Assert.False(line.IsActive);
        Assert.False(line.NeedsRefresh);
    }

    [Fact]
    public async Task A_line_carries_enough_to_be_read_without_a_join()
    {
        var harness = Build();
        harness.Registry.Add(Source("a", UpdateCadence.Quarterly()));

        var line = Assert.Single(await harness.Report.GetAsync());

        Assert.Equal(SourceId.Create("a"), line.SourceId);
        Assert.Equal("Source a", line.Name);
        Assert.Equal(CadenceKind.Quarterly, line.Cadence.Kind);
    }

    // ---------- ordering ----------

    [Fact]
    public async Task Sources_needing_a_refresh_come_first()
    {
        var harness = Build();
        harness.Registry.Add(Source("current-one"));
        harness.Registry.Add(Source("overdue-one"));
        await RunAsync(harness, "current-one", Now.AddHours(-2), Now.AddHours(-1));
        await RunAsync(harness, "overdue-one", Now.AddDays(-9), Now.AddDays(-8));

        var lines = await harness.Report.GetAsync();

        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].NeedsRefresh);
        Assert.False(lines[1].NeedsRefresh);
    }

    [Fact]
    public async Task The_longest_neglected_comes_first_among_those_needing_a_refresh()
    {
        var harness = Build();
        harness.Registry.Add(Source("stale-days"));
        harness.Registry.Add(Source("stale-weeks"));
        await RunAsync(harness, "stale-days", Now.AddDays(-3), Now.AddDays(-3));
        await RunAsync(harness, "stale-weeks", Now.AddDays(-40), Now.AddDays(-40));

        var lines = await harness.Report.GetAsync();

        Assert.Equal(SourceId.Create("stale-weeks"), lines[0].SourceId);
    }

    [Fact]
    public async Task A_source_that_has_never_run_outranks_one_that_is_merely_late()
    {
        var harness = Build();
        harness.Registry.Add(Source("never"));
        harness.Registry.Add(Source("late"));
        await RunAsync(harness, "late", Now.AddDays(-40), Now.AddDays(-40));

        var lines = await harness.Report.GetAsync();

        // Never having run has no elapsed time, so it sorts as the most neglected of all. That is
        // the right reading: a source that has produced nothing needs attention before one that
        // has merely fallen behind.
        Assert.Equal(SourceId.Create("never"), lines[0].SourceId);
    }

    // ---------- the single-source query ----------

    [Fact]
    public async Task One_source_can_be_asked_about_directly()
    {
        var harness = Build();
        harness.Registry.Add(Source("a"));
        await RunAsync(harness, "a", Now.AddHours(-3), Now.AddHours(-2));

        var line = await harness.Report.GetAsync(SourceId.Create("a"));

        Assert.NotNull(line);
        Assert.Equal(FreshnessState.Current, line!.Assessment.State);
    }

    [Fact]
    public async Task An_unregistered_source_reports_null_rather_than_a_guess() =>

        // Null says "not registered". A NeverIngested line would say "registered and never
        // fetched", which is a different and untrue statement.
        Assert.Null(await Build().Report.GetAsync(SourceId.Create("not-registered")));

    [Fact]
    public async Task Null_arguments_are_refused()
    {
        var harness = Build();

        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Report.GetAsync(null!));

        Assert.Throws<ArgumentNullException>(
            () => new FreshnessReport(null!, harness.Runs, new FixedClock(Now)));

        Assert.Throws<ArgumentNullException>(
            () => new FreshnessReport(harness.Registry, null!, new FixedClock(Now)));

        Assert.Throws<ArgumentNullException>(
            () => new FreshnessReport(harness.Registry, harness.Runs, null!));
    }
}
