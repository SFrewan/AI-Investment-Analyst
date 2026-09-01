using System.Globalization;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.Opportunities;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Operations;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Opportunities.Equity;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Operations;

/// <summary>
/// The work one operating cycle does when it reviews an instrument's prices.
/// </summary>
/// <remarks>
/// <para>
/// The first registered <see cref="ICycleWorkPlan"/>. Phase 6 built the loop and deliberately
/// shipped no plan for it to run, so every cycle escalated and suspended with "no work plan is
/// registered". This is the smallest plan that produces something the validation run can measure: it
/// reads a price series, asks the registered discoverers what they see in it, evaluates and ranks
/// what they found, and proposes recording it.
/// </para>
/// <para>
/// <strong>What it proposes is a record, not a trade.</strong> The action is
/// <see cref="Capability.OpportunityManagement"/> with no financial effect: the platform is writing
/// down that it found something and what it expects, so that the outcome can be measured against
/// the expectation later. It does not place an order, does not touch a venue, does not size a
/// position and does not reach the execution plane. The observation window is measurement, and this
/// is the thing being measured.
/// </para>
/// <para>
/// <strong>Nothing is written outside the seam.</strong> Every stage before the gate works on an
/// in-memory aggregate. The opportunity reaches the repository only inside
/// <see cref="ExecuteAsync"/>, which the gateway invokes after the policy engine returned Execute
/// and inside the authorisation window - so a denied or unapproved candidate leaves no row, and the
/// cycle's own progress saves in between cannot drag a domain write along with them.
/// </para>
/// <para>
/// <strong>Every read is pinned to one instant.</strong> The stages run over seconds and the clock
/// moves between them; a restatement published in that gap would let the screen and the score
/// disagree about what the series was. The instant the cycle's first stage ran is recorded and used
/// for every read afterwards, which makes the whole pass replayable from the same evidence.
/// </para>
/// <para>
/// <strong>It is not a strategy engine.</strong> It owns sequencing and the safety-relevant
/// plumbing; what counts as a candidate belongs to <see cref="IOpportunityDiscoverer"/>, what counts
/// as sufficient evidence to <see cref="IEvidenceRequirement"/>, and what the economics are to
/// <see cref="IOpportunityEconomicsCalculator"/>. A second opportunity type is three registrations
/// and no change here.
/// </para>
/// </remarks>
public sealed class EquityReviewWorkPlan : ICycleWorkPlan
{
    /// <summary>The template name a watch names to run this plan.</summary>
    public const string Template = "equity-price-review";

    public const string ServiceId = "application.operations.equity-price-review";
    public const string ServiceVersion = "1.0";

    /// <summary>The action type recorded on every candidate this plan writes down.</summary>
    public static ActionType RecordCandidate { get; } = ActionType.Create("opportunity.record-candidate");

    private static readonly ProposedBy Proposer = ProposedBy.Service(ServiceId, ServiceVersion);

    /// <summary>
    /// What the risk assessment says can go wrong, stated once rather than rebuilt per candidate.
    /// </summary>
    /// <remarks>
    /// The same three every time, because they are properties of the rule rather than of the
    /// instrument: a rate counted over few occurrences, a target that is a past price rather than a
    /// promise, and a series inheriting its vendor's corrections. A candidate-specific factor would
    /// have to be measured, and this rule measures three things.
    /// </remarks>
    private static readonly string[] RiskFactors =
    [
        "The base rate is measured over a single series and a small number of occurrences.",
        "The target is a price the instrument previously traded at, which is not a commitment by " +
        "anyone to trade there again.",
        "The series is supplied by the operator's vendor and inherits that vendor's corrections and gaps.",
    ];

