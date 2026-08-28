using System.ComponentModel.DataAnnotations;

namespace AI.Investment.Infrastructure.Configuration;

/// <summary>
/// What a validation run measures, and against what. Declared in configuration, before the run.
/// </summary>
/// <remarks>
/// <para>
/// The window, the horizon, the event threshold and the benchmark are the four choices that decide
/// what a validation result means, and all four can be used to manufacture a favourable one. They
/// live here - in configuration under change control, with a declaration date - rather than as
/// arguments an operator supplies at the moment of running, so that changing them is a reviewable act
/// that leaves a trace, and so that a report can state which values it used and be checked against
/// them.
/// </para>
/// <para>
/// <see cref="BenchmarkDeclaredAtUtc"/> is the one field that is awkward on purpose. A run refuses to
/// proceed if the benchmark was declared after it began, so moving the benchmark forwards to match a
/// result makes the run fail rather than improve.
/// </para>
/// </remarks>
public sealed class ValidationOptions
{
    public const string SectionName = "Validation";

    /// <summary>First decision time measured, inclusive.</summary>
    public DateTime FromUtc { get; init; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>End of observation, inclusive.</summary>
    public DateTime ToUtc { get; init; } = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>How long after a decision its outcome is measured.</summary>
    public TimeSpan Horizon { get; init; } = TimeSpan.FromDays(30);

    /// <summary>The interval between decision times when the window is walked.</summary>
    public TimeSpan Step { get; init; } = TimeSpan.FromDays(1);

    /// <summary>The realised move, as a ratio, at or above which the event counts as having happened.</summary>
    [Range(-1, 10)]
    public decimal EventThresholdRatio { get; init; } = 0.00m;

    /// <summary>The observation attribute prices are read from, for the subjects and the benchmark.</summary>
    [Required]
    public string PriceAttribute { get; init; } = "security.close";

    /// <summary>Human-readable name of the naive benchmark.</summary>
    [Required]
    public string BenchmarkName { get; init; } = "index buy-and-hold";

    /// <summary>The subject kind of the index proxy held by the benchmark.</summary>
    [Required]
    public string BenchmarkSubjectKind { get; init; } = "Security";

    /// <summary>The identifier of the index proxy held by the benchmark.</summary>
    [Required]
    public string BenchmarkSubjectIdentifier { get; init; } = "SPY";

    /// <summary>Starting capital, used identically by both sides.</summary>
    [Range(0.01, 1000000000)]
    public decimal BenchmarkInitialCapital { get; init; } = 100000m;

    /// <summary>The currency the comparison is denominated in.</summary>
    [Required]
    public string Currency { get; init; } = "USD";

    /// <summary>Trading cost per leg, as a ratio, charged to the system and the benchmark alike.</summary>
    [Range(0, 1)]
    public decimal CostPerTradeRatio { get; init; } = 0.001m;

    /// <summary>
    /// When the benchmark above was fixed. A run that began before this refuses to produce numbers.
    /// </summary>
    public DateTime BenchmarkDeclaredAtUtc { get; init; } = new(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc);
}
