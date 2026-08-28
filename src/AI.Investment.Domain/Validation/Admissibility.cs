using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Observations;

namespace AI.Investment.Domain.Validation;

/// <summary>Why a piece of information may not be used at a historical decision time.</summary>
/// <remarks>
/// <see cref="None"/> is zero, and it is the only value that means "usable". Everything else is a
/// refusal, so a default-initialised or badly deserialised judgement refuses rather than admits.
/// </remarks>
public enum AdmissibilityRefusal
{
    /// <summary>Admissible. The only non-refusal.</summary>
    None = 0,

    /// <summary>It became public after the decision. Using it is look-ahead bias.</summary>
    PublishedAfterCutoff = 1,

    /// <summary>It describes a period that had not finished. A fact about the future is not a fact.</summary>
    DescribesPeriodAfterCutoff = 2,

    /// <summary>There is no provenance, so nothing can be established about it either way.</summary>
    ProvenanceMissing = 3,

    /// <summary>It was fetched before it was published, so at least one timestamp is wrong.</summary>
    ImpossibleOrdering = 4,

    /// <summary>A derived value whose own inputs were not yet public at the decision.</summary>
    DerivedFromInadmissibleEvidence = 5,

    /// <summary>The value's cutoff is later than the decision it is being used for.</summary>
    CalculatedWithALaterCutoff = 6,
}

/// <summary>
/// Whether one piece of information could legitimately have been used at a point in the past.
/// </summary>
/// <remarks>
/// Three states rather than two. <see cref="IsAdmissible"/> and <see cref="IsRefused"/> do not
/// partition the space: <see cref="IsUndeterminable"/> is the case where the record does not carry
/// enough information to decide, and it is deliberately separate from a refusal because the two
/// call for different responses. A refusal is a fact about the data; an undeterminable judgement is
/// a fact about the <em>record</em>, and a validation run that meets one has discovered that its own
/// history is not good enough to measure from. Collapsing the two would let a backtest quietly
/// proceed over evidence nobody can vouch for, which is the failure mode this whole phase exists to
/// prevent.
/// </remarks>
public sealed record Admissibility
{
    private Admissibility(AdmissibilityRefusal refusal, bool undeterminable, string explanation)
    {
        Refusal = refusal;
        IsUndeterminable = undeterminable;
        Explanation = explanation;
    }

    public AdmissibilityRefusal Refusal { get; }

    /// <summary>True when the record cannot support a judgement either way.</summary>
    public bool IsUndeterminable { get; }

    public string Explanation { get; }

    public bool IsAdmissible => Refusal == AdmissibilityRefusal.None && !IsUndeterminable;

    /// <summary>True when it was decided that this may not be used.</summary>
    public bool IsRefused => Refusal != AdmissibilityRefusal.None;

    public static Admissibility Admitted { get; } =
        new(AdmissibilityRefusal.None, undeterminable: false, "Public before the decision, and about a period that had ended.");

    public static Admissibility Refused(AdmissibilityRefusal refusal, string explanation) =>
        new(refusal, undeterminable: false, explanation);

    /// <summary>The record cannot support a judgement. Fails the validation rather than passing it.</summary>
    public static Admissibility Undeterminable(AdmissibilityRefusal refusal, string explanation) =>
        new(refusal, undeterminable: true, explanation);

    public override string ToString() => Explanation;
}

