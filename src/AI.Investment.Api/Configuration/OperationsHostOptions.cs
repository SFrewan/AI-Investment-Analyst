using System.ComponentModel.DataAnnotations;

namespace AI.Investment.Api.Configuration;

/// <summary>
/// What this instance runs on its own for continuous operation.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Off by default.</strong> Unattended operation does not begin because a host happened to
/// start: somebody turns it on, on the instances they mean to run it. That is the same choice the
/// retention sweep makes, and for a stronger reason - this is the loop that proposes actions.
/// </para>
/// <para>
/// The cycle runner and the outbox dispatcher are separately switchable because they fail
/// differently and are wanted in different places. An installation may well want several instances
/// delivering queued messages and exactly one running cycles.
/// </para>
/// </remarks>
public sealed class OperationsHostOptions
{
    public const string SectionName = "OperationsHost";

    /// <summary>Whether this instance advances operating cycles.</summary>
    public bool RunCycles { get; init; }

    /// <summary>Whether this instance delivers queued messages.</summary>
    public bool RunOutboxDispatcher { get; init; }

    /// <summary>How long after start-up the first pass runs.</summary>
    /// <remarks>
    /// A delay rather than an immediate start, so a rolling deployment does not have every instance
    /// waking up at the same instant on the same work.
    /// </remarks>
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromSeconds(30);

    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan CycleInterval { get; init; } = TimeSpan.FromSeconds(30);

    [Range(1, 100)]
    public int CycleBatchSize { get; init; } = 4;

    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan OutboxInterval { get; init; } = TimeSpan.FromSeconds(15);

    [Range(1, 500)]
    public int OutboxBatchSize { get; init; } = 50;
}