    private readonly ICycleStore _cycles;
    private readonly IWatchStore _watches;
    private readonly IEnumerable<IOpportunityDiscoverer> _discoverers;
    private readonly IEnumerable<IEvidenceRequirement> _requirements;
    private readonly IEnumerable<IOpportunityEconomicsCalculator> _calculators;
    private readonly PriceSeriesReader _prices;
    private readonly IDataAcquisition _acquisition;
    private readonly DiscoverySettings _settings;
    private readonly IOpportunityRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    private Guid _cycleId;
    private DateTime _asAtUtc;
    private IngestionSubject? _subject;
    private IReadOnlyList<PricedObservation> _series = [];

    /// <summary>
    /// The window the screen actually read, without the extra session read beside it.
    /// </summary>
    /// <remarks>
    /// <see cref="_series"/> carries one session more than the screen looks at, so the rule can tell
    /// a new drawdown from one already open. That extra close is not part of the conclusion, so it
    /// is not part of the evidence the opportunity cites and it is not the session the score is
    /// stamped at. Evidence that includes a price the conclusion did not rest on is evidence nobody
    /// can check.
    /// </remarks>
    private IReadOnlyList<PricedObservation> Screened =>
        _series.Count <= _settings.MaxSessions
            ? _series
            : _series.Skip(_series.Count - _settings.MaxSessions).ToList();
    private PriceRecoveryVerdict? _verdict;
    private Opportunity? _candidate;
    private OpportunityEconomics? _economics;
    private string _obstacle = string.Empty;

    public EquityReviewWorkPlan(
        ICycleStore cycles,
        IWatchStore watches,
        IEnumerable<IOpportunityDiscoverer> discoverers,
        IEnumerable<IEvidenceRequirement> requirements,
        IEnumerable<IOpportunityEconomicsCalculator> calculators,
        PriceSeriesReader prices,
        IDataAcquisition acquisition,
        DiscoverySettings settings,
        IOpportunityRepository repository,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _cycles = cycles ?? throw new ArgumentNullException(nameof(cycles));
        _watches = watches ?? throw new ArgumentNullException(nameof(watches));
        _discoverers = discoverers ?? throw new ArgumentNullException(nameof(discoverers));
        _requirements = requirements ?? throw new ArgumentNullException(nameof(requirements));
        _calculators = calculators ?? throw new ArgumentNullException(nameof(calculators));
        _prices = prices ?? throw new ArgumentNullException(nameof(prices));
        _acquisition = acquisition ?? throw new ArgumentNullException(nameof(acquisition));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public string TemplateName => Template;

    /// <summary>Why this pass produced nothing, when it produced nothing.</summary>
    /// <remarks>
    /// Kept so a caller and a test can tell the several different reasons apart. "The series is too
    /// short" says the data plane is still filling; "the condition has never occurred often enough"
    /// says it is full and the screen is unconvinced. A plan that reported both as silence would
    /// leave an operator unable to tell whether anything was working.
    /// </remarks>
    public string Obstacle => _obstacle;

    /// <inheritdoc />
    public async Task<CycleStageResult> RunStageAsync(
        CycleStageContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.CycleId != _cycleId)
        {
            Reset(context);
        }

        return context.Stage switch
        {
            CycleStage.Discover => await DiscoverAsync(context, cancellationToken).ConfigureAwait(false),
            CycleStage.Collect => await CollectAsync(context, cancellationToken).ConfigureAwait(false),
            CycleStage.Validate => Screen(),
            CycleStage.Analyze => await AskDiscoverersAsync(cancellationToken).ConfigureAwait(false),
            CycleStage.Calculate => ComputeEconomics(),
            CycleStage.AssessRisk => Evaluate(),
            CycleStage.Rank => Rank(),
            CycleStage.ProposeAction => Propose(context),

            // Identify, ExecuteOrEscalate, Monitor, MeasureOutcome and Record. There is at most one
            // candidate per pass, nothing is executed at L3, and measuring the outcome is the
            // validation run's job over the whole window rather than this cycle's over one pass.
            _ => Nothing(),
        };
    }

