using System.Globalization;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Validation;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Validation;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Persistence;
using AI.Investment.Infrastructure.Persistence.Repositories;
using AI.Investment.Infrastructure.Time;
using AI.Investment.Infrastructure.Validation;
using Xunit;

namespace AI.Investment.Integration.Tests.Validation;

/// <summary>
/// The point-in-time read side against a real PostgreSQL, and the report it produces.
/// </summary>
/// <remarks>
/// <para>
/// The claims worth establishing here cannot be established anywhere else. Whether a query narrows on
/// publication time is a property of the SQL it generates; whether a restatement resolves to the value
/// that was current at a past decision is a property of how several rows describing the same instant
/// are ordered and grouped in the database. Both look correct in any in-memory double, because a
/// double is written by the same person who wrote the query and shares its assumptions.
/// </para>
/// <para>
/// The last test writes the report the phase's exit criterion asks for. It writes it from whatever
/// the repository actually holds, which is the only way a report is worth reading.
/// </para>
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class ValidationPersistenceTests : IAsyncLifetime
{
    private const string PriceAttribute = "security.close";

    private static readonly DateTime WindowStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime WindowEnd = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A fresh subject each time it is asked for, not a shared instance.
    /// </summary>
    /// <remarks>
    /// An owned entity belongs to exactly one owner. Handing the same <see cref="IngestionSubject"/>
    /// instance to several observations makes the change tracker attribute it to one of them and
    /// leave the rest with nothing, which arrives as a not-null violation on <c>subject_kind</c>
    /// rather than as anything that names the cause.
    /// </remarks>
    private static IngestionSubject Apple() => IngestionSubject.Create("Security", "AAPL");

    private readonly PostgresFixture _fixture;

    public ValidationPersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The single admission test, in SQL: published at or before the cutoff, and nothing else.
    /// </summary>
    [SkippableFact]
    public async Task Only_observations_public_at_the_cutoff_are_returned()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var decision = WindowStart.AddDays(30);

        await SeedAsync(
            Price(Apple(), decision.AddDays(-1), 100m, publishedAtUtc: decision.AddDays(-1)),
            Price(Apple(), decision.AddDays(-1), 999m, publishedAtUtc: decision.AddDays(10)));

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());
        var history = new EfValidationHistory(context);

        var atDecision = await history.GetAdmissibleAsync(
            Apple(), PriceAttribute, KnowledgeCutoff.At(decision));

        var later = await history.GetAdmissibleAsync(
            Apple(), PriceAttribute, KnowledgeCutoff.At(decision.AddDays(30)));

        Assert.Single(atDecision);
        Assert.Equal(2, later.Count);
    }

    /// <summary>
    /// Bitemporal replay. A figure corrected afterwards must not reach the decision that came first,
    /// and the correction must be the one a later reader sees.
    /// </summary>
    [SkippableFact]
    public async Task A_restated_value_resolves_to_what_was_known_at_the_time()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var instant = WindowStart.AddDays(30);

        await SeedAsync(
            Price(Apple(), instant, 100m, publishedAtUtc: instant),
            Price(Apple(), instant, 200m, publishedAtUtc: instant.AddDays(40)));

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());
        var history = new EfValidationHistory(context);

        var then = await history.GetPriceAsOfAsync(
            Apple(), PriceAttribute, instant, KnowledgeCutoff.At(instant));

        var now = await history.GetPriceAsOfAsync(
            Apple(), PriceAttribute, instant, KnowledgeCutoff.At(instant.AddDays(90)));

        Assert.NotNull(then);
        Assert.Equal(100m, then!.Price);

        Assert.NotNull(now);
        Assert.Equal(200m, now!.Price);
    }

    /// <summary>
    /// A series returns one point per instant - the version current at the cutoff - so a restatement
    /// cannot silently double the length of the series it appears in.
    /// </summary>
    [SkippableFact]
    public async Task A_price_series_returns_one_point_per_instant_in_order()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await SeedAsync(
            Price(Apple(), WindowStart.AddDays(2), 102m, WindowStart.AddDays(2)),
            Price(Apple(), WindowStart.AddDays(1), 101m, WindowStart.AddDays(1)),
            Price(Apple(), WindowStart.AddDays(1), 111m, WindowStart.AddDays(5)),
            Price(Apple(), WindowStart.AddDays(3), 103m, WindowStart.AddDays(3)));

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());
        var history = new EfValidationHistory(context);

        var series = await history.GetPriceSeriesAsync(
            Apple(), PriceAttribute, WindowStart, WindowEnd, KnowledgeCutoff.At(WindowStart.AddDays(90)));

        Assert.Equal(3, series.Count);
        Assert.Equal(series.OrderBy(point => point.AtUtc).Select(point => point.AtUtc), series.Select(point => point.AtUtc));

        // The restated value is the one that survives, because the reader is later than both.
        Assert.Equal(111m, series[0].Price);
    }

    /// <summary>
    /// A stored value that is not a number is omitted and counted rather than coerced. A zero price
    /// is not a cheap asset; it is missing data that would dominate any return computed from it.
    /// </summary>
    [SkippableFact]
    public async Task Values_that_are_not_numbers_are_omitted_and_counted()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await SeedAsync(
            Price(Apple(), WindowStart.AddDays(1), 101m, WindowStart.AddDays(1)),
            Observation.RecordFact(
                Apple(),
                PriceAttribute,
                ObservationValue.Text("unavailable"),
                Provenance.Create("test-feed", WindowStart.AddDays(2), WindowStart.AddDays(2), WindowStart.AddDays(2))));

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());
        var history = new EfValidationHistory(context);

        var series = await history.GetPriceSeriesAsync(
            Apple(), PriceAttribute, WindowStart, WindowEnd, KnowledgeCutoff.At(WindowEnd));

        Assert.Single(series);
        Assert.Equal(1, await history.CountUnreadableAsync(Apple(), PriceAttribute));
    }

    /// <summary>
    /// The report the phase's exit criterion asks for, generated from what the repository holds.
    /// </summary>
    /// <remarks>
    /// It is written to <c>artifacts/verify</c> - which .gitignore excludes - so that the committed
    /// copy under <c>docs/Reports</c> is one that was produced by this test against a real database
    /// rather than composed by hand. A performance report nobody can reproduce is an assertion.
    /// </remarks>
    [SkippableFact]
    public async Task The_validation_report_is_generated_from_the_repository_and_written_out()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        var service = new ValidationService(
            new EfValidationHistory(context),
            new EfPredictionCatalogue(context),
            new EfShadowDecisionStore(context),
            new SystemClock());

        var report = await service.RunAsync(new ValidationRequest(
            EvaluationWindow.Create(WindowStart, WindowEnd, TimeSpan.FromDays(30), TimeSpan.FromDays(1)),
            Percentage.Zero,
            CalculationVersion.Create(1, 0),
            BenchmarkDefinition.Create(
                "index buy-and-hold",
                IngestionSubject.Create("Security", "SPY"),
                PriceAttribute,
                BenchmarkRule.BuyAndHold,
                Money.Create(100_000m, Currency.Usd),
                Percentage.FromRatio(0.001m),
                new DateTime(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc)),
            PriceAttribute));

        var markdown = ValidationReportWriter.ToMarkdown(report);

        // An empty repository is the honest starting position, and the report must say so rather
        // than printing zeroes that look like measurements.
        Assert.Equal(0, report.PredictionsConsidered);
        Assert.Equal(ValidationVerdict.NotEstablished, report.Verdict);
        Assert.Contains("untested hypothesis", report.Conclusion, StringComparison.Ordinal);
        Assert.Contains("## 8. Conclusion", markdown, StringComparison.Ordinal);

        await WriteAsync(markdown);
    }

    private static Observation Price(
        IngestionSubject subject,
        DateTime asOfUtc,
        decimal price,
        DateTime publishedAtUtc) =>
        Observation.RecordFact(
            subject,
            PriceAttribute,
            ObservationValue.Number(price),
            Provenance.Create("test-feed", asOfUtc, publishedAtUtc, publishedAtUtc));

    /// <summary>
    /// Writes observations through the seam, because the guard requires it of anything the platform
    /// believes - which an observation is. The setup uses the same door as production rather than a
    /// back one.
    /// </summary>
    private async Task SeedAsync(params Observation[] observations)
    {
        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(WindowStart)))
        {
            await context.Observations.AddRangeAsync(observations);
            await context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Writes the generated report where the verification scripts already put their output.
    /// </summary>
    /// <remarks>
    /// The test walks up from its own binaries to find the repository root rather than taking a
    /// configured path, because a path in configuration is one more thing that can be pointed
    /// somewhere convenient. If it cannot find the root it does nothing: the assertions above are
    /// the test, and the file is the deliverable.
    /// </remarks>
    private static async Task WriteAsync(string markdown)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "scripts")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            return;
        }

        var output = Path.Combine(directory.FullName, "artifacts", "verify");

        Directory.CreateDirectory(output);

        await File.WriteAllTextAsync(
            Path.Combine(output, "validation-report.md"),
            markdown,
            System.Text.Encoding.UTF8);

        await File.WriteAllTextAsync(
            Path.Combine(output, "VALIDATION-DONE.txt"),
            string.Create(CultureInfo.InvariantCulture, $"generated={DateTime.UtcNow:O}"),
            System.Text.Encoding.UTF8);
    }
}
