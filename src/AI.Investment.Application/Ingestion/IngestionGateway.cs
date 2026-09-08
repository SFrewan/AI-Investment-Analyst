using System.Globalization;
using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Ingestion;

/// <summary>
/// Admits, fetches, archives and records. The only path from the platform to a provider.
/// </summary>
/// <remarks>
/// <para>
/// Four gates stand in front of every fetch, in this order, and each one names the rule it
/// refused by:
/// </para>
/// <list type="number">
/// <item>The source is registered.</item>
/// <item><see cref="SourceAdmission"/> - active, covering, and licensed for what ingestion does.</item>
/// <item>A connector exists and <see cref="ProviderCapabilityCheck"/> says it can serve the request.</item>
/// <item>The provider's declared rate limit has room.</item>
/// </list>
/// <para>
/// Only then does the request reach <see cref="IActionGateway"/>, which applies the platform's
/// policy engine and audits the outcome. Ingestion is a side effect, so it goes through the same
/// seam as everything else rather than beside it - which also means the kill switch stops data
/// collection without any ingestion-specific code being written for it.
/// </para>
/// <para>
/// The order is not arbitrary. The cheapest and most consequential checks come first, and nothing
/// touches the network until all four have passed: an unlicensed fetch is a compliance problem the
/// moment the bytes arrive, not the moment they are stored.
/// </para>
/// </remarks>
public sealed class IngestionGateway : IIngestionGateway
{
    /// <summary>
    /// A run stops after this many pages and is recorded as partially successful.
    /// </summary>
    /// <remarks>
    /// A bound on a loop driven by a value the provider controls. Reported through the ledger
    /// rather than applied silently: a truncated run that claims success is indistinguishable from
    /// a complete one, which is how a gap enters a history nobody investigates.
    /// </remarks>
    public const int MaxPagesPerRun = 500;

    public const string SourceRegisteredRule = "ingestion.source-registered@1";
    public const string ProviderAvailableRule = "ingestion.provider-available@1";
    public const string WithinRateLimitRule = "ingestion.within-rate-limit@1";
    public const string PolicyPermittedRule = "ingestion.policy-permitted@1";

    /// <summary>How deep <see cref="Describe"/> walks an inner-exception chain.</summary>
    internal const int MaxDiagnosticDepth = 5;

    private static readonly ActionType IngestActionType = ActionType.Create("ingestion.fetch");
    private static readonly ProposedBy Proposer = ProposedBy.Service("ingestion-gateway", "1.0");

    private readonly ISourceRegistry _sources;
    private readonly IProviderCatalogue _providers;
    private readonly IProviderRateLimiter _rateLimiter;
    private readonly IRawResponseArchive _archive;
    private readonly IIngestionRunStore _runStore;
    private readonly IProviderExchangeStore? _exchangeStore;
    private readonly IActionGateway _actionGateway;
    private readonly IClock _clock;