    /// <summary>
    /// Writes the candidate down. Invoked by the gateway, inside the authorisation window, only
    /// after the policy engine returned Execute.
    /// </summary>
    /// <remarks>
    /// The only method in this class that persists anything, and the only one that moves the
    /// opportunity out of <see cref="OpportunityStatus.Ranked"/>. A plan that wrote during its
    /// stages would have recorded candidates the policy engine went on to refuse.
    /// </remarks>
    public async Task<string> ExecuteAsync(
        ActionProposal proposal,
        AutonomyResolution autonomy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var candidate = _candidate;

        if (candidate is null)
        {
            throw new InvalidOperationException(
                "The gateway authorised recording a candidate and this plan is holding none. The " +
                "proposal and the effect have come apart, and writing something else would be worse " +
                "than failing here.");
        }

        candidate.RecordProposal(proposal.ProposalId, _clock.UtcNow);

        await _repository.AddAsync(candidate, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Recorded opportunity {candidate.OpportunityId} for {candidate.Subject}, citing " +
            $"{candidate.Evidence.Count} observations.");
    }

    private void Reset(CycleStageContext context)
    {
        _cycleId = context.CycleId;
        _asAtUtc = context.NowUtc;
        _subject = null;
        _series = [];
        _verdict = null;
        _candidate = null;
        _economics = null;
        _obstacle = string.Empty;
    }

    /// <summary>Learns what this cycle is about, from the watch that started it.</summary>
    /// <remarks>
    /// A cycle carries a capability, a template and the watch that fired it; the instrument lives on
    /// the watch's target. Reading it from there rather than from configuration is what lets one
    /// registration of this plan serve every instrument an operator watches.
    /// </remarks>
    private async Task<CycleStageResult> DiscoverAsync(
        CycleStageContext context,
        CancellationToken cancellationToken)
    {
        var cycle = await _cycles.FindAsync(context.CycleId, cancellationToken).ConfigureAwait(false);

        if (cycle?.WatchId is not { } watchId)
        {
            return Blocked(
                "this cycle was not started by a watch, so there is no instrument to review. A " +
                "template that reviews prices has to be told which prices.");
        }

        var watch = await _watches.FindAsync(watchId, cancellationToken).ConfigureAwait(false);

        if (watch?.Target.Identifier is not { } identifier)
        {
            return Blocked(
                "the watch behind this cycle names no specific instrument. A sector-wide target " +
                "cannot be ordered, position-sized or reconciled against a fill.");
        }

        _subject = IngestionSubject.Create(watch.Target.Kind, identifier);

        return Nothing();
    }

