using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Ingestion;

/// <summary>
/// The deterministic half of <see cref="IngestionGateway"/>'s gates, askable before anything is spent.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> An acquisition runner charges its authorisation at intent - one
/// unit claimed immediately before the request is handed to <c>IDataAcquisition</c>, and never
/// credited back. But four gates stand inside <see cref="IngestionGateway.IngestAsync"/>, which is
/// on the far side of that charge. A request the gateway would refuse for registration, admission or
/// capability therefore spends a unit for a request that never leaves the process. On a six-unit
/// authorisation whose connector is disabled and whose source may not be active, that is not a
/// theoretical loss: it is the likeliest way the whole ceiling is spent without one byte reaching
/// the provider.
/// </para>
/// <para>
/// <strong>What it is, and is not.</strong> It is the same decisions, in the same order, using the
/// same rule identifiers, evaluated over values the caller has already read. It is pure and total:
/// it opens no connection, resolves no service, reserves nothing and mutates nothing, so asking it
/// costs nothing and asking it twice costs nothing twice. It is <em>not</em> a replacement for the
/// gateway's gates - the gateway still runs all four on the way to the provider, unchanged. Anything
/// this admits may still be refused there; nothing this refuses could have been admitted there.
/// </para>
/// <para>
/// <strong>The two gates that stay behind, and why.</strong>
/// </para>
/// <list type="bullet">
/// <item>
/// <see cref="IngestionGateway.WithinRateLimitRule"/> is not deterministic and not free.
/// <c>IProviderRateLimiter.TryAcquireAsync</c> <em>reserves</em> a request when it succeeds, so
/// evaluating it here would spend a slot the gateway would then spend again. Duplicating a side
/// effect to avoid spending an authorisation unit trades one accounting error for another, so it is
/// deliberately left where it is. A run refused on rate limit still consumes a unit; at one request
/// per batch, paced, that is unreachable in practice and it is reported rather than papered over.
/// </item>
/// <item>
/// <see cref="IngestionGateway.PolicyPermittedRule"/> is the Action/Policy seam - the kill switch,
/// operator privileges and the idempotency claim. It is external state and it audits what it
/// decides, which is a side effect worth having exactly once.
/// </item>
/// </list>
/// <para>
/// <strong>Provider enablement needs no rule of its own.</strong> A connector that is not enabled is
/// not registered in the catalogue at all, so a disabled provider arrives here as a null
/// <c>provider</c> and is refused under <see cref="IngestionGateway.ProviderAvailableRule"/> - the
/// same rule, with the same meaning, that the gateway would use. Inventing a second rule for the
/// same condition would give one refusal two names.
/// </para>
/// </remarks>
public static class IngestionPreflight
{
    /// <summary>
    /// Every rule this can return. The deterministic gates, in the order the gateway applies them.
    /// </summary>
    public static IReadOnlyList<string> PreflightedRules { get; } =
    [
        IngestionGateway.SourceRegisteredRule,
        SourceAdmission.SourceActiveRule,
        SourceAdmission.CategoryRecognisedRule,
        SourceAdmission.SuppliesCategoryRule,
        SourceAdmission.StoragePermittedRule,
        SourceAdmission.ProcessingPermittedRule,
        IngestionGateway.ProviderAvailableRule,
        ProviderCapabilityCheck.CategorySupportedRule,
        ProviderCapabilityCheck.RegionSupportedRule,
        ProviderCapabilityCheck.SubjectKindSupportedRule,
        ProviderCapabilityCheck.WindowSupportedRule,
        ProviderCapabilityCheck.WindowWithinLimitRule,
    ];

    /// <summary>
    /// The gateway rules this deliberately does not evaluate, because doing so would have effects.
    /// </summary>
    public static IReadOnlyList<string> DeferredRules { get; } =
    [
        IngestionGateway.WithinRateLimitRule,
        IngestionGateway.PolicyPermittedRule,
    ];

    /// <summary>
    /// Whether the deterministic gates admit this request.
    /// </summary>
    /// <param name="source">
    /// The registry entry for <c>request.SourceId</c>, or null when the registry holds none. Read by
    /// the caller so that this stays pure; reading a row has no effect a second read would repeat.
    /// </param>
    /// <param name="provider">
    /// The connector registered for that source, or null when none is - which is also what a
    /// disabled connector looks like, because a disabled one is never registered.
    /// </param>
    /// <param name="request">The exact request that would be dispatched, window and all.</param>
    /// <remarks>
    /// The order is <see cref="IngestionGateway.IngestAsync"/>'s own and is not an implementation
    /// detail: registration before permission before ability, so that a refusal names the earliest
    /// reason rather than the first one a reader happens to check. An unregistered source is not
    /// "incapable"; an inactive one is not "unlicensed".
    /// </remarks>
    public static PreflightResult Evaluate(
        DataSource? source,
        IDataProvider? provider,
        IngestionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Gate 1. Registration. An unregistered origin cannot be assessed, so it cannot be used.
        if (source is null)
        {
            return PreflightResult.Refused(
                IngestionGateway.SourceRegisteredRule,
                $"Source '{request.SourceId}' is not in the registry. Nothing may be ingested from " +
                "an origin whose authority and licensing have never been assessed.");
        }

        // Gate 2. Permission. Active, covering, and licensed for what ingestion does.
        var admission = SourceAdmission.Evaluate(source, request.Category, request.Region);

        if (!admission.IsAdmitted)
        {
            return PreflightResult.Refused(admission.RuleId!, admission.Reason!);
        }

        // Gate 3a. A connector exists. A disabled one does not, which is the point.
        if (provider is null)
        {
            return PreflightResult.Refused(
                IngestionGateway.ProviderAvailableRule,
                $"Source '{request.SourceId}' is registered and admissible, but no connector is " +
                "registered for it, so there is nothing to fetch with. A connector that is not " +
                "enabled is not registered.");
        }

        // Gate 3b. Ability, including the request's shape. This is where a request carrying a window
        // against a connector that serves none is refused - for SEC EDGAR, the shape error that
        // would otherwise be discovered one unit too late.
        var capability = ProviderCapabilityCheck.Evaluate(provider.Capabilities, request);

        if (!capability.IsCapable)
        {
            return PreflightResult.Refused(capability.RuleId!, capability.Reason!);
        }

        return PreflightResult.Admitted;
    }
}

/// <summary>
/// Whether the deterministic gates admit a request, and which rule refused when they do not.
/// </summary>
/// <remarks>
/// Shaped like <see cref="SourceAdmissionResult"/> and <see cref="ProviderCapabilityResult"/> rather
/// than reduced to a bool, for the reason both of those give: "no data appeared" has to stay
/// distinguishable from "nothing was asked for", and a refusal that cannot name its rule is neither.
/// </remarks>
/// <param name="IsAdmitted">Whether every deterministic gate admits the request.</param>
/// <param name="RuleId">The versioned rule that refused, or null when admitted.</param>
/// <param name="Reason">Why, in the words an operator reads, or null when admitted.</param>
public sealed record PreflightResult(bool IsAdmitted, string? RuleId, string? Reason)
{
    public static PreflightResult Admitted { get; } = new(true, null, null);

    public static PreflightResult Refused(string ruleId, string reason) =>
        new(false, ruleId, reason);

    public override string ToString() =>
        IsAdmitted ? "admitted" : $"refused [{RuleId}] {Reason}";
}
