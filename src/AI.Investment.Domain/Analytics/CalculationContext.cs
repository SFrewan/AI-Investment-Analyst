using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Analytics;

/// <summary>
/// What is being measured, on what evidence, and when the measuring happened.
/// </summary>
/// <remarks>
/// <para>
/// The separation between <see cref="Cutoff"/> and <see cref="CalculatedAtUtc"/> is what makes
/// backtesting expressible. Live, they are the same instant. Replaying 2021 from today, the cutoff
/// sits in 2021 while the calculation happens now - so the pair records both what the platform was
/// allowed to know and when it drew the conclusion, and the two can never be confused for each
/// other afterwards.
/// </para>
/// <para>
/// <see cref="IngestionSubject"/> is reused rather than a parallel analytics subject introduced.
/// A measurement and the observations it rests on must be about the same thing, and two structurally
/// identical ways of naming a subject would eventually disagree about whether they were.
/// </para>
/// </remarks>
public sealed record CalculationContext
{
    private CalculationContext(IngestionSubject subject, KnowledgeCutoff cutoff, DateTime calculatedAtUtc)
    {
        Subject = subject;
        Cutoff = cutoff;
        CalculatedAtUtc = calculatedAtUtc;
    }

    public IngestionSubject Subject { get; }

    public KnowledgeCutoff Cutoff { get; }

    public DateTime CalculatedAtUtc { get; }

    public static CalculationContext Create(
        IngestionSubject subject,
        KnowledgeCutoff cutoff,
        DateTime calculatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(cutoff);

        DateRange.EnsureUtc(calculatedAtUtc, nameof(calculatedAtUtc));

        if (calculatedAtUtc < cutoff.AsOfUtc)
        {
            throw new DomainRuleViolationException(
                "CalculationContext.CutoffPrecedesCalculation",
                $"A calculation performed at {calculatedAtUtc:O} cannot be permitted to know " +
                $"everything up to {cutoff.AsOfUtc:O}. A cutoff in the future of the calculation is " +
                "look-ahead stated as configuration.");
        }

        return new CalculationContext(subject, cutoff, calculatedAtUtc);
    }

    /// <summary>Whether evidence with this provenance was knowable at the cutoff.</summary>
    public bool Admits(Provenance provenance) => Cutoff.Admits(provenance);

    public override string ToString() => $"{Subject} as of {Cutoff}";
}
