using AI.Investment.Domain.Ingestion;

namespace AI.Investment.Application.Ingestion;

/// <summary>
/// The single entry point for drawing data from the outside world.
/// </summary>
/// <remarks>
/// <para>
/// What <see cref="Actions.IActionGateway"/> is to side effects, this is to ingestion - and it is
/// built on top of it rather than beside it, so an ingestion run is an audited action like any
/// other. Nothing else should call an <see cref="IDataProvider"/> directly; a caller that does
/// bypasses admission, capability checking, rate limiting, the archive and the ledger in one go.
/// </para>
/// <para>
/// <strong>Returns rather than throws for an unsuccessful run.</strong> A scheduler ingesting
/// fifty subjects should not lose forty-nine because the third provider was down. Every ingestion
/// outcome - refused, failed, partial, successful - comes back as a completed
/// <see cref="IngestionRun"/> that has already been written to the ledger.
/// </para>
/// <para>
/// The exception is a failure of the platform's own machinery before any run begins - an audit
/// sink that cannot write, for instance. That is not an ingestion outcome and must not be dressed
/// up as one, so it propagates.
/// </para>
/// </remarks>
public interface IIngestionGateway
{
    /// <summary>
    /// Admits, fetches, archives and records. Always returns a completed run.
    /// </summary>
    Task<IngestionRun> IngestAsync(IngestionRequest request, CancellationToken cancellationToken = default);
}