/// <summary>
/// The point-in-time guard: what the platform was allowed to know, and when.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Publication time is the only admission test.</strong> A filing published before the
/// decision was knowable to anybody at the decision, whether or not this platform had fetched it
/// yet; a filing published afterwards was knowable to nobody, however early this platform happens to
/// have stored it. <c>RetrievedAtUtc</c> is a fact about this installation's fetch history and
/// nothing else, so admitting on it would make a historical result change when a source is
/// backfilled - the same period, the same world, a different answer.
/// </para>
/// <para>
/// This class therefore <strong>never admits on retrieval time</strong>. It reads
/// <c>RetrievedAtUtc</c> in exactly one place, and only to detect the impossible ordering that means
/// one of the two timestamps is wrong - a check that can only ever make a judgement stricter. A
/// domain test asserts the consequence directly: changing retrieval time alone never changes a
/// verdict.
/// </para>
/// <para>
/// The second rule catches the subtler leak. A quarterly figure published on the day the quarter
/// ended is not evidence about that quarter; it is a data error, or a preliminary number restated
/// later. For a <see cref="ClaimKind.Fact"/>, a value describing a period that had not finished at
/// the decision is refused even though its publication time passes, because a "fact" about a period
/// still in progress is a forecast wearing a fact's clothes.
/// </para>
/// <para>
/// Pure and total. Every method is a function of its arguments, so the guard can be exhaustively
/// tested without a database, a clock or a fixture.
/// </para>
/// </remarks>
public static class PointInTimeGuard
{
    /// <summary>Judges one piece of provenance against a decision time.</summary>
    public static Admissibility Judge(Provenance? provenance, ClaimKind kind, KnowledgeCutoff cutoff)
    {
        ArgumentNullException.ThrowIfNull(cutoff);

        if (provenance is null)
        {
            return Admissibility.Undeterminable(
                AdmissibilityRefusal.ProvenanceMissing,
                "the value carries no provenance, so whether it was knowable at the decision cannot " +
                "be established. A validation run may not guess.");
        }

        // The only reading of RetrievedAtUtc in this class, and it can only tighten a verdict: a
        // value this system fetched before it was published means one of the two timestamps is
        // wrong, and neither can then be trusted to place the value in time.
        if (provenance.RetrievedAtUtc < provenance.PublishedAtUtc)
        {
            return Admissibility.Undeterminable(
                AdmissibilityRefusal.ImpossibleOrdering,
                $"the value was fetched at {provenance.RetrievedAtUtc:O} and published at " +
                $"{provenance.PublishedAtUtc:O}, which cannot both be true. Its position in time " +
                "cannot be established.");
        }

        if (!cutoff.Admits(provenance.PublishedAtUtc))
        {
            return Admissibility.Refused(
                AdmissibilityRefusal.PublishedAfterCutoff,
                $"it became public at {provenance.PublishedAtUtc:O}, after the decision at {cutoff}. " +
                "Nobody knew it yet.");
        }

        if (kind == ClaimKind.Fact && !cutoff.Admits(provenance.AsOfUtc))
        {
            return Admissibility.Refused(
                AdmissibilityRefusal.DescribesPeriodAfterCutoff,
                $"it is recorded as a fact about {provenance.AsOfUtc:O}, which had not happened at " +
                $"the decision at {cutoff}. A fact about a period still in progress is a forecast.");
        }

        return Admissibility.Admitted;
    }

    /// <summary>Judges one stored observation against a decision time.</summary>
    public static Admissibility Judge(Observation? observation, KnowledgeCutoff cutoff)
    {
        ArgumentNullException.ThrowIfNull(cutoff);

        return observation is null
            ? Admissibility.Undeterminable(
                AdmissibilityRefusal.ProvenanceMissing,
                "there is no observation to judge.")
            : Judge(observation.Provenance, observation.Kind, cutoff);
    }

    /// <summary>
    /// Judges a calculated value, which is admissible only if every input behind it was.
    /// </summary>
    /// <remarks>
    /// A derived number launders its inputs: the calculation itself was performed at a stated time,
    /// but what it <em>knew</em> is whatever went into it. <see cref="MetricResult.EvidenceAvailableAtUtc"/>
    /// is the latest publication time among its inputs, and that - not the moment of arithmetic - is
    /// when the result became knowable.
    /// </remarks>
    public static Admissibility Judge(MetricResult? result, KnowledgeCutoff cutoff)
    {
        ArgumentNullException.ThrowIfNull(cutoff);

        if (result is null)
        {
            return Admissibility.Undeterminable(
                AdmissibilityRefusal.ProvenanceMissing,
                "there is no calculated result to judge.");
        }

        if (result.Inputs.Count == 0)
        {
            return Admissibility.Undeterminable(
                AdmissibilityRefusal.ProvenanceMissing,
                "the calculation records no inputs, so what it knew cannot be established.");
        }

        if (!cutoff.Admits(result.EvidenceAvailableAtUtc))
        {
            return Admissibility.Refused(
                AdmissibilityRefusal.DerivedFromInadmissibleEvidence,
                $"its latest input became public at {result.EvidenceAvailableAtUtc:O}, after the " +
                $"decision at {cutoff}. The arithmetic is not the leak; the inputs are.");
        }

        if (result.Cutoff.AsOfUtc > cutoff.AsOfUtc)
        {
            return Admissibility.Refused(
                AdmissibilityRefusal.CalculatedWithALaterCutoff,
                $"it was calculated as of {result.Cutoff}, which is later than the decision at " +
                $"{cutoff}. A value computed with a wider view of the world is not the value the " +
                "decision had.");
        }

        return Admissibility.Admitted;
    }
}
