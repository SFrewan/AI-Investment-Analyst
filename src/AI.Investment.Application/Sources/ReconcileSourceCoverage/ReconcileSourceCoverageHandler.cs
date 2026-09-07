using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Sources.ReconcileSourceCoverage;

/// <summary>
/// Teaches the registry about a category a connector has gained since it was registered.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The gap this closes.</strong> Seeding fills gaps and does not reconcile - deliberately,
/// because a registry row may have been re-licensed, deactivated or re-scored by an operator and
/// overwriting it on every start-up would quietly undo that. The consequence is that a connector
/// which gains a capability keeps it to itself: the shipped definition declares the new category,
/// the stored row does not, and every request for it is refused by
/// <c>source.supplies-category@1</c> naming a category count that is simply out of date. The
/// domain has always been able to express the change - <see cref="DataSource.UpdateCoverage"/> -
/// and until now nothing above the domain could ask for it.
/// </para>
/// <para>
/// <strong>It adds and never removes.</strong> The new coverage is the union of what is stored and
/// what the shipped definition declares, so a category an operator added by hand survives an
/// upgrade, and a category dropped from a future build is not silently revoked from a source that
/// may still be serving it. Narrowing coverage is a decision with consequences for evidence
/// already gathered; it is not something a reconciliation should do on its own initiative.
/// </para>
/// <para>
/// <strong>Nothing else about the row is touched.</strong> Licensing, activation, reliability and
/// verification policy are the operator's, and this handler has no opinion about any of them. It
/// answers exactly one question - which categories does this source supply - and leaves every
/// other answer as it found it.
/// </para>
/// <para>
/// Through the seam, like every other side effect, under
/// <see cref="Capability.ReferenceDataManagement"/>. The idempotency key carries the resulting
/// category set rather than only the source, so running it twice for one change is suppressed and
/// a genuinely different change later is not.
/// </para>
/// </remarks>
public sealed class ReconcileSourceCoverageHandler
{
    public const string ServiceId = "application.sources.reconcile-source-coverage";
    public const string ServiceVersion = "1.0";

    private static readonly ActionType ReconcileActionType =
        ActionType.Create("source.reconcile-coverage");

    private readonly IEnumerable<ISourceDefinition> _definitions;
    private readonly ISourceRegistry _registry;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IActionGateway _gateway;
    private readonly IClock _clock;

    public ReconcileSourceCoverageHandler(
        IEnumerable<ISourceDefinition> definitions,
        ISourceRegistry registry,
        IUnitOfWork unitOfWork,
        IActionGateway gateway,
        IClock clock)
    {
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<ReconcileSourceCoverageResult> HandleAsync(
        SourceId sourceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceId);

        var source = await _registry.GetByIdAsync(sourceId, cancellationToken).ConfigureAwait(false);

        if (source is null)
        {
            return new ReconcileSourceCoverageResult(
                sourceId.Value,
                ReconcileSourceCoverageStatus.NotFound,
                $"Source '{sourceId}' is not registered, so there is nothing to reconcile.",
                []);
        }

        var shipping = _definitions.ToList();
        var definition = shipping.Find(d => d.SourceId == sourceId);

        if (definition is null)
        {
            // The list is named, not just its absence. "No definition" has two very different
            // causes - a row somebody registered by hand, and a connector that failed to register
            // its definition because it is switched off in this configuration - and the reader
            // cannot tell them apart without seeing what the container actually holds.
            var known = shipping.Count == 0
                ? "no connector registered a definition at all"
                : string.Join(", ", shipping.Select(d => d.SourceId.Value));

            return new ReconcileSourceCoverageResult(
                sourceId.Value,
                ReconcileSourceCoverageStatus.NoDefinition,
                $"No connector in this build ships a definition for '{sourceId}', so there is " +
                $"nothing to reconcile it against. Definitions present: [{known}]. A row " +
                "registered by hand stays as the operator wrote it; a connector switched off in " +
                "this configuration ships nothing to reconcile against either.",
                [.. source.Categories]);
        }

        var now = _clock.UtcNow;

        // Constructed to be read, never added. This is the shipped definition's opinion about what
        // the connector can now answer, which is the only thing being compared.
        var shipped = definition.Definition(now);

        var union = source.Categories
            .Union(shipped.Categories)
            .OrderBy(category => (int)category)
            .ToList();

        var added = union.Except(source.Categories).ToList();

        if (added.Count == 0)
        {
            return new ReconcileSourceCoverageResult(
                sourceId.Value,
                ReconcileSourceCoverageStatus.AlreadyCurrent,
                $"Source '{sourceId}' already supplies every category this build declares.",
                [.. source.Categories]);
        }

        var proposal = ActionProposal.Create(
            CorrelationId.New(),
            Capability.ReferenceDataManagement,
            ReconcileActionType,
            ActionTarget.Create("DataSource", sourceId.Value),
            new ReconcileSourceCoverageParameters(source, added, union),

            // Widening a registry row spends nothing and admits no data on its own: a source still
            // has to be active, licensed and asked before anything is fetched from it.
            ActionEconomics.NoFinancialEffect(),
            ProposedBy.Service(ServiceId, ServiceVersion),

            // The resulting set, not just the source. Keyed on the source alone, the second
            // capability a connector ever gains would be suppressed by the first reconciliation
            // and the refusal would name a category count nobody could explain.
            $"source.reconcile-coverage:{sourceId.Value}:" +
            string.Join('-', union.Select(category => (int)category)),
            now);

        var outcome = await _gateway.DispatchAsync(
            proposal,
            async token =>
            {
                source.UpdateCoverage(union, now);

                await _unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);

                return source;
            },
            cancellationToken).ConfigureAwait(false);

        return outcome.Status switch
        {
            ActionOutcomeStatus.Executed => new ReconcileSourceCoverageResult(
                sourceId.Value,
                ReconcileSourceCoverageStatus.Widened,
                outcome.Reason,
                union),

            ActionOutcomeStatus.DuplicateSuppressed => new ReconcileSourceCoverageResult(
                sourceId.Value,
                ReconcileSourceCoverageStatus.AlreadyCurrent,
                outcome.Reason,
                union),

            ActionOutcomeStatus.ApprovalRequired => new ReconcileSourceCoverageResult(
                sourceId.Value,
                ReconcileSourceCoverageStatus.ApprovalRequired,
                outcome.Reason,
                [.. source.Categories]),

            // Denied, and any future status: not reconciled. Fail closed, including in a switch.
            _ => new ReconcileSourceCoverageResult(
                sourceId.Value,
                ReconcileSourceCoverageStatus.Denied,
                outcome.Reason,
                [.. source.Categories]),
        };
    }
}

/// <summary>What became of a coverage reconciliation.</summary>
public enum ReconcileSourceCoverageStatus
{
    Widened = 0,
    AlreadyCurrent = 1,
    NotFound = 2,
    NoDefinition = 3,
    ApprovalRequired = 4,
    Denied = 5,
}

/// <param name="SourceId">Which source.</param>
/// <param name="Status">What happened.</param>
/// <param name="Reason">Why.</param>
/// <param name="Categories">What the source supplies now.</param>
public sealed record ReconcileSourceCoverageResult(
    string SourceId,
    ReconcileSourceCoverageStatus Status,
    string Reason,
    IReadOnlyList<DataCategory> Categories);
