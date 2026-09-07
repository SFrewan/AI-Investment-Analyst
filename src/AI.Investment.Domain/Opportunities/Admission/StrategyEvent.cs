using System.Globalization;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Validation;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Opportunities.Admission;

/// <summary>Whether a declaration was written before the predictions it scores, or after them.</summary>
/// <remarks>
/// <para>
/// The distinction the platform kept blurring. A declaration made before any prediction exists is a
/// claim about the future. A declaration made today and scored against history is a description of
/// data that already existed, however carefully the event was chosen - and the two are not
/// distinguishable from the score alone, which is why the strategy has to say which it is.
/// </para>
/// <para>
/// Marking a backtest does not exempt it from anything. It fails the look-ahead check by
/// construction, because its declaration genuinely does postdate the evidence. What the marking
/// buys is an honest report: the refusal reads as the expected consequence of a backtest rather
/// than as a defect, and the way out is to leave the declaration on the record and accumulate
/// predictions after it.
/// </para>
/// </remarks>
public enum DeclarationBasis
{
    /// <summary>Not stated. Refused, so a defaulted value can never pass for a claim about the future.</summary>
    Unknown = 0,

    /// <summary>Written down before the predictions it scores were made.</summary>
    Prospective = 1,

    /// <summary>Written down today and scored against history that already existed.</summary>
    Backtest = 2,
}

/// <summary>
/// What a detection strategy claims will happen, declared before it is measured.
/// </summary>
/// <remarks>
/// <para>
/// A strategy states a probability. Validation labels an outcome. If those two describe different
/// events the resulting Brier score measures the gap between them rather than the strategy - which
/// is not hypothetical: the platform's first strategy scored 0.5538 against the validation event and
/// 0.11 against its own, and a validation report would have called that a badly calibrated model.
/// This type exists so that the event is one object, declared once, and both sides read it.
/// </para>
/// <para>
/// It carries two dataset fingerprints because they answer different questions.
/// <see cref="EvidenceBaseFingerprint"/> names the data the strategy will be <em>scored</em> on;
/// <see cref="TuningBasisFingerprint"/> names the data its parameters were <em>chosen</em> on. When
/// they are the same string the measurement is in-sample and the score is a description of the past
/// rather than a claim about the future. No arithmetic can detect that, so it is declared and
/// checked rather than inferred.
/// </para>
/// <para>
/// Shaped deliberately after <see cref="BenchmarkDefinition"/>: a canonical string, a hash of it, and
/// a check that the declaration predates the thing it is judged against. A benchmark chosen once the
/// numbers are in is not a benchmark, and neither is an event.
/// </para>
/// </remarks>
public sealed record StrategyEvent
{
    /// <summary>The declaration cannot postdate the evidence it will be scored on.</summary>
    public const string DeclaredAfterTheEvidenceRule = "Admission.EventDeclaredAfterTheEvidence";

    public const int MaxFingerprintLength = 200;

    /// <summary>The fingerprint of a strategy whose parameters were not fitted to any dataset.</summary>
    public const string NotTuned = "untuned";

    private StrategyEvent(
        OpportunityType type,
        PredictionDirection direction,
        Percentage threshold,
        int horizonSessions,
        string evidenceBaseFingerprint,
        string tuningBasisFingerprint,
        DateTime declaredAtUtc,
        decimal? claimedBrier,
        decimal? claimedSkill,
        DeclarationBasis basis,
        string searchFamily)
    {
        Type = type;
        Direction = direction;
        Threshold = threshold;
        HorizonSessions = horizonSessions;
        EvidenceBaseFingerprint = evidenceBaseFingerprint;
        TuningBasisFingerprint = tuningBasisFingerprint;
        DeclaredAtUtc = declaredAtUtc;
        ClaimedBrier = claimedBrier;
        ClaimedSkill = claimedSkill;
        Basis = basis;
        SearchFamily = searchFamily;
    }

    /// <summary>The strategy this event belongs to.</summary>
    public OpportunityType Type { get; }

    /// <summary>Which way the strategy says the subject will move.</summary>
    public PredictionDirection Direction { get; }

    /// <summary>
    /// The event itself: the realised return reaching at least this ratio, compared with
    /// <c>&gt;=</c> exactly as <see cref="OutcomeLabeller"/> compares it.
    /// </summary>
    public Percentage Threshold { get; }

    /// <summary>How many sessions the claim is about.</summary>
    public int HorizonSessions { get; }

    /// <summary>The dataset this strategy is scored on.</summary>
    public string EvidenceBaseFingerprint { get; }

