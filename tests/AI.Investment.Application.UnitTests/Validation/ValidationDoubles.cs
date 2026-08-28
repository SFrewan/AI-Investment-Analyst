using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Shadow;
using AI.Investment.Domain.Validation;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.UnitTests.Validation;

/// <summary>
/// An in-memory history that enforces the point-in-time rule the real one enforces in SQL.
/// </summary>
/// <remarks>
/// <para>
/// The filtering is the behaviour under test, so the double implements it rather than short-circuiting
/// it. A fake that returned everything would let a service with no cutoff logic pass every test in
/// this file, which is precisely the defect these tests exist to catch.
/// </para>
/// <para>
/// It also reproduces the restatement rule: several rows may describe the same instant, and reading
/// "as of" a past decision returns the latest of them that had been published by then - not the
/// correction that came afterwards.
/// </para>
/// </remarks>
internal sealed class FakeValidationHistory : IValidationHistory
{
    private readonly List<Observation> _observations = [];

    public int UnreadableCount { get; set; }

    /// <summary>Records a price, with the two times that matter kept apart.</summary>
    public void Add(IngestionSubject subject, string attribute, DateTime asOfUtc, decimal price, DateTime publishedAtUtc) =>
        _observations.Add(Observation.RecordFact(
            subject,
            attribute,
            ObservationValue.Number(price),
            Provenance.Create("test-feed", asOfUtc, publishedAtUtc, publishedAtUtc)));

