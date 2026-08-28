using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Validation;

namespace AI.Investment.Application.Validation;

/// <summary>One prediction the guard refused, and why.</summary>
/// <param name="PredictionId">Which prediction.</param>
/// <param name="Refusal">The rule that refused it.</param>
/// <param name="Explanation">Why, in terms a reader can check against the record.</param>
/// <param name="WasUndeterminable">
/// True when the record could not support a judgement at all, as opposed to being judged and refused.
/// </param>
public sealed record RefusedPrediction(
    Guid PredictionId,
    AdmissibilityRefusal Refusal,
    string Explanation,
    bool WasUndeterminable);

/// <summary>What a replay of the window produced.</summary>
public sealed record BacktestResult(
    IReadOnlyList<PredictionRecord> Admitted,
    IReadOnlyList<RefusedPrediction> Refused,
    int Considered)
{
    /// <summary>True when a prediction was refused because its history could not be established.</summary>
    public bool HasUndeterminableHistory => Refused.Any(refusal => refusal.WasUndeterminable);
}

/// <summary>
/// Replays a window, admitting only what was knowable at each decision.
/// </summary>
/// <remarks>
/// <para>
/// The engine's whole job is to say no. It walks the predictions the repository holds, and for each
/// one asks a single question: could this have been made, with this evidence, at the moment it claims
/// to have been made? Everything that fails is counted and reported rather than dropped, because the
/// refusals are a result in their own right - a run that admitted four predictions out of nine hundred
/// has learned something important about the data, and a run that quietly reported a hit rate over the
/// four has learned nothing and said something false.
/// </para>
/// <para>
/// <strong>Fail closed on undeterminable history.</strong> A candidate with no record of when its
/// evidence became public is refused rather than admitted. The alternative - assume it was fine - is
/// how look-ahead bias enters a system that has a point-in-time guard: not through the guard, but
/// around it, on the rows the guard could not judge.
/// </para>
/// <para>
/// Deterministic. Given the same candidates the engine produces the same admissions in the same order,
/// so two runs over the same history are comparable and a difference between them means the history
/// changed.
/// </para>
/// <para>
/// Static, like every other decision in this system that is a pure function of its arguments -
/// <c>LimitEngine</c>, <c>AdmissionControl</c>, <c>AutonomyResolver</c>. There is no state to hold and
/// therefore nothing to configure, mock or get wrong between two calls.
/// </para>
/// </remarks>
public static class BacktestEngine
{
    /// <summary>
    /// Judges every candidate in the window. Pure with respect to its argument: no clock, no store.
    /// </summary>
    public static BacktestResult Replay(EvaluationWindow window, IReadOnlyList<PredictionCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(candidates);

        var admitted = new List<PredictionRecord>();
        var refused = new List<RefusedPrediction>();

        foreach (var candidate in candidates.OrderBy(c => c.DecidedAtUtc).ThenBy(c => c.PredictionId))
        {
            var verdict = Judge(window, candidate);

            if (verdict is not null)
            {
                refused.Add(verdict);

                continue;
            }

            admitted.Add(PredictionRecord.Create(
                candidate.PredictionId,
                candidate.Subject,
                candidate.DecidedAtUtc,
                candidate.ResolvesAtUtc,
                candidate.Direction,
                candidate.Methodology,
                candidate.EvidenceAvailableAtUtc!.Value,
                candidate.SourceReference,
                candidate.StatedProbability,
                candidate.StatedConfidence));
        }

        return new BacktestResult(admitted, refused, candidates.Count);
    }

    private static RefusedPrediction? Judge(EvaluationWindow window, PredictionCandidate candidate)
    {
        if (!window.Contains(candidate.DecidedAtUtc))
        {
            return new RefusedPrediction(
                candidate.PredictionId,
                AdmissibilityRefusal.PublishedAfterCutoff,
                $"the prediction was made at {candidate.DecidedAtUtc:O}, outside the window {window}.",
                WasUndeterminable: false);
        }

        if (candidate.EvidenceAvailableAtUtc is null)
        {
            return new RefusedPrediction(
                candidate.PredictionId,
                AdmissibilityRefusal.ProvenanceMissing,
                "the record does not say when the evidence behind this prediction became public, so " +
                "whether it was knowable at the decision cannot be established. A run may not assume " +
                "it was.",
                WasUndeterminable: true);
        }

        var cutoff = KnowledgeCutoff.At(candidate.DecidedAtUtc);

        if (!cutoff.Admits(candidate.EvidenceAvailableAtUtc.Value))
        {
            return new RefusedPrediction(
                candidate.PredictionId,
                AdmissibilityRefusal.DerivedFromInadmissibleEvidence,
                $"its latest evidence became public at {candidate.EvidenceAvailableAtUtc:O}, after the " +
                $"decision at {candidate.DecidedAtUtc:O}. Measuring it would measure hindsight.",
                WasUndeterminable: false);
        }

        if (candidate.ResolvesAtUtc <= candidate.DecidedAtUtc)
        {
            return new RefusedPrediction(
                candidate.PredictionId,
                AdmissibilityRefusal.DescribesPeriodAfterCutoff,
                $"it resolves at {candidate.ResolvesAtUtc:O}, at or before the decision at " +
                $"{candidate.DecidedAtUtc:O}, so it is a statement about the past.",
                WasUndeterminable: false);
        }

        if (candidate.StatedProbability is not null &&
            (candidate.StatedProbability.Ratio < 0m || candidate.StatedProbability.Ratio > 1m))
        {
            return new RefusedPrediction(
                candidate.PredictionId,
                AdmissibilityRefusal.ProvenanceMissing,
                $"its stated probability of {candidate.StatedProbability.Ratio} is not a probability, " +
                "so the record is wrong about something it should know.",
                WasUndeterminable: true);
        }

        return null;
    }
}