    /// <summary>
    /// Acquires the instrument's prices, then reads the series the screen will run over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Fetching belongs here, inside the cycle.</strong> A cycle is a leased, persisted
    /// state machine with a budget that already counts provider calls, so a fetch that fails, is
    /// refused or exhausts a rate limit is retried, escalated and accounted for by machinery that
    /// exists rather than by a timer beside it. It is also the only place that knows which
    /// instrument is being reviewed: the subject came from the watch two stages ago.
    /// </para>
    /// <para>
    /// <strong>It grants nothing.</strong> The acquisition goes through the ingestion gateway
    /// unchanged, and every gate still refuses in its own name - the source must be registered, its
    /// recorded licensing must admit the category and region, a connector must exist for it, that
    /// connector must be capable of the request, and the rate limiter must allow it. A refusal is
    /// written to the run ledger with the rule that produced it.
    /// </para>
    /// <para>
    /// <strong>A fetch that did not happen fails the stage rather than falling through to what is
    /// stored.</strong> Screening yesterday's closes because today's fetch was refused is the
    /// quietest way to act on stale evidence, and it would look identical to a successful pass.
    /// </para>
    /// </remarks>
    private async Task<CycleStageResult> CollectAsync(
        CycleStageContext context,
        CancellationToken cancellationToken)
    {
        var subject = _subject;

        if (subject is null)
        {
            return Nothing();
        }

        var acquisition = await AcquireAsync(subject, context, cancellationToken).ConfigureAwait(false);

        if (acquisition is not null && !acquisition.WasFetched)
        {
            // The run is already in the ledger with its refusal rule or failure reason; this states
            // the obstacle for the cycle and leaves the series empty, so nothing is screened.
            _obstacle = string.Create(
                CultureInfo.InvariantCulture,
                $"market data for {subject} could not be acquired from " +
                $"'{_settings.PriceSourceId}': {acquisition.Run.Reason ?? acquisition.Run.Outcome.ToString()}");

            return new CycleStageResult
            {
                ModelSpend = Money.Zero(Currency.Create(_settings.CurrencyCode)),
                ProviderCalls = 1,
                ProviderFailed = true,
            };
        }

        // Split-adjusted, and a refusal is a real outcome rather than an empty list. The stored
        // close is the raw one, so a split leaves a step the screen would read as a spectacular
        // fall and score with complete confidence - the only place in this platform that produces
        // a confident wrong number instead of a refusal.
        var adjusted = await _prices
            .ReadAdjustedAsync(
                subject,
                _settings.PriceAttribute,
                _settings.SplitAttribute,

                // One more than the screen reads. The extra session is not screened; it is what
                // lets the rule tell a drawdown that has just begun from one already open.
                _settings.MaxSessions + 1,
                _asAtUtc,
                _settings.MaxUnexplainedMove,
                cancellationToken)
            .ConfigureAwait(false);

        if (!adjusted.IsUsable)
        {
            _obstacle = string.Create(
                CultureInfo.InvariantCulture,
                $"the price series for {subject} was not screened: {adjusted.Explanation}");

            // Not a provider failure - the fetch worked and the data is there. The pass simply
            // declines to draw a conclusion from a series it cannot restate, and says so.
            return new CycleStageResult
            {
                ModelSpend = Money.Zero(Currency.Create(_settings.CurrencyCode)),
                ProviderCalls = acquisition is null ? 0 : 1,
                EvidenceUntrustworthy = true,
            };
        }

        _series = adjusted.Observations;

        if (acquisition is null)
        {
            return Nothing();
        }

        return new CycleStageResult
        {
            ModelSpend = Money.Zero(Currency.Create(_settings.CurrencyCode)),
            ProviderCalls = 1,
        };
    }

