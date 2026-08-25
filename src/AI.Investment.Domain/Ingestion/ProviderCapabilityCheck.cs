namespace AI.Investment.Domain.Ingestion;

/// <summary>
/// Decides whether a connector can serve a request, before the request is made.
/// </summary>
/// <remarks>
/// <para>
/// The second of the two gates in front of every fetch.
/// <see cref="Sources.SourceAdmission"/> answers "are we permitted to take this?"; this answers
/// "is this connector able to fetch it?". Same specification as both the policy engine and source
/// admission: pure, total, deterministic, fail-closed, and every refusal names a versioned rule.
/// </para>
/// <para>
/// Checking beforehand rather than letting the provider reject the call is not merely tidier. A
/// malformed request still consumes quota, still appears in the provider's logs, and - for a
/// window larger than the provider accepts - may silently return a truncated result rather than
/// an error, which is the failure mode that puts a gap in a history nobody notices.
/// </para>
/// </remarks>
public static class ProviderCapabilityCheck
{
    public const string CategorySupportedRule = "provider.category-supported@1";
    public const string RegionSupportedRule = "provider.region-supported@1";
    public const string SubjectKindSupportedRule = "provider.subject-kind-supported@1";
    public const string WindowSupportedRule = "provider.window-supported@1";
    public const string WindowWithinLimitRule = "provider.window-within-limit@1";

    /// <summary>
    /// Evaluates the rules in order and returns the first refusal, or
    /// <see cref="ProviderCapabilityResult.Capable"/>.
    /// </summary>
    public static ProviderCapabilityResult Evaluate(
        ProviderCapabilities capabilities,
        IngestionRequest request)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(request);

        if (!capabilities.Supports(request.Category))
        {
            return ProviderCapabilityResult.Incapable(
                CategorySupportedRule,
                $"The connector does not supply {request.Category}.");
        }

        if (!capabilities.Covers(request.Region))
        {
            return ProviderCapabilityResult.Incapable(
                RegionSupportedRule,
                $"The connector does not cover {request.Region}.");
        }

        if (!capabilities.Understands(request.Subject.Kind))
        {
            return ProviderCapabilityResult.Incapable(
                SubjectKindSupportedRule,
                $"The connector does not understand subjects of kind '{request.Subject.Kind}'.");
        }

        if (request.Window is not { } window)
        {
            return ProviderCapabilityResult.Capable;
        }

        if (!capabilities.SupportsWindow)
        {
            return ProviderCapabilityResult.Incapable(
                WindowSupportedRule,
                "The request asks for a period, and the connector only serves the latest value. " +
                "Serving the latest value in answer to a historical question would be a wrong " +
                "answer rather than a missing one.");
        }

        if (capabilities.MaxWindowDuration is { } max && window.Duration > max)
        {
            return ProviderCapabilityResult.Incapable(
                WindowWithinLimitRule,
                $"The requested period is {window.Duration}, and the connector accepts at most " +
                $"{max}. Split the request rather than sending one the provider may silently " +
                "truncate.");
        }

        return ProviderCapabilityResult.Capable;
    }
}
