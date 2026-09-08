using AI.Investment.Domain.Observations;

namespace AI.Investment.Application.Normalization;

/// <summary>
/// What reading a payload produced, or why it could not be read.
/// </summary>
/// <remarks>
/// <para>
/// A result rather than an exception. A provider that changed a field name is an ordinary event in
/// the life of a data platform - frequent, expected, and something the ledger should record rather
/// than something that should unwind a batch of fifty subjects.
/// </para>
/// <para>
/// <strong>Three shapes, not two.</strong> A payload can be read, refused, or read with some of its
/// rows refused. The third existed as a fact long before it existed as a type: eleven archived price
/// payloads hold between 556 and 969 perfectly readable rows each and were discarded whole because
/// one to twenty rows carried a non-positive close. That was not a judgement anybody made - it was
/// what a two-shaped result forced, because <see cref="Quarantine"/> constructs with an empty
/// observation list, so a normaliser meeting a bad row had no way to report anything else.
/// </para>
/// <para>
/// <strong><see cref="IsQuarantined"/> keeps its exact meaning: the payload could not be read at
/// all.</strong> A partial read is not quarantined - its observations are real and are recorded -
/// so every existing caller that branches on it behaves as it did. The refused rows travel
/// separately, because a hole a reader cannot see is the thing this platform is most careful about.
/// </para>
/// </remarks>
public sealed record NormalizationResult
{
    private NormalizationResult(
        IReadOnlyList<Observation> observations,
        string? ruleId,
        string? reason,
        int rejectedRows,
        string? rejectedRowRuleId,
        string? rejectedRowReason)
    {
        Observations = observations;
        RuleId = ruleId;
        Reason = reason;
        RejectedRows = rejectedRows;
        RejectedRowRuleId = rejectedRowRuleId;
        RejectedRowReason = rejectedRowReason;
    }

    public IReadOnlyList<Observation> Observations { get; }

    /// <summary>The rule that rejected the payload, or null when it was read.</summary>
    public string? RuleId { get; }

    /// <summary>Why it was rejected, or null when it was read.</summary>
    public string? Reason { get; }

    /// <summary>
    /// How many individual rows were refused while the rest of the payload was read. Zero unless
    /// this is a partial read.
    /// </summary>
    public int RejectedRows { get; }

    /// <summary>The rule that refused those rows, or null when none were refused.</summary>
    public string? RejectedRowRuleId { get; }

    /// <summary>Which rows and why, in the words an operator reads. Null when none were refused.</summary>
    public string? RejectedRowReason { get; }

    /// <summary>The payload could not be read at all. Unchanged: a partial read is not this.</summary>
    public bool IsQuarantined => RuleId is not null;

    /// <summary>Some rows were refused and the rest were read.</summary>
    public bool HadRejectedRows => RejectedRows > 0;

    public static NormalizationResult Normalized(IReadOnlyList<Observation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        return new NormalizationResult(observations, null, null, 0, null, null);
    }

    /// <summary>
    /// The payload was read, and named rows within it were refused rather than admitted.
    /// </summary>
    /// <remarks>
    /// A normaliser reaches for this only when a refused row is individually meaningless and the
    /// rest of the document is not - never as a way to be permissive about a payload it does not
    /// understand. A document whose shape is wrong, whose encoding is wrong, or whose every row is
    /// refused is quarantined whole, exactly as before. The two guards below are what stop this
    /// factory from being used to say either of those things quietly.
    /// </remarks>
    public static NormalizationResult Partial(
        IReadOnlyList<Observation> observations,
        string rejectedRowRuleId,
        string rejectedRowReason,
        int rejectedRows)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentException.ThrowIfNullOrWhiteSpace(rejectedRowRuleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rejectedRowReason);

        if (rejectedRows <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rejectedRows),
                rejectedRows,
                "A partial read refused at least one row. A result claiming none would put a "
                + "rejection in the ledger that never happened.");
        }

        if (observations.Count == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(observations),
                observations.Count,
                "A payload whose every row was refused is quarantined, not partially read. "
                + "Recording it as a successful read of nothing would make a document the platform "
                + "could not use look like a document with nothing in it.");
        }

        return new NormalizationResult(
            observations, null, null, rejectedRows, rejectedRowRuleId, rejectedRowReason);
    }

    public static NormalizationResult Quarantine(string ruleId, string reason) =>
        new([], ruleId, reason, 0, null, null);

    public override string ToString() =>
        IsQuarantined
            ? $"quarantined [{RuleId}] {Reason}"
            : HadRejectedRows
                ? $"{Observations.Count} observations, {RejectedRows} row(s) refused [{RejectedRowRuleId}]"
                : $"{Observations.Count} observations";
}
