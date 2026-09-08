using AI.Investment.Domain.Ingestion;

namespace AI.Investment.Application.Normalization;

/// <summary>Turns what a run archived into observations.</summary>
public interface INormalizationPipeline
{
    Task<NormalizationSummary> NormalizeAsync(
        IngestionRun run,
        CancellationToken cancellationToken = default);
}

/// <summary>What normalising a run produced.</summary>
/// <param name="PayloadsRead">Payloads successfully interpreted.</param>
/// <param name="ObservationsRecorded">
/// Observations written. Zero when policy refused the write, even if payloads were read - the
/// two are separate facts and collapsing them would hide a denial.
/// </param>
/// <param name="PayloadsQuarantined">Payloads that could not be read and were recorded as such.</param>
/// <param name="RowsRejected">
/// Individual rows refused inside payloads that were otherwise read. Deliberately separate from
/// <paramref name="PayloadsQuarantined"/>: one is a document the platform could not use, the other
/// is a hole inside a document it could. Collapsing them would hide whichever is rarer.
/// </param>
public sealed record NormalizationSummary(
    int PayloadsRead,
    int ObservationsRecorded,
    int PayloadsQuarantined,
    int RowsRejected = 0)
{
    public bool HadFailures => PayloadsQuarantined > 0;

    public override string ToString() =>
        RowsRejected == 0
            ? $"read={PayloadsRead}, recorded={ObservationsRecorded}, quarantined={PayloadsQuarantined}"
            : $"read={PayloadsRead}, recorded={ObservationsRecorded}, quarantined={PayloadsQuarantined}, rows rejected={RowsRejected}";
}
