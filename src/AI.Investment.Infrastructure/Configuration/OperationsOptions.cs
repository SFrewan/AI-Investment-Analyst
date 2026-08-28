using System.ComponentModel.DataAnnotations;
using AI.Investment.Domain.Enums;

namespace AI.Investment.Infrastructure.Configuration;

/// <summary>
/// The ceilings and cadences of unattended operation.
/// </summary>
/// <remarks>
/// <para>
/// Every value here has a restrictive default, so an installation that has configured nothing runs
/// slowly and narrowly rather than quickly and broadly. The failure this guards against is a
/// deployment where somebody forgot the section and the platform helpfully assumed generous limits.
/// </para>
/// <para>
/// Read through <c>IOptionsMonitor</c> at evaluation time rather than bound once at start-up, so
/// that lowering a ceiling during an incident takes effect without a restart. Raising one still
/// requires a deployment somebody reviewed, because it is stored in configuration under change
/// control rather than settable through the API.
/// </para>
/// </remarks>
public sealed class OperationsOptions
{
    public const string SectionName = "Operations";

    /// <summary>How this instance names itself when it leases a cycle or a message.</summary>
    [Required]
    public string WorkerName { get; init; } = "operations-worker";

    /// <summary>The most cycles that may run at once, across every capability.</summary>
    [Range(1, 1000)]
    public int MaxConcurrentCycles { get; init; } = 4;

    /// <summary>The most cycles that may run at once for any one capability.</summary>
    [Range(1, 1000)]
    public int MaxConcurrentCyclesPerCapability { get; init; } = 2;

    /// <summary>The most triggers that may be waiting before new ones are refused.</summary>
    [Range(0, 100000)]
    public int MaxQueuedTriggers { get; init; } = 100;

    /// <summary>The most cycles one watch may start inside <see cref="FiringWindow"/>.</summary>
    [Range(1, 10000)]
    public int MaxFiringsPerWatchPerWindow { get; init; } = 6;

    /// <summary>The window the per-watch firing allowance is measured over.</summary>
    public TimeSpan FiringWindow { get; init; } = TimeSpan.FromHours(1);

    /// <summary>The default wall-clock ceiling on one cycle.</summary>
    public TimeSpan CycleMaxWallClock { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The default spend ceiling on one cycle, in <see cref="BudgetCurrency"/>.</summary>
    [Range(0, 1000000)]
    public decimal CycleMaxModelSpend { get; init; } = 1.00m;

    /// <summary>The currency every cycle budget is denominated in.</summary>
    [Required]
    public string BudgetCurrency { get; init; } = "USD";

    /// <summary>The default provider-call ceiling on one cycle.</summary>
    [Range(0, 100000)]
    public int CycleMaxProviderCalls { get; init; } = 50;

    /// <summary>The default ceiling on how many actions one cycle may take.</summary>
    [Range(0, 1000)]
    public int CycleMaxActions { get; init; } = 1;

    /// <summary>How long a worker holds a queued message before another may take it.</summary>
    public TimeSpan OutboxLeaseDuration { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The first retry delay. Doubles on each subsequent attempt.</summary>
    public TimeSpan OutboxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How many times a message is attempted before it is abandoned and escalated.</summary>
    [Range(1, 100)]
    public int OutboxMaxAttempts { get; init; } = 8;

    /// <summary>Per-template budget overrides, keyed by template name.</summary>
    public IReadOnlyList<CycleBudgetOptions> Budgets { get; init; } = [];
}

/// <summary>A budget override for one cycle template.</summary>
/// <remarks>
/// Per template because the templates differ in what they legitimately cost, and one budget covering
/// a freshness check and a full analysis is either too tight for the second or meaningless for the
/// first. A template with no entry gets the defaults above, which are the restrictive ones.
/// </remarks>
public sealed class CycleBudgetOptions
{
    [Required]
    public string Template { get; init; } = string.Empty;

    public TimeSpan? MaxWallClock { get; init; }

    public decimal? MaxModelSpend { get; init; }

    public int? MaxProviderCalls { get; init; }

    public int? MaxActions { get; init; }

    /// <summary>The capability this template's cycles operate under, for documentation only.</summary>
    public Capability? Capability { get; init; }
}