    public Task<IReadOnlyList<Observation>> GetAdmissibleAsync(
        IngestionSubject subject,
        string attribute,
        KnowledgeCutoff cutoff,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Observation>>(
            Admissible(subject, attribute, cutoff)
                .OrderBy(o => o.Provenance.AsOfUtc)
                .ThenBy(o => o.Provenance.PublishedAtUtc)
                .ToList());

    public Task<IReadOnlyList<PricePoint>> GetPriceSeriesAsync(
        IngestionSubject subject,
        string attribute,
        DateTime fromUtc,
        DateTime toUtc,
        KnowledgeCutoff cutoff,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PricePoint>>(
            Admissible(subject, attribute, cutoff)
                .Where(o => o.Provenance.AsOfUtc >= fromUtc && o.Provenance.AsOfUtc <= toUtc)
                .GroupBy(o => o.Provenance.AsOfUtc)
                .Select(group => group.OrderByDescending(o => o.Provenance.PublishedAtUtc).First())
                .OrderBy(o => o.Provenance.AsOfUtc)
                .Select(ToPoint)
                .ToList());

    public Task<PricePoint?> GetPriceAsOfAsync(
        IngestionSubject subject,
        string attribute,
        DateTime atUtc,
        KnowledgeCutoff cutoff,
        CancellationToken cancellationToken = default)
    {
        var row = Admissible(subject, attribute, cutoff)
            .Where(o => o.Provenance.AsOfUtc <= atUtc)
            .OrderByDescending(o => o.Provenance.AsOfUtc)
            .ThenByDescending(o => o.Provenance.PublishedAtUtc)
            .FirstOrDefault();

        return Task.FromResult(row is null ? null : ToPoint(row));
    }

    public Task<int> CountUnreadableAsync(
        IngestionSubject subject,
        string attribute,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(UnreadableCount);

    public Task<IReadOnlyList<string>> GetSourceIdsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(
            _observations
                .Where(o => o.Provenance.PublishedAtUtc >= fromUtc && o.Provenance.PublishedAtUtc <= toUtc)
                .Select(o => o.Provenance.SourceId.Value)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList());

    private IEnumerable<Observation> Admissible(IngestionSubject subject, string attribute, KnowledgeCutoff cutoff) =>
        _observations
            .Where(o => o.Subject == subject && string.Equals(o.Attribute, attribute, StringComparison.Ordinal))

            // Publication, never retrieval. The same single test the real store makes.
            .Where(o => cutoff.Admits(o.Provenance.PublishedAtUtc));

    private static PricePoint ToPoint(Observation observation) =>
        new(observation.Provenance.AsOfUtc, observation.Value.AsNumber(), observation.Provenance.PublishedAtUtc);
}

/// <summary>A catalogue that returns exactly what a test put in it.</summary>
internal sealed class FakePredictionCatalogue : IPredictionCatalogue
{
    private readonly List<PredictionCandidate> _candidates = [];

    public void Add(PredictionCandidate candidate) => _candidates.Add(candidate);

    public Task<IReadOnlyList<PredictionCandidate>> GetAsync(
        EvaluationWindow window,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PredictionCandidate>>(_candidates.ToList());
}

/// <summary>A shadow store that only reads. Nothing in validation writes one.</summary>
internal sealed class FakeShadowStore : IShadowDecisionStore
{
    private readonly List<ShadowDecision> _decisions = [];

    public void Seed(ShadowDecision decision) => _decisions.Add(decision);

    public Task AddAsync(ShadowDecision decision, CancellationToken cancellationToken = default)
    {
        _decisions.Add(decision);

        return Task.CompletedTask;
    }

    public Task<int> CountAsync(DateTime sinceUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(_decisions.Count(d => d.RecordedAtUtc >= sinceUtc));

    public Task<int> CountWouldHaveExecutedAsync(DateTime sinceUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(_decisions.Count(d => d.RecordedAtUtc >= sinceUtc && d.WouldHaveExecuted));

    public Task<IReadOnlyList<ShadowDecision>> GetRecentAsync(int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ShadowDecision>>(
            _decisions.OrderByDescending(d => d.RecordedAtUtc).Take(Math.Max(limit, 0)).ToList());

    public Task<IReadOnlyList<ShadowDecision>> GetBetweenAsync(
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ShadowDecision>>(
            _decisions
                .Where(d => d.RecordedAtUtc >= fromUtc && d.RecordedAtUtc <= toUtc)
                .OrderBy(d => d.RecordedAtUtc)
                .ToList());

    public Task SaveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Fixtures shared by the validation tests.</summary>
internal static class ValidationFixtures
{
    public const string PriceAttribute = "security.close";

    public static readonly DateTime WindowStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static readonly DateTime WindowEnd = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    public static readonly IngestionSubject Apple = IngestionSubject.Create("Security", "AAPL");

    public static readonly IngestionSubject Index = IngestionSubject.Create("Security", "SPY");

    public static readonly CalculationVersion Method = CalculationVersion.Create(1, 0);

    public static EvaluationWindow Window(TimeSpan? horizon = null) =>
        EvaluationWindow.Create(
            WindowStart,
            WindowEnd,
            horizon ?? TimeSpan.FromDays(30),
            TimeSpan.FromDays(1));

    public static BenchmarkDefinition Benchmark(decimal cost = 0m, DateTime? declaredAt = null) =>
        BenchmarkDefinition.Create(
            "index buy-and-hold",
            Index,
            PriceAttribute,
            BenchmarkRule.BuyAndHold,
            Money.Create(100_000m, Currency.Usd),
            Percentage.FromRatio(cost),
            declaredAt ?? WindowStart.AddDays(-1));

    public static PredictionCandidate Candidate(
        DateTime decidedAtUtc,
        DateTime? evidenceAvailableAtUtc,
        PredictionDirection direction = PredictionDirection.Positive,
        TimeSpan? horizon = null,
        decimal? probability = null,
        Guid? proposalId = null,
        Guid? predictionId = null,
        IngestionSubject? subject = null) =>
        new(
            predictionId ?? Guid.NewGuid(),
            subject ?? Apple,
            decidedAtUtc,
            decidedAtUtc.Add(horizon ?? TimeSpan.FromDays(30)),
            direction,
            Method,
            "opportunity/test",
            evidenceAvailableAtUtc,
            probability is null ? null : Percentage.FromRatio(probability.Value),
            null,
            proposalId);
}
