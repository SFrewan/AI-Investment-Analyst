using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Ingestion;

namespace AI.Investment.Application.Ingestion;

/// <summary>
/// Fetches from a source and turns what came back into what the platform knows.
/// </summary>
/// <remarks>
/// <para>
/// The two halves of acquiring data joined into the one operation a caller actually wants.
/// <see cref="IIngestionGateway"/> establishes that a source may be used and archives exactly what
/// it said; <see cref="INormalizationPipeline"/> decides what those bytes mean. They stay separate
/// types because they fail for different reasons and are re-run independently - a normaliser can
/// be fixed and replayed against the original bytes without touching the network - but nothing
/// outside this interface should have to remember to call both.
/// </para>
/// <para>
/// <strong>Never throws for an unsuccessful acquisition.</strong> Like the gateway it builds on: a
/// caller acquiring fifty subjects must not lose forty-nine because the third source refused. Every
/// outcome comes back described.
/// </para>
/// </remarks>
public interface IDataAcquisition
{
    Task<AcquisitionResult> AcquireAsync(
        IngestionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>What one acquisition fetched and what was made of it.</summary>
/// <remarks>
/// <see cref="Normalization"/> is null when normalisation was not attempted - the run was refused,
/// failed, or archived nothing. Null is not an empty summary: "we did not try" and "we tried and
/// found nothing" are different facts, and a zero-filled summary would report the first as the
/// second.
/// </remarks>
/// <param name="Run">The completed run, already written to the ledger.</param>
/// <param name="Normalization">What normalising it produced, or null if it was not attempted.</param>
public sealed record AcquisitionResult(IngestionRun Run, NormalizationSummary? Normalization)
{
    /// <summary>Whether the fetch itself succeeded, wholly or in part.</summary>
    public bool WasFetched =>
        Run.Outcome is IngestionOutcome.Succeeded or IngestionOutcome.PartiallySucceeded;

    /// <summary>Observations recorded. Zero when nothing was normalised or the write was refused.</summary>
    public int ObservationsRecorded => Normalization?.ObservationsRecorded ?? 0;

    public override string ToString() =>
        Normalization is null ? Run.ToString() : $"{Run} -> {Normalization}";
}