    /// <summary>
    /// The dataset the strategy's parameters were chosen on, or <see cref="NotTuned"/>.
    /// </summary>
    public string TuningBasisFingerprint { get; }

    /// <summary>When the claim was written down.</summary>
    public DateTime DeclaredAtUtc { get; }

    /// <summary>
    /// The Brier score this strategy asserts it can achieve, declared before it is measured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes the sample requirement derivable rather than chosen. A strategy that
    /// claims a strong score is asking to be judged against a large effect, which a smaller sample
    /// can establish; one that claims to scrape the ceiling is asking to be judged against a small
    /// effect, which needs many more resolved predictions before it means anything. See
    /// <see cref="SampleRequirement"/> for what is done with it.
    /// </para>
    /// <para>
    /// It sits inside <see cref="Fingerprint"/> deliberately. A claim that could be lowered after
    /// the score arrived would be a description of the result rather than a prediction about it, and
    /// silently editing it produces a different declaration rather than a kinder bar.
    /// </para>
    /// <para>
    /// Null means the strategy declared nothing, and is read as implicitly claiming the admission
    /// ceiling - the weakest admissible claim, and therefore the most demanding sample. Saying
    /// nothing is never the cheap option.
    /// </para>
    /// </remarks>
    public decimal? ClaimedBrier { get; }

    /// <summary>
    /// The share of the base-rate Brier this strategy asserts it can remove, if it claims that way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The alternative to <see cref="ClaimedBrier"/>, and the better one for most events. An
    /// absolute Brier claim is not interpretable without the base rate: for an event that happens
    /// 87 per cent of the time, a forecaster who knows only that scores 0.1112, so a claim of 0.20
    /// is a promise to do <em>worse than nothing</em> - which is what the platform's first revenue
    /// strategy accidentally promised. A claim of 0.25 here means "I will score at most three
    /// quarters of what the base rate alone scores", and that sentence is well formed whatever the
    /// base rate turns out to be.
    /// </para>
    /// <para>
    /// A declaration states one or the other, never both: two claims would leave the gate choosing
    /// which one the strategy meant, and it would choose the flattering one by construction.
    /// </para>
    /// </remarks>
    public decimal? ClaimedSkill { get; }

    /// <summary>
    /// The search this strategy belongs to, which is what the trial budget counts within.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The budget exists to catch a search: twenty variants tried against one dataset, the best one
    /// kept, and a winner that looked good by chance before it looked good by skill. Counting per
    /// evidence base could not tell that from something quite different - four independent
    /// hypotheses, each written down before it was measured, that happen to use the same data. The
    /// first is a search; the second is what pre-registration is for.
    /// </para>
    /// <para>
    /// So trials count within a family. A hypothesis declared on its own opens a family of one; a
    /// variant found by sweeping an earlier strategy names that strategy's family and is counted in
    /// it. Naming is not a way out: the register derives the count from its own entries and takes
    /// whichever is larger, the declared count or the counted one, so a declaration can only make
    /// its own budget position worse. And hypothesis proliferation across families is handled
    /// separately, by tightening the significance every derived sample size is computed at - see
    /// <see cref="SampleRequirement"/>.
    /// </para>
    /// </remarks>
    public string SearchFamily { get; }

    /// <summary>
    /// Whether this declaration precedes the predictions it scores, or is a backtest.
    /// </summary>
    /// <remarks>
    /// Inside <see cref="Fingerprint"/>, so relabelling a backtest as prospective produces a
    /// different declaration rather than a better verdict.
    /// </remarks>
    public DeclarationBasis Basis { get; }

    /// <summary>A stable hash of everything above, so a silently edited claim is a different one.</summary>
    public string Fingerprint => Fingerprints.Of(Canonical());

    /// <summary>True when the strategy was fitted to the very data it is about to be scored on.</summary>
    public bool WasTunedOnTheEvidence =>
        string.Equals(TuningBasisFingerprint, EvidenceBaseFingerprint, StringComparison.Ordinal);