    public IngestionGateway(
        ISourceRegistry sources,
        IProviderCatalogue providers,
        IProviderRateLimiter rateLimiter,
        IRawResponseArchive archive,
        IIngestionRunStore runStore,
        IActionGateway actionGateway,
        IClock clock,
        IProviderExchangeStore? exchangeStore = null)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _actionGateway = actionGateway ?? throw new ArgumentNullException(nameof(actionGateway));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        // Optional, and deliberately so. A composition that has not registered an exchange store
        // behaves exactly as this gateway did before - it records no evidence, rather than being
        // unable to acquire. Nothing downstream reads these rows, so their absence changes no
        // outcome.
        _exchangeStore = exchangeStore;
    }

    public async Task<IngestionRun> IngestAsync(
        IngestionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Gate 1. Registration. An unregistered origin cannot be assessed, so it cannot be used.
        var source = await _sources.GetByIdAsync(request.SourceId, cancellationToken).ConfigureAwait(false);

        if (source is null)
        {
            return await RefuseAsync(
                request,
                SourceRegisteredRule,
                $"Source '{request.SourceId}' is not in the registry. Nothing may be ingested from " +
                "an origin whose authority and licensing have never been assessed.",
                cancellationToken).ConfigureAwait(false);
        }

        // Gate 2. Permission.
        var admission = SourceAdmission.Evaluate(source, request.Category, request.Region);

        if (!admission.IsAdmitted)
        {
            return await RefuseAsync(
                request,
                admission.RuleId!,
                admission.Reason!,
                cancellationToken).ConfigureAwait(false);
        }

        // Gate 3. Ability.
        var provider = _providers.Find(request.SourceId);

        if (provider is null)
        {
            return await RefuseAsync(
                request,
                ProviderAvailableRule,
                $"Source '{request.SourceId}' is registered and admissible, but no connector is " +
                "registered for it, so there is nothing to fetch with.",
                cancellationToken).ConfigureAwait(false);
        }

        var capability = ProviderCapabilityCheck.Evaluate(provider.Capabilities, request);

        if (!capability.IsCapable)
        {
            return await RefuseAsync(
                request,
                capability.RuleId!,
                capability.Reason!,
                cancellationToken).ConfigureAwait(false);
        }

        // Gate 4. Rate. Complying with a declared limit, not reacting to enforcement.
        if (provider.Capabilities.Quota is { } quota)
        {
            var acquired = await _rateLimiter
                .TryAcquireAsync(request.SourceId, quota, _clock.UtcNow, cancellationToken)
                .ConfigureAwait(false);

            if (!acquired)
            {
                return await RefuseAsync(
                    request,
                    WithinRateLimitRule,
                    $"The declared quota for '{request.SourceId}' ({quota}) is spent. The run is " +
                    "recorded as refused so the scheduler can retry it rather than a thread waiting.",
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return await DispatchAsync(request, provider, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IngestionRun> DispatchAsync(
        IngestionRequest request,
        IDataProvider provider,
        CancellationToken cancellationToken)
    {
        var proposal = ActionProposal.Create(
            request.CorrelationId,
            Capability.DataIngestion,
            IngestActionType,
            ActionTarget.Create(request.Subject.Kind, request.Subject.Identifier),
            new IngestionParameters(request),
            ActionEconomics.NoFinancialEffect(),
            Proposer,
            IdempotencyKeyFor(request),
            _clock.UtcNow);

        // Captured from inside the effect so that a run which started is still recorded when the
        // effect throws. The seam rethrows a failing effect after auditing it, by design.
        IngestionRun? started = null;

        // Evidence for every exchange this run makes. Accumulated rather than written as it goes,
        // because the run itself is not durable until after the effect returns and a provenance row
        // pointing at a run nobody recorded would be evidence of nothing.
        var exchanges = new List<ProviderExchange>();

        // Set the instant the run reaches the ledger, and read by the catch filter below.
        //
        // An ingestion run has a primary key and the store inserts it. Recording one twice is not a
        // retry, it is a duplicate-key failure - and because it happened inside a catch block it
        // replaced the exception that got us there. A live pilot ended as
        // "23505 duplicate key value violates unique constraint PK_ingestion_runs" when what had
        // actually failed was the provenance write two lines earlier.
        var runRecorded = false;

        try
        {
            var outcome = await _actionGateway.DispatchAsync(
                proposal,
                async token =>
                {
                    var run = IngestionRun.Start(request, _clock.UtcNow);
                    started = run;

                    await FetchAllAsync(provider, request, run, exchanges, token).ConfigureAwait(false);

                    return run;
                },
                cancellationToken).ConfigureAwait(false);

            if (outcome.WasExecuted && outcome.Result is { } executed)
            {
                await _runStore.RecordAsync(executed, cancellationToken).ConfigureAwait(false);
                runRecorded = true;

                await RecordExchangesAsync(exchanges, cancellationToken).ConfigureAwait(false);

                return executed;
            }

            // Denied, awaiting approval, or a duplicate. The effect was never invoked, so no
            // network call was made and no run was started - but the attempt is still recorded,
            // because "policy stopped this" is exactly what an operator needs to see when data
            // does not appear.
            return await RefuseAsync(
                request,
                PolicyPermittedRule,
                $"{outcome.Status}: {outcome.Reason}",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (started is not null && !runRecorded)
        {
            // The seam has already audited the failure and rethrown. Record the run so the data
            // plane's own ledger agrees, then return it rather than propagating: a scheduler
            // ingesting fifty subjects must not lose forty-nine to one provider being down.
            //
            // `!runRecorded` in the FILTER, not an `if` in the body, and that is the whole fix.
            // A failure after the run is already durable is a different situation with a different
            // right answer: the ledger already agrees, there is nothing to record, and the only
            // thing left to do with the exception is let it out. Filtering here means such a
            // failure never enters this block at all - so it cannot insert a second run, cannot
            // re-attempt a provenance write that has just failed, and reaches the caller with its
            // own type, message and stack rather than with a duplicate-key error standing in front
            // of it.
            //
            // The one case that reaches the caller as a throw is therefore precisely the one the
            // provenance record exists for: the run happened and its evidence could not be written.
            // Reporting that as success would be the platform quietly believing it holds evidence
            // it does not hold.
            if (!started.IsComplete)
            {
                started.MarkFailed(Describe(ex), _clock.UtcNow);
            }

            await _runStore.RecordAsync(started, CancellationToken.None).ConfigureAwait(false);
            await RecordExchangesAsync(exchanges, CancellationToken.None).ConfigureAwait(false);

            return started;
        }
    }

    /// <summary>
    /// Writes the run's evidence, once the run is durable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately NOT wrapped in a catch. If the evidence cannot be written the caller is told,
    /// because the failure mode this whole record exists to prevent is a platform that quietly
    /// believes it has evidence it does not have. The run is already recorded by the time this runs,
    /// so a throw here leaves no partially-written acquisition state behind.
    /// </para>
    /// <para>
    /// It cannot change whether a payload is Normalized, Partial or Quarantined: that is decided in
    /// <c>NormalizationPipeline</c>, downstream, from the archived bytes, and nothing on that path
    /// reads these rows.
    /// </para>
    /// </remarks>
    private async Task RecordExchangesAsync(
        List<ProviderExchange> exchanges,
        CancellationToken cancellationToken)
    {
        if (_exchangeStore is null || exchanges.Count == 0)
        {
            return;
        }

        await _exchangeStore.RecordAsync(exchanges, cancellationToken).ConfigureAwait(false);
    }

    private async Task FetchAllAsync(
        IDataProvider provider,
        IngestionRequest request,
        IngestionRun run,
        List<ProviderExchange> exchanges,
        CancellationToken cancellationToken)
    {
        string? continuationToken = null;
        var pages = 0;

        do
        {
            var response = await provider
                .FetchAsync(request, continuationToken, cancellationToken)
                .ConfigureAwait(false);

            var hash = await _archive.StoreAsync(
                    request.SourceId,
                    response.Payload,
                    response.MediaType,
                    response.RetrievedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);

            run.RecordArtifact(hash);

            // One row per exchange. `pages` is the ordinal, so a paged run produces several rows
            // sharing one IngestionRunId, each with its own status, bytes and retrieval time.
            //
            // Note this does not depend on whether StoreAsync actually wrote anything: it returns
            // the same hash for bytes it already held, and an exchange that received a payload the
            // archive had seen before is still an exchange that happened.
            if (response.Provenance is { } provenance)
            {
                exchanges.Add(ProviderExchange.Record(
                    run.Id,
                    pages,
                    request.SourceId,
                    request.Fingerprint(),
                    provenance.EndpointTemplate,
                    provenance.ParametersAsJson(),
                    provenance.RequestedFromUtc,
                    provenance.RequestedToUtc,
                    provenance.HttpStatusCode,
                    provenance.HeadersAsJson(),
                    response.RetrievedAtUtc,
                    hash.Value,
                    response.Payload.Length,
                    provenance.ProviderCorrelationId,
                    _clock.UtcNow));
            }

            continuationToken = response.ContinuationToken;
            pages++;

            if (pages >= MaxPagesPerRun && continuationToken is not null)
            {
                run.MarkPartiallySucceeded(
                    $"Stopped after {MaxPagesPerRun} pages with more available. The remainder was " +
                    "not fetched; narrow the request or raise the bound deliberately.",
                    _clock.UtcNow);

                return;
            }
        }
        while (continuationToken is not null);

        run.MarkSucceeded(_clock.UtcNow);
    }

    private async Task<IngestionRun> RefuseAsync(
        IngestionRequest request,
        string ruleId,
        string reason,
        CancellationToken cancellationToken)
    {
        var run = IngestionRun.Refuse(request, ruleId, reason, _clock.UtcNow);

        await _runStore.RecordAsync(run, cancellationToken).ConfigureAwait(false);

        return run;
    }

    /// <summary>
    /// The action idempotency key for one fetch: the request, scoped to the run that asked for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The fingerprint alone is not an action identity.</strong> It hashes source,
    /// category, region, subject and window - deliberately nothing time-varying - so a recurring
    /// watch produces the same fingerprint on every firing, for ever. Using it as the idempotency
    /// key meant the first cycle claimed it and every cycle afterwards was suppressed as a
    /// duplicate: a daily price review could fetch exactly once in the platform's lifetime, and
    /// each later cycle recorded a refused run, archived nothing and raised a provider failure.
    /// </para>
    /// <para>
    /// <strong>Scoping it to the correlation restores the intent without weakening it.</strong>
    /// The correlation of a cycle-driven ingestion is derived from the cycle identity, and the
    /// cycle store's unique index on the trigger key means one observation produces one cycle.
    /// So a redelivered observation, a resumed cycle and a retried pass all carry the same
    /// correlation and still deduplicate to a single fetch - which is what the key is for - while
    /// a genuinely new observation is a new act and is allowed to happen.
    /// </para>
    /// <para>
    /// <see cref="IngestionRequest.Fingerprint"/> is untouched. It still identifies the request
    /// shape, which is what the stored <c>request_fingerprint</c> column and
    /// <c>IIngestionRunStore.HasCompletedAsync</c> are about; those ask "has this exact request
    /// ever succeeded", which is a different question from "has this act already been performed".
    /// </para>
    /// <para>
    /// Length is bounded: 64 hex characters, a colon, and a correlation of at most
    /// <see cref="CorrelationId.MaxLength"/>, giving 193 against
    /// <see cref="ActionProposal.MaxIdempotencyKeyLength"/> of 200.
    /// </para>
    /// </remarks>
    private static string IdempotencyKeyFor(IngestionRequest request) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{request.Fingerprint()}:{request.CorrelationId}");

    /// <summary>
    /// Describes a failure in terms safe to store permanently, and precise enough to act on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Type names only. Never a message, never a URL, never a response body.</strong> This
    /// text goes into an append-only ledger that cannot be redacted afterwards, and a provider's
    /// exception message is one of the likelier places for a URL with an embedded key to surface.
    /// The guarantee is structural rather than a redaction pass: nothing that can carry a
    /// credential is read at all, so there is no pattern to get wrong and nothing to keep in step
    /// with a provider's future message format.
    /// </para>
    /// <para>
    /// <strong>Why the chain, and not the outer type alone.</strong> Fifty-seven requests failed in
    /// one run and every one recorded <c>HttpRequestException during ingestion.</c> - which is
    /// equally true of a name-resolution failure, a refused connection, a TLS fault and a 429, and
    /// so distinguished none of them. The inner chain separates the largest part of that: a status
    /// the server chose arrives alone, while a connection that never opened arrives wrapped around
    /// an <c>IOException</c> and a socket failure. That is the difference between "the vendor
    /// refused us" and "we never reached the vendor", and it is the one this run needed.
    /// </para>
    /// <para>
    /// <strong>What this deliberately does not record, and where it belongs.</strong> The socket
    /// error code and the HTTP status code would separate the remaining cases exactly, and neither
    /// can carry a secret. They are absent because reading them means naming
    /// <c>System.Net.Sockets</c> and <c>System.Net.Http</c> types here, and this is the application
    /// layer: <c>DataPlaneRuleTests.Application_cannot_reach_the_network</c> refuses that, on the
    /// ground that an application service which knows about HTTP is one step from making a request
    /// outside the gateway. The rule is right and is not being weakened. Classifying a transport
    /// failure in those terms is a connector's job, and belongs in Infrastructure with the
    /// connector that produced it.
    /// </para>
    /// </remarks>
    internal static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var chain = new StringBuilder();
        var current = (Exception?)exception;
        var depth = 0;

        while (current is not null && depth < MaxDiagnosticDepth)
        {
            if (depth > 0)
            {
                chain.Append(" <- ");
            }

            chain.Append(current.GetType().Name);

            if (current is ITransportDiagnostic diagnostic)
            {
                // A closed-set token the connector built from framework enumerations. This layer
                // reads a string and still knows nothing about HTTP, which is what keeps
                // DataPlaneRuleTests satisfied while the ledger gets the fact it needs.
                chain.Append(CultureInfo.InvariantCulture, $"[{diagnostic.TransportDiagnostic}]");
            }

            current = current.InnerException;
            depth++;
        }

        if (current is not null)
        {
            // Bounded rather than truncated mid-word: a chain deeper than this has stopped saying
            // anything new, and an unbounded walk is an unbounded string in an append-only row.
            chain.Append(" <- ...");
        }

        return chain.Append(" during ingestion.").ToString();
    }
}
