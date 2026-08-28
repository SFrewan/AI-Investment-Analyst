using System.Globalization;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Validation;

/// <summary>
/// The period a validation run measures, and the horizon each prediction is judged over.
/// </summary>
/// <remarks>
/// <para>
/// Declared before the run and never adjusted afterwards. A window chosen once the results are known
/// is the oldest way to make a strategy look good, and it does not announce itself in the output: a
/// report that says "2024 to 2026" reads identically whether that range was picked in advance or
/// picked because it was the range that worked.
/// </para>
/// <para>
/// <see cref="Horizon"/> matters as much as the endpoints. A prediction made close to
/// <see cref="ToUtc"/> has not had time to resolve, and the honest treatment is to label it
/// unresolved and exclude it from the rates while still counting it in the sample - rather than
/// either dropping it silently or judging it early.
/// </para>
/// </remarks>
public sealed record EvaluationWindow
{
    /// <summary>The shortest window worth measuring. Below this the rates are noise with a decimal point.</summary>
    public static readonly TimeSpan MinimumSpan = TimeSpan.FromDays(1);

    private EvaluationWindow(DateTime fromUtc, DateTime toUtc, TimeSpan horizon, TimeSpan step)
    {
        FromUtc = fromUtc;
        ToUtc = toUtc;
        Horizon = horizon;
        Step = step;
    }

    /// <summary>First decision time, inclusive.</summary>
    public DateTime FromUtc { get; }

    /// <summary>End of observation, inclusive. Outcomes after this are not looked at.</summary>
    public DateTime ToUtc { get; }

    /// <summary>How long after a decision its outcome is measured.</summary>
    public TimeSpan Horizon { get; }

    /// <summary>The interval between decision times when the window is walked.</summary>
    public TimeSpan Step { get; }

    public TimeSpan Span => ToUtc - FromUtc;

    /// <summary>The last decision time whose horizon still fits inside the window.</summary>
    public DateTime LastResolvableDecisionUtc => ToUtc - Horizon;

    public static EvaluationWindow Create(DateTime fromUtc, DateTime toUtc, TimeSpan horizon, TimeSpan step)
    {
        DateRange.EnsureUtc(fromUtc, nameof(fromUtc));
        DateRange.EnsureUtc(toUtc, nameof(toUtc));

        if (toUtc - fromUtc < MinimumSpan)
        {
            throw new DomainValidationException(
                nameof(toUtc),
                $"An evaluation window must span at least {MinimumSpan}. A shorter one produces rates " +
                "whose precision is entirely spurious.");
        }

        if (horizon <= TimeSpan.Zero)
        {
            throw new DomainValidationException(
                nameof(horizon),
                "A prediction needs a horizon to be judged over. Without one there is no moment at " +
                "which it was right or wrong.");
        }

        if (step <= TimeSpan.Zero)
        {
            throw new DomainValidationException(
                nameof(step),
                "A window is walked in steps. A step of zero would stand still.");
        }

        if (horizon >= toUtc - fromUtc)
        {
            throw new DomainValidationException(
                nameof(horizon),
                $"A horizon of {horizon} does not fit inside a window of {toUtc - fromUtc}, so no " +
                "prediction in it could ever resolve.");
        }

        return new EvaluationWindow(fromUtc, toUtc, horizon, step);
    }

    /// <summary>Whether a decision at this instant falls inside the window.</summary>
    public bool Contains(DateTime instantUtc)
    {
        DateRange.EnsureUtc(instantUtc, nameof(instantUtc));

        return instantUtc >= FromUtc && instantUtc <= ToUtc;
    }

    /// <summary>Whether a decision at this instant has had time to resolve.</summary>
    public bool Resolves(DateTime decisionAtUtc)
    {
        DateRange.EnsureUtc(decisionAtUtc, nameof(decisionAtUtc));

        return decisionAtUtc <= LastResolvableDecisionUtc;
    }

    /// <summary>
    /// Every decision time in the window, in order. Deterministic: the same window always yields the
    /// same instants, which is what makes a replay comparable with the run before it.
    /// </summary>
    public IReadOnlyList<KnowledgeCutoff> DecisionTimes()
    {
        var times = new List<KnowledgeCutoff>();

        for (var at = FromUtc; at <= ToUtc; at = at.Add(Step))
        {
            times.Add(KnowledgeCutoff.At(at));
        }

        return times;
    }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{FromUtc:O} to {ToUtc:O}, horizon {Horizon}, step {Step}");
}