    public static StrategyEvent Declare(
        OpportunityType type,
        PredictionDirection direction,
        Percentage threshold,
        int horizonSessions,
        string evidenceBaseFingerprint,
        DateTime declaredAtUtc,
        string? tuningBasisFingerprint = null,
        decimal? claimedBrier = null,
        DeclarationBasis basis = DeclarationBasis.Prospective,
        decimal? claimedSkill = null,
        string? searchFamily = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(threshold);
        DateRange.EnsureUtc(declaredAtUtc, nameof(declaredAtUtc));

        if (direction is not (PredictionDirection.Positive or PredictionDirection.Negative))
        {
            throw new DomainValidationException(
                nameof(direction),
                $"A strategy that declares '{direction}' has not made a claim. Abstaining and not " +
                "having decided are both legitimate at prediction time and neither is an event that " +
                "can be scored.");
        }

        if (basis == DeclarationBasis.Unknown)
        {
            throw new DomainValidationException(
                nameof(basis),
                "A declaration must say whether it precedes the predictions it scores. A strategy " +
                "that has not said cannot be told from one claiming to have known in advance, and " +
                "that is the whole difference between a prediction and a description.");
        }

        if (claimedBrier is not null && claimedSkill is not null)
        {
            throw new DomainValidationException(
                nameof(claimedSkill),
                "A declaration claims an absolute score or a share of the base rate, not both. Two " +
                "claims leave the gate to choose which one was meant, and whichever it chose would " +
                "be the strategy's choice rather than its declaration.");
        }

        if (claimedSkill is { } skill && skill is <= 0m or > 1m)
        {
            throw new DomainValidationException(
                nameof(claimedSkill),
                $"A claimed share of the base-rate score lies in (0,1]; {skill} is not one. Zero is " +
                "a claim to add nothing, and no sample establishes that.");
        }

        if (claimedBrier is { } claim && claim is < 0m or > 1m)
        {
            throw new DomainValidationException(
                nameof(claimedBrier),
                $"A Brier score lies between zero and one; {claim} is not one. A claim outside the " +
                "scale cannot be compared with a measurement on it.");
        }

        if (horizonSessions < 1)
        {
            throw new DomainValidationException(
                nameof(horizonSessions),
                "A claim about zero sessions resolves at the instant it is made, which means it is " +
                "about the present and there is nothing to be right about.");
        }

        return new StrategyEvent(
            type,
            direction,
            threshold,
            horizonSessions,
            NormaliseFingerprint(evidenceBaseFingerprint, nameof(evidenceBaseFingerprint)),
            NormaliseFingerprint(tuningBasisFingerprint ?? NotTuned, nameof(tuningBasisFingerprint)),
            declaredAtUtc,
            claimedBrier,
            claimedSkill,
            basis,
            NormaliseFingerprint(searchFamily ?? type.Value, nameof(searchFamily)));
    }

    /// <summary>
    /// Refuses a declaration written after the evidence it will be judged on already existed.
    /// </summary>
    /// <remarks>
    /// This is the look-ahead check one level up from <see cref="PredictionRecord"/>. That type
    /// stops a single prediction resting on evidence that was not yet public; this stops the
    /// <em>event definition</em> being chosen once the outcomes are visible, which no per-prediction
    /// check can see because each individual prediction is then perfectly well formed.
    /// </remarks>
    public void EnsureDeclaredBefore(DateTime earliestPredictionUtc)
    {
        DateRange.EnsureUtc(earliestPredictionUtc, nameof(earliestPredictionUtc));

        if (DeclaredAtUtc > earliestPredictionUtc)
        {
            throw new DomainRuleViolationException(
                DeclaredAfterTheEvidenceRule,
                $"the event was declared at {DeclaredAtUtc:O}, after the earliest prediction it " +
                $"scores was made at {earliestPredictionUtc:O}. An event chosen once the outcomes " +
                "are visible is a description of what happened.");
        }
    }

    /// <summary>True when the labeller's threshold is the one this strategy actually counted.</summary>
    public bool Matches(Percentage labelledThreshold)
    {
        ArgumentNullException.ThrowIfNull(labelledThreshold);
        return Threshold.Ratio == labelledThreshold.Ratio;
    }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Type} {Direction} >= {Threshold.Ratio:0.####} over {HorizonSessions} sessions #{Fingerprint[..8]}");

    private static string NormaliseFingerprint(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(
                parameterName,
                "A dataset fingerprint is required. An unnamed dataset cannot be compared with " +
                "another, which is the entire purpose of recording it.");
        }

        var trimmed = value.Trim();

        return trimmed.Length > MaxFingerprintLength
            ? throw new DomainValidationException(
                parameterName,
                $"A dataset fingerprint may not exceed {MaxFingerprintLength} characters.")
            : trimmed;
    }

    private string Canonical() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Type}|{Direction}|{Threshold.Ratio}|{HorizonSessions}|{EvidenceBaseFingerprint}|{TuningBasisFingerprint}|{DeclaredAtUtc:O}|{ClaimedBrier}|{Basis}|{ClaimedSkill}|{SearchFamily}");
}
