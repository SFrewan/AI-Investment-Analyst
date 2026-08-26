using AI.Investment.Domain.Observations;

namespace AI.Investment.Application.Normalization;

/// <summary>
/// What reading a payload produced, or why it could not be read.
/// </summary>
/// <remarks>
/// A result rather than an exception. A provider that changed a field name is an ordinary event in
/// the life of a data platform - frequent, expected, and something the ledger should record rather
/// than something that should unwind a batch of fifty subjects.
/// </remarks>
public sealed record NormalizationResult
{
    private NormalizationResult(
        IReadOnlyList<Observation> observations,
        string? ruleId,
        string? reason)
    {
        Observations = observations;
        RuleId = ruleId;
        Reason = reason;
    }

    public IReadOnlyList<Observation> Observations { get; }

    /// <summary>The rule that rejected the payload, or null when it was read.</summary>
    public string? RuleId { get; }

    /// <summary>Why it was rejected, or null when it was read.</summary>
    public string? Reason { get; }

    public bool IsQuarantined => RuleId is not null;

    public static NormalizationResult Normalized(IReadOnlyList<Observation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        return new NormalizationResult(observations, null, null);
    }

    public static NormalizationResult Quarantine(string ruleId, string reason) =>
        new([], ruleId, reason);

    public override string ToString() =>
        IsQuarantined
            ? $"quarantined [{RuleId}] {Reason}"
            : $"{Observations.Count} observations";
}
