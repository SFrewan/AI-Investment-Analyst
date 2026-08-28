using System.Globalization;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Shadow;

namespace AI.Investment.Domain.Validation;

/// <summary>One occasion on which a higher autonomy level would have decided differently.</summary>
/// <param name="ProposalId">The proposal both decisions were about.</param>
/// <param name="RecordedAtUtc">When the measurement was taken.</param>
/// <param name="ActualOutcome">What the gate actually answered.</param>
/// <param name="ShadowOutcome">What it would have answered one level up.</param>
/// <param name="Label">How the underlying prediction turned out, when that is known.</param>
public sealed record ShadowDivergence(
    Guid ProposalId,
    DateTime RecordedAtUtc,
    PolicyOutcome ActualOutcome,
    PolicyOutcome ShadowOutcome,
    OutcomeLabel Label);

/// <summary>
/// What the shadow measurements say, and - more importantly - what they do not.
/// </summary>
/// <remarks>
/// <para>
/// Phase 6 recorded, for every gated action, what the same gate would have answered one autonomy
/// level higher. Those records are the evidence that would justify raising autonomy, and this is the
/// arithmetic over them. It is arithmetic and nothing else: no proposal is re-evaluated here, no
/// effect is invoked, and there is no code path from a shadow record to an execution. Autonomy is not
/// raised by this phase and cannot be.
/// </para>
/// <para>
/// <strong>An agreement rate is not evidence of anything on its own.</strong> If the shadow level
/// agrees with the real one on every occasion, the measurement has shown that raising autonomy would
/// change nothing - which is a finding about the policy, not about whether the system is any good.
/// The interesting number is the divergence rate, and it only becomes evidence when the divergent
/// occasions have known outcomes. Where they do not, <see cref="DivergenceHitRate"/> says so rather
/// than reporting a number, because "higher autonomy would have acted more often" reads like an
/// argument for higher autonomy and is not one.
/// </para>
/// </remarks>
public sealed record ShadowComparisonResult
{
    /// <summary>Below this, the rates are not worth printing.</summary>
    public const int MinimumSample = 20;

    private ShadowComparisonResult(
        int total,
        int agreements,
        int shadowWouldHaveExecutedAndActualDidNot,
        int actualExecutedAndShadowWouldNot,
        Measurement agreementRate,
        Measurement divergenceRate,
        Measurement divergenceHitRate,
        IReadOnlyList<ShadowDivergence> divergences)
    {
        Total = total;
        Agreements = agreements;
        ShadowWouldHaveExecutedAndActualDidNot = shadowWouldHaveExecutedAndActualDidNot;
        ActualExecutedAndShadowWouldNot = actualExecutedAndShadowWouldNot;
        AgreementRate = agreementRate;
        DivergenceRate = divergenceRate;
        DivergenceHitRate = divergenceHitRate;
        Divergences = divergences;
    }

    public int Total { get; }

    public int Agreements { get; }

    public int DivergenceCount => Total - Agreements;

    /// <summary>Occasions a higher level would have acted on and the real one did not.</summary>
    public int ShadowWouldHaveExecutedAndActualDidNot { get; }

    /// <summary>Occasions the real level acted on and a higher one would not have. Rare, and worth knowing.</summary>
    public int ActualExecutedAndShadowWouldNot { get; }

    public Measurement AgreementRate { get; }

    public Measurement DivergenceRate { get; }

    /// <summary>
    /// Of the occasions a higher level would have acted on and the real one did not, the share that
    /// would have turned out right. The only number here that bears on whether autonomy should rise.
    /// </summary>
    public Measurement DivergenceHitRate { get; }

    public IReadOnlyList<ShadowDivergence> Divergences { get; }

    public static ShadowComparisonResult From(
        IEnumerable<ShadowDecision> decisions,
        IReadOnlyDictionary<Guid, OutcomeLabel> labelsByProposal,
        int minimumSample = MinimumSample)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(labelsByProposal);

        if (minimumSample < 1)
        {
            throw new DomainValidationException(
                nameof(minimumSample),
                "A minimum of zero would let one shadow record be reported as an agreement rate.");
        }

        var material = decisions.ToList();
        var agreements = 0;
        var wouldHaveActed = 0;
        var actualActed = 0;
        var divergences = new List<ShadowDivergence>();

        foreach (var decision in material)
        {
            if (decision.Agreed)
            {
                agreements++;

                continue;
            }

            var label = labelsByProposal.TryGetValue(decision.ProposalId, out var found)
                ? found
                : OutcomeLabel.Unavailable;

            divergences.Add(new ShadowDivergence(
                decision.ProposalId,
                decision.RecordedAtUtc,
                decision.ActualOutcome,
                decision.ShadowOutcome,
                label));

            if (decision.ShadowOutcome == PolicyOutcome.Execute &&
                decision.ActualOutcome != PolicyOutcome.Execute)
            {
                wouldHaveActed++;
            }

            if (decision.ActualOutcome == PolicyOutcome.Execute &&
                decision.ShadowOutcome != PolicyOutcome.Execute)
            {
                actualActed++;
            }
        }

        var judged = divergences
            .Where(d => d.ShadowOutcome == PolicyOutcome.Execute && d.ActualOutcome != PolicyOutcome.Execute)
            .Where(d => d.Label is OutcomeLabel.TruePositive or OutcomeLabel.FalsePositive)
            .ToList();

        return new ShadowComparisonResult(
            material.Count,
            agreements,
            wouldHaveActed,
            actualActed,
            Rate(agreements, material.Count, minimumSample, "shadow/actual agreement rate"),
            Rate(material.Count - agreements, material.Count, minimumSample, "shadow/actual divergence rate"),
            judged.Count == 0
                ? Measurement.Unavailable(
                    wouldHaveActed == 0
                        ? "a higher autonomy level would not have acted on any occasion the real one " +
                          "declined, so there is nothing to judge."
                        : $"{wouldHaveActed} occasions would have been acted on at a higher level, and " +
                          "none of them has a recorded outcome. Without outcomes, 'it would have acted " +
                          "more often' is a description of the policy rather than evidence about the " +
                          "system.")
                : judged.Count < minimumSample
                    ? Measurement.Insufficient(judged.Count, minimumSample)
                    : Measurement.Measured(
                        judged.Count(d => d.Label == OutcomeLabel.TruePositive) / (decimal)judged.Count,
                        judged.Count,
                        "share of the extra actions a higher autonomy level would have taken that " +
                        "turned out right"),
            divergences);
    }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Total} shadow measurements, {Agreements} agreements, {DivergenceCount} divergences");

    private static Measurement Rate(int numerator, int denominator, int minimum, string name)
    {
        if (denominator == 0)
        {
            return Measurement.Unavailable(
                $"{name}: no shadow measurements were recorded in the window.");
        }

        return denominator < minimum
            ? Measurement.Insufficient(denominator, minimum)
            : Measurement.Measured((decimal)numerator / denominator, denominator, name);
    }
}
