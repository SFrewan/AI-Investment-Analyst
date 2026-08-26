using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Freshness;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Freshness;

/// <summary>
/// Assesses every registered source against the last time it was successfully ingested.
/// </summary>
/// <remarks>
/// <para>
/// Two collaborators and a pure policy. The registry says what was expected of each source, the run
/// ledger says when one last succeeded, and <see cref="FreshnessPolicy"/> decides what that means.
/// All the judgement lives in the policy, where it can be exercised exhaustively without a
/// database.
/// </para>
/// <para>
/// <strong>Only successful runs count.</strong> A source that has been refused fifty times in a row
/// has not been refreshed, and reading the latest run of any outcome would report it as current -
/// which is precisely the failure this report exists to catch. The distinction is why
/// <c>GetLatestSuccessfulForSourceAsync</c> exists separately from <c>GetLatestForSourceAsync</c>.
/// </para>
/// <para>
/// Inactive sources are included rather than filtered out. A source someone deactivated and forgot
/// is a real cause of missing data, and a report that silently omitted it would make that
/// invisible; the policy marks them <see cref="FreshnessState.NotScheduled"/> so they are visible
/// without being alarming.
/// </para>
/// </remarks>
public sealed class FreshnessReport : IFreshnessReport
{
    private readonly ISourceRegistry _sources;
    private readonly IIngestionRunStore _runs;
    private readonly IClock _clock;

    public FreshnessReport(ISourceRegistry sources, IIngestionRunStore runs, IClock clock)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<IReadOnlyList<SourceFreshness>> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var sources = await _sources.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var lines = new List<SourceFreshness>(sources.Count);

        foreach (var source in sources)
        {
            lines.Add(await AssessAsync(source, now, cancellationToken).ConfigureAwait(false));
        }

        // Sorted so the report reads as a queue rather than a list: what needs attention first,
        // and within that the longest-neglected first. A report nobody can scan is a report nobody
        // reads.
        return lines
            .OrderByDescending(line => line.NeedsRefresh)
            .ThenByDescending(line => line.Assessment.Elapsed ?? TimeSpan.MaxValue)
            .ThenBy(line => line.SourceId.Value, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<SourceFreshness?> GetAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceId);

        var source = await _sources.GetByIdAsync(sourceId, cancellationToken).ConfigureAwait(false);

        return source is null
            ? null
            : await AssessAsync(source, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SourceFreshness> AssessAsync(
        DataSource source,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var latest = await _runs
            .GetLatestSuccessfulForSourceAsync(source.Id, cancellationToken)
            .ConfigureAwait(false);

        // Completion, not start. A run that began inside the interval and finished outside it
        // delivered data as of when it finished, and dating freshness from the start would claim
        // the platform had data before it did.
        var lastRefreshed = latest?.CompletedAtUtc;

        return new SourceFreshness(
            source.Id,
            source.Name,
            source.Cadence,
            source.IsActive,
            FreshnessPolicy.Assess(source, lastRefreshed, nowUtc));
    }
}
