using AI.Investment.Domain.Ingestion;

namespace AI.Investment.Application.Abstractions;

/// <summary>
/// Records what was asked of a provider and what came back, one row per exchange. Append-only.
/// </summary>
/// <remarks>
/// <para>
/// There is no update and no delete, and there never will be: this is the ledger a future policy
/// would rely on to decide whether an empty answer was a real answer, and a ledger that can be
/// edited afterwards is not evidence.
/// </para>
/// <para>
/// <strong>It cannot change what a payload normalises to.</strong> Normalisation happens in
/// <c>NormalizationPipeline</c>, downstream and in a different unit of work; nothing here is read on
/// that path. Whether a response is Normalized, Partial or Quarantined is decided by the bytes and
/// the normaliser, exactly as before.
/// </para>
/// </remarks>
public interface IProviderExchangeStore
{
    /// <summary>
    /// Writes the exchanges for one run. Called after the run itself is durable, so a row can never
    /// reference a run that does not exist.
    /// </summary>
    Task RecordAsync(
        IReadOnlyList<ProviderExchange> exchanges,
        CancellationToken cancellationToken = default);

    /// <summary>Every exchange recorded for a run, in the order they happened.</summary>
    Task<IReadOnlyList<ProviderExchange>> ForRunAsync(
        IngestionRunId runId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every exchange that produced these bytes.
    /// </summary>
    /// <remarks>
    /// The query the content-addressed archive could never answer: 324 runs produced the two-byte
    /// <c>[]</c> payload, and this is how a future investigation asks what each of them requested.
    /// </remarks>
    Task<IReadOnlyList<ProviderExchange>> ForResponseContentHashAsync(
        string contentHash,
        CancellationToken cancellationToken = default);
}