    /// <summary>
    /// One acquisition for the subject under review, or null when no source is configured.
    /// </summary>
    /// <remarks>
    /// No window is stated. The screen counts a base rate over a hundred and twenty sessions and the
    /// rule needs sixty of history behind it, so the request asks for what the connector supplies
    /// and the reader takes the most recent sessions that were public at the cycle's pinned instant.
    /// A window narrowed to "since yesterday" would produce a series too short to screen and a
    /// candidate that could never be evidenced.
    /// </remarks>
    private async Task<AcquisitionResult?> AcquireAsync(
        IngestionSubject subject,
        CycleStageContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.PriceSourceId))
        {
            return null;
        }

        var request = IngestionRequest.Create(
            SourceId.Create(_settings.PriceSourceId),
            DataCategory.MarketPrices,
            Region.Global,
            subject,

            // The cycle's own correlation, so the run, its archived payload and every observation
            // it produced trace back to the pass that asked for them.
            CorrelationFor(context),

            // When the fetch is actually being made. The reads stay pinned to _asAtUtc; stamping a
            // request with an instant that has passed would misdate the run in the ledger.
            _clock.UtcNow);

        return await _acquisition.AcquireAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the screen to the series this pass read, so the score can be built from the same
    /// evidence the discoverers saw.
    /// </summary>
    private CycleStageResult Screen()
    {
        if (_subject is null)
        {
            return Nothing();
        }

        _verdict = PriceRecoveryRule.EvaluateEpisode(
            _series.Select(price => price.ToClosingPrice()).ToList(),
            _settings.Rule,
            _settings.MaxSessions);

        if (!_verdict.HasCandidate)
        {
            _obstacle = PriceRecoveryRule.Explain(_verdict.Refusal);
        }

        // Untrustworthy evidence is a different thing from evidence that does not support a
        // candidate, and only the first should reach the escalation policy. A series that is simply
        // short is the ordinary state of a platform that started collecting last week.
        return Nothing();
    }

    private async Task<CycleStageResult> AskDiscoverersAsync(CancellationToken cancellationToken)
    {
        var subject = _subject;

        if (subject is null || _verdict is null || !_verdict.HasCandidate)
        {
            return Nothing();
        }

        foreach (var discoverer in _discoverers)
        {
            if (discoverer.Type != EquityOpportunity.Type)
            {
                continue;
            }

            var found = await discoverer
                .DiscoverAsync(subject, _asAtUtc, cancellationToken)
                .ConfigureAwait(false);

            if (found.Count > 0)
            {
                _candidate = found[0];

                return Nothing();
            }
        }

        _obstacle = "no registered discoverer produced a candidate from this series.";

        return Nothing();
    }

    private CycleStageResult ComputeEconomics()
    {
        var candidate = _candidate;

        if (candidate is null)
        {
            return Nothing();
        }

        var type = candidate.Type;
        var requirement = _requirements.FirstOrDefault(r => r.Type == type);

        if (requirement is null)
        {
            _candidate = null;

            return Blocked(
                $"no evidence requirement is registered for opportunity type '{type}'. An " +
                "unregistered type would be the least checked rather than the most.");
        }

        var missing = requirement.MissingRequirements(candidate);

        if (missing.Count > 0)
        {
            _candidate = null;

            return Blocked("the candidate is short of: " + string.Join(" ", missing));
        }

        var calculator = _calculators.FirstOrDefault(c => c.Type == type);

        if (calculator is null)
        {
            _candidate = null;

            return Blocked(
                $"no economics calculator is registered for opportunity type '{type}'. " +
                "Profit is calculated here rather than stated, and there is nothing to calculate it.");
        }

        _economics = calculator.Calculate(candidate, _asAtUtc);

        return Nothing();
    }

    private CycleStageResult Evaluate()
    {
        var candidate = _candidate;
        var economics = _economics;

        if (candidate is null || economics is null || _verdict?.Candidate is not { } screened)
        {
            return Nothing();
        }

        candidate.Evaluate(
            economics,
            OpportunityRisk.Create(
                "A position in a listed equity taken because the price fell a stated distance below " +
                "its own recent high. It can be closed, and closing it pays a spread and a " +
                "commission. The base rate behind it is counted over one instrument's own history " +
                "and is not evidence about any other instrument or any other period.",
                Reversibility,
                candidate.Evidence,
                RiskFactors),
            screened.Confidence,
            _asAtUtc);

        return new CycleStageResult
        {
            ModelSpend = Money.Zero(Currency.Create(_settings.CurrencyCode)),
            Confidence = screened.Confidence,
        };
    }

    /// <summary>
    /// Records the measured base rate as the score the candidate is ranked by.
    /// </summary>
    /// <remarks>
    /// The score is a measurement rather than a number: it names the metric, the version of the
    /// arithmetic that produced it, and every close it was counted over. Ranking on a loose decimal
    /// would make this week's ordering incomparable with last week's for reasons invisible in the
    /// data, which is exactly what <see cref="OpportunityScore"/> refuses to allow.
    /// </remarks>
    private CycleStageResult Rank()
    {
        var candidate = _candidate;
        var subject = _subject;

        if (candidate is null || subject is null || _verdict?.Candidate is not { } screened)
        {
            return Nothing();
        }

        var context = CalculationContext.Create(subject, KnowledgeCutoff.At(_asAtUtc), _asAtUtc);

        candidate.Rank(
            OpportunityScore.From(
                MetricResult.Create(
                    context,
                    PriceRecoveryRule.Metric,
                    MetricValue.Ratio(screened.SuccessProbability),
                    "successes / trials, counted over the cited closes",
                    candidate.Source.DiscovererId,
                    PriceRecoveryRule.Version,
                    Screened[^1].SessionCloseUtc,
                    Inputs())),
            _asAtUtc);

        return Nothing();
    }

    /// <summary>Every close the rate was counted over, named by its position in the series.</summary>
    private List<CalculationInput> Inputs()
    {
        var screened = Screened;
        var inputs = new List<CalculationInput>(screened.Count);

        for (var i = 0; i < screened.Count; i++)
        {
            var price = screened[i];

            inputs.Add(CalculationInput.Create(
                string.Create(CultureInfo.InvariantCulture, $"close-{i + 1:D4}"),
                Claims.Fact(price.Close, price.Provenance),
                UnitOfMeasure.Money));
        }

        return inputs;
    }

    private CycleStageResult Propose(CycleStageContext context)
    {
        var candidate = _candidate;

        if (candidate is null || candidate.Status != OpportunityStatus.Ranked)
        {
            return Nothing();
        }

        var proposal = ActionProposal.Create(
            CorrelationFor(context),
            Capability.OpportunityManagement,
            RecordCandidate,
            ActionTarget.Create(candidate.Subject.Kind, candidate.Subject.Identifier),
            new OpportunityCandidateParameters(
                candidate.Type,
                candidate.Source.DiscovererId.Value,
                candidate.Subject.Identifier ?? candidate.Subject.Kind,
                candidate.Evidence.Count),

            // Writing down what the platform noticed spends nothing and risks nothing. The
            // opportunity's own economics describe what acting on it would cost; nobody is acting.
            ActionEconomics.NoFinancialEffect(Currency.Create(_settings.CurrencyCode)),
            Proposer,

            // Keyed on the cycle: re-running a crashed cycle must not record the candidate twice.
            string.Create(CultureInfo.InvariantCulture, $"opportunity.record-candidate:{context.CycleId}"),
            _clock.UtcNow,
            cycleId: context.CycleId,
            evidence: candidate.Evidence,
            confidence: candidate.Confidence);

        return new CycleStageResult
        {
            ModelSpend = Money.Zero(Currency.Create(_settings.CurrencyCode)),
            Proposal = proposal,
            Confidence = candidate.Confidence,
        };
    }

    /// <summary>
    /// Reversibility of recording a candidate.
    /// </summary>
    /// <remarks>
    /// <see cref="ReversibilityClass.ReversibleWithCost"/> rather than <c>Reversible</c>, matching
    /// what a simulated order is recorded as. The record is of a position that could be taken, and
    /// stating it as freely reversible would understate the tier of everything downstream that reads
    /// the risk assessment.
    /// </remarks>
    private const ReversibilityClass Reversibility = ReversibilityClass.ReversibleWithCost;

    private static CorrelationId CorrelationFor(CycleStageContext context) =>
        CorrelationId.Create(
            string.Create(CultureInfo.InvariantCulture, $"cycle-{context.CycleId:N}"));

    private CycleStageResult Nothing() =>
        CycleStageResult.Nothing(Currency.Create(_settings.CurrencyCode));

    /// <summary>
    /// A stage that could not do its work because something the installation was supposed to supply
    /// is missing.
    /// </summary>
    /// <remarks>
    /// Reported as a failed step rather than as silence. A watch pointing at nothing and a series
    /// that simply has not fallen both produce no proposal, and only one of them is a configuration
    /// mistake somebody needs to hear about.
    /// </remarks>
    private CycleStageResult Blocked(string obstacle)
    {
        _obstacle = obstacle;

        return new CycleStageResult
        {
            ModelSpend = Money.Zero(Currency.Create(_settings.CurrencyCode)),
            ProviderFailed = true,
        };
    }
}
