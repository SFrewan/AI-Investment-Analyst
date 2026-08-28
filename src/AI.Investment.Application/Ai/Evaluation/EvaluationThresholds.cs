using System.Globalization;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Application.Ai.Evaluation;

/// <summary>
/// The bar the AI layer must clear. Below it, the phase does not end.
/// </summary>
/// <remarks>
/// <para>
/// These four numbers are the exit criterion of the AI phase, stated as something a test can fail
/// rather than something a person can judge. That framing is the point: "the agents seem good
/// enough" is exactly the assessment that drifts, and drifts in one direction, under schedule
/// pressure.
/// </para>
/// <para>
/// <see cref="MinExpectationAccuracy"/> is the one that measures the controls rather than the
/// model. A run where every answer parses, everything is grounded, and the deliberately fabricated
/// cases are accepted anyway would score perfectly on the other three.
/// </para>
/// </remarks>
public sealed record EvaluationThresholds
{
    private EvaluationThresholds(
        decimal minSchemaValidity,
        decimal minGroundedness,
        decimal minStability,
        decimal minExpectationAccuracy)
    {
        MinSchemaValidity = minSchemaValidity;
        MinGroundedness = minGroundedness;
        MinStability = minStability;
        MinExpectationAccuracy = minExpectationAccuracy;
    }

    /// <summary>
    /// The agreed bar for Phase 4: every answer parses, everything quoted is grounded, repeated runs
    /// agree, and every case behaves as the scenario says it must.
    /// </summary>
    /// <remarks>
    /// All four are 1.0, and that is defensible only because the provider in this phase is
    /// deterministic and offline. When a sampling provider is wired up these come down to measured
    /// values - and the number they come down to is a decision recorded in the phase document, not
    /// an adjustment made to get a build green.
    /// </remarks>
    public static EvaluationThresholds Phase4 { get; } = new(1.0m, 1.0m, 1.0m, 1.0m);

    public decimal MinSchemaValidity { get; }

    public decimal MinGroundedness { get; }

    public decimal MinStability { get; }

    public decimal MinExpectationAccuracy { get; }

    public static EvaluationThresholds Create(
        decimal minSchemaValidity,
        decimal minGroundedness,
        decimal minStability,
        decimal minExpectationAccuracy) =>
        new(
            Rate(minSchemaValidity, nameof(minSchemaValidity)),
            Rate(minGroundedness, nameof(minGroundedness)),
            Rate(minStability, nameof(minStability)),
            Rate(minExpectationAccuracy, nameof(minExpectationAccuracy)));

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"schema>={MinSchemaValidity:0.###} grounded>={MinGroundedness:0.###} " +
            $"stable>={MinStability:0.###} expected>={MinExpectationAccuracy:0.###}");

    private static decimal Rate(decimal value, string parameterName) =>
        value is >= 0m and <= 1m
            ? value
            : throw new DomainValidationException(parameterName, "A threshold must be between 0 and 1.");
}
