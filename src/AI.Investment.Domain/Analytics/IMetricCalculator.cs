using AI.Investment.Domain.Sources;

namespace AI.Investment.Domain.Analytics;

/// <summary>
/// Identity and versioning shared by every calculator, whatever it consumes.
/// </summary>
/// <remarks>
/// Separated from the generic interface so calculators can be catalogued, listed and reported on
/// without the listing code needing to know the shape of their inputs.
/// </remarks>
public interface IMetricCalculator
{
    /// <summary>What this calculator measures.</summary>
    MetricId Metric { get; }

    /// <summary>Its identity as a producer of evidence, recorded in every result's provenance.</summary>
    SourceId CalculatorId { get; }

    /// <summary>Which revision of the formula this implementation is.</summary>
    CalculationVersion Version { get; }

    /// <summary>The unit every result will carry.</summary>
    UnitOfMeasure Unit { get; }
}

/// <summary>
/// A deterministic measurement: same inputs, same cutoff, same number, every time.
/// </summary>
/// <remarks>
/// <para>
/// The inputs are a type parameter rather than a fixed shape because the terms differ per metric -
/// two revenue figures for growth, a price series for volatility - and flattening them into a
/// dictionary would move the question of which terms are required from the compiler to runtime.
/// </para>
/// <para>
/// Synchronous, and free of any clock or store. Everything time-dependent arrives in the context,
/// which is what makes a calculator testable without a database and replayable at any cutoff.
/// </para>
/// </remarks>
/// <typeparam name="TInputs">The terms this formula requires.</typeparam>
public interface IMetricCalculator<in TInputs> : IMetricCalculator
{
    /// <summary>Measures, or states why it cannot.</summary>
    CalculationOutcome Calculate(CalculationContext context, TInputs inputs);
}
