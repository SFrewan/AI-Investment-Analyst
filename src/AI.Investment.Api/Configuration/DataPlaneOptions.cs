using System.ComponentModel.DataAnnotations;

namespace AI.Investment.Api.Configuration;

/// <summary>
/// What this host runs on its own, and how often.
/// </summary>
/// <remarks>
/// <para>
/// Both background activities default to <strong>off</strong>. A deployment that has not thought
/// about whether this instance should seed the registry or sweep the archive gets an instance that
/// does neither, which is the safe reading: the sweep deletes evidence, and two instances sweeping
/// the same archive is a situation worth opting into deliberately rather than inheriting.
/// </para>
/// <para>
/// Seeding is a partial exception in spirit - it only ever adds registry rows, and adds them
/// inactive - but it still writes, and a host that wrote to the database on start-up without
/// anyone asking would be a surprise.
/// </para>
/// </remarks>
public sealed class DataPlaneOptions
{
    public const string SectionName = "DataPlane";

    /// <summary>
    /// Whether this instance registers the source definitions its connectors ship with.
    /// </summary>
    /// <remarks>
    /// Registration is idempotent and leaves existing entries untouched, so running it on several
    /// instances is harmless. It is off by default because writing to a database at start-up
    /// should be a decision, not a default.
    /// </remarks>
    public bool SeedSourcesOnStartup { get; init; }

    /// <summary>Whether this instance runs the retention sweep.</summary>
    /// <remarks>
    /// <strong>Enable on exactly one instance.</strong> The sweep proposes deletions through the
    /// seam, which deduplicates by content hash, so a second instance cannot delete the same
    /// payload twice - but it would burn approval slots and audit rows discovering that. There is
    /// no distributed lock here and this option is not a substitute for one.
    /// </remarks>
    public bool RunRetentionSweep { get; init; }

    /// <summary>How long to wait between retention sweeps, in minutes.</summary>
    /// <remarks>
    /// Minutes rather than a <see cref="TimeSpan"/>, and deliberately. A duration in JSON invites
    /// values like <c>"24:00:00"</c>, which is not a parseable timespan at all - the hours
    /// component may not exceed 23, so the obvious way to write one day is silently wrong. An
    /// integer has one reading, and a bad one fails validation with a number rather than a
    /// format error.
    /// </remarks>
    [Range(5, 10080)]
    public int RetentionSweepIntervalMinutes { get; init; } = 24 * 60;

    /// <summary>How long to wait after start-up before the first sweep, in minutes.</summary>
    /// <remarks>
    /// Non-zero so that a restart loop cannot turn into a deletion loop, and so an instance that
    /// is about to fail its readiness check has a chance to do so before it starts removing
    /// evidence.
    /// </remarks>
    [Range(1, 1440)]
    public int RetentionSweepDelayMinutes { get; init; } = 5;

    /// <summary>How many payloads one sweep examines.</summary>
    /// <remarks>
    /// Bounded so a sweep ends. When it stops at this limit it says so, and the next sweep
    /// continues rather than the work being lost.
    /// </remarks>
    [Range(1, 5000)]
    public int RetentionSweepBatchSize { get; init; } = 500;

    /// <summary>The interval, as a duration.</summary>
    public TimeSpan RetentionSweepInterval => TimeSpan.FromMinutes(RetentionSweepIntervalMinutes);

    /// <summary>The start-up delay, as a duration.</summary>
    public TimeSpan RetentionSweepDelay => TimeSpan.FromMinutes(RetentionSweepDelayMinutes);
}
