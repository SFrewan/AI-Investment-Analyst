namespace AI.Investment.Domain.Ingestion;

/// <summary>
/// Whether a provider can serve a particular request, and if not, which rule says so.
/// </summary>
/// <remarks>
/// Deliberately shaped like <see cref="Sources.SourceAdmissionResult"/> rather than reduced to a
/// bool. The two answer different questions - "are we allowed to?" and "is this provider able
/// to?" - and both have to be explainable after the fact, because "no data appeared" is otherwise
/// indistinguishable from "nothing was asked for".
/// </remarks>
/// <param name="IsCapable">Whether the provider can serve the request.</param>
/// <param name="RuleId">The versioned rule that refused, or null when capable.</param>
/// <param name="Reason">A human-readable explanation, or null when capable.</param>
public sealed record ProviderCapabilityResult(bool IsCapable, string? RuleId, string? Reason)
{
    public static ProviderCapabilityResult Capable { get; } = new(true, null, null);

    public static ProviderCapabilityResult Incapable(string ruleId, string reason) =>
        new(false, ruleId, reason);

    public override string ToString() =>
        IsCapable ? "capable" : $"incapable [{RuleId}] {Reason}";
}
