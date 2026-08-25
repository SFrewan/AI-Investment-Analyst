namespace AI.Investment.Domain.Sources;

/// <summary>
/// The outcome of asking whether a source may be drawn from for a particular purpose.
/// </summary>
/// <remarks>
/// A result rather than an exception, because refusal is an expected outcome of a routine
/// question, not an error. Ingestion asks this before every run: an inactive source, a narrowed
/// licence or a category the source does not cover are all ordinary states, and the caller
/// routes to quarantine or skips the run rather than unwinding a stack.
/// </remarks>
/// <param name="IsAdmitted">Whether the source may be drawn from.</param>
/// <param name="RuleId">
/// The versioned identifier of the rule that refused, or null when admitted. Named for the same
/// reason policy decisions carry policy identifiers: a refusal has to be explainable later.
/// </param>
/// <param name="Reason">A human-readable explanation, or null when admitted.</param>
public sealed record SourceAdmissionResult(bool IsAdmitted, string? RuleId, string? Reason)
{
    /// <summary>The source may be drawn from.</summary>
    public static SourceAdmissionResult Admitted { get; } = new(true, null, null);

    /// <summary>The source may not be drawn from, for the stated reason.</summary>
    public static SourceAdmissionResult Refused(string ruleId, string reason) =>
        new(false, ruleId, reason);

    public override string ToString() =>
        IsAdmitted ? "admitted" : $"refused [{RuleId}] {Reason}";
}
