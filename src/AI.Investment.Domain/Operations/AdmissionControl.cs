using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Operations;

/// <summary>Why work was refused admission.</summary>
public enum AdmissionRefusal
{
    None = 0,

    /// <summary>The limits could not be read. Nothing is admitted.</summary>
    LimitsUnavailable = 1,

    /// <summary>Too many cycles are running across the whole platform.</summary>
    GlobalConcurrency = 2,

    /// <summary>Too many cycles are running for this capability.</summary>
    CapabilityConcurrency = 3,

    /// <summary>The queue of waiting work is already at its ceiling.</summary>
    QueueDepth = 4,

    /// <summary>This watch has already started its allowance of cycles in the current window.</summary>
    WatchFiringRate = 5,
}

/// <summary>The configured ceilings on how much work may be in flight at once.</summary>
/// <remarks>
/// <para>
/// Backpressure, and the reason it is here rather than left to a queue's own behaviour: a
/// market-wide event makes thousands of watches fire within seconds, and a system that accepts all
/// of them fans out into thousands of simultaneous cycles, an enormous model bill, and a flood of
/// escalations that trains the operator to click through approvals without reading them. That last
/// one is the most common way a human-in-the-loop control fails in practice, and it is caused by
/// volume rather than by any single bad decision.
/// </para>
/// <para>
/// <see cref="FailClosed"/> exists for the same reason <c>LimitSet.FailClosed</c> does. Limits that
/// cannot be read are not "no limits"; the two have opposite safe readings.
/// </para>
/// </remarks>
public sealed record AdmissionLimits
{
    private AdmissionLimits(
        int maxConcurrentCycles,
        int maxConcurrentCyclesPerCapability,
        int maxQueuedTriggers,
        int maxFiringsPerWatchPerWindow,
        TimeSpan window,
        bool refusesEverything)
    {
        MaxConcurrentCycles = maxConcurrentCycles;
        MaxConcurrentCyclesPerCapability = maxConcurrentCyclesPerCapability;
        MaxQueuedTriggers = maxQueuedTriggers;
        MaxFiringsPerWatchPerWindow = maxFiringsPerWatchPerWindow;
        Window = window;
        RefusesEverything = refusesEverything;
    }

    public int MaxConcurrentCycles { get; }

    public int MaxConcurrentCyclesPerCapability { get; }

    public int MaxQueuedTriggers { get; }

    public int MaxFiringsPerWatchPerWindow { get; }

    /// <summary>The window the per-watch firing allowance is measured over.</summary>
    public TimeSpan Window { get; }

    /// <summary>True when these limits could not be read and therefore admit nothing.</summary>
    public bool RefusesEverything { get; }

    /// <summary>The limits to use when the configured ones cannot be read. Admits nothing.</summary>
    public static AdmissionLimits FailClosed { get; } =
        new(0, 0, 0, 0, TimeSpan.FromHours(1), true);

    public static AdmissionLimits Create(
        int maxConcurrentCycles,
        int maxConcurrentCyclesPerCapability,
        int maxQueuedTriggers,
        int maxFiringsPerWatchPerWindow,
        TimeSpan window)
    {
        if (maxConcurrentCycles < 1)
        {
            throw new DomainValidationException(
                nameof(maxConcurrentCycles),
                "A concurrency ceiling of zero admits nothing, which is a stopped system rather than " +
                "a configured one. Use FailClosed if that is what is meant.");
        }

        if (maxConcurrentCyclesPerCapability < 1)
        {
            throw new DomainValidationException(
                nameof(maxConcurrentCyclesPerCapability),
                "A per-capability concurrency ceiling of zero admits nothing.");
        }

        if (maxQueuedTriggers < 0)
        {
            throw new DomainValidationException(nameof(maxQueuedTriggers), "A queue ceiling may not be negative.");
        }

        if (maxFiringsPerWatchPerWindow < 1)
        {
            throw new DomainValidationException(
                nameof(maxFiringsPerWatchPerWindow),
                "A watch that may fire zero times per window is a disabled watch, which is expressed " +
                "on the watch rather than here.");
        }

        if (window <= TimeSpan.Zero)
        {
            throw new DomainValidationException(
                nameof(window),
                "A firing allowance needs a window to be measured over.");
        }

        return new AdmissionLimits(
            maxConcurrentCycles,
            maxConcurrentCyclesPerCapability,
            maxQueuedTriggers,
            maxFiringsPerWatchPerWindow,
            window,
            refusesEverything: false);
    }
}

/// <summary>What the platform currently has in flight, as measured before admitting more.</summary>
public sealed record AdmissionRequest(
    Capability Capability,
    Guid? WatchId,
    int RunningCycles,
    int RunningCyclesForCapability,
    int QueuedTriggers,
    int FiringsForWatchInWindow);

/// <summary>Whether more work may start, and if not, which ceiling stopped it.</summary>
public sealed record AdmissionDecision
{
    private AdmissionDecision(bool isAdmitted, AdmissionRefusal refusal, string explanation)
    {
        IsAdmitted = isAdmitted;
        Refusal = refusal;
        Explanation = explanation;
    }

    public bool IsAdmitted { get; }

    public AdmissionRefusal Refusal { get; }

    public string Explanation { get; }

    internal static AdmissionDecision Admitted { get; } =
        new(true, AdmissionRefusal.None, "Within every concurrency and rate ceiling.");

    internal static AdmissionDecision Refused(AdmissionRefusal refusal, string explanation) =>
        new(false, refusal, explanation);

    public override string ToString() => Explanation;
}

/// <summary>
/// Decides whether the platform may start more work. Pure, total and fail-closed.
/// </summary>
public static class AdmissionControl
{
    public static AdmissionDecision Admit(AdmissionRequest request, AdmissionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(limits);

        if (limits.RefusesEverything)
        {
            return AdmissionDecision.Refused(
                AdmissionRefusal.LimitsUnavailable,
                "the concurrency ceilings could not be read, so no further work is admitted. A system " +
                "that cannot determine how much it is already doing must not do more.");
        }

        if (request.RunningCycles >= limits.MaxConcurrentCycles)
        {
            return AdmissionDecision.Refused(
                AdmissionRefusal.GlobalConcurrency,
                $"{request.RunningCycles} cycles are already running against a ceiling of " +
                $"{limits.MaxConcurrentCycles}.");
        }

        if (request.RunningCyclesForCapability >= limits.MaxConcurrentCyclesPerCapability)
        {
            return AdmissionDecision.Refused(
                AdmissionRefusal.CapabilityConcurrency,
                $"{request.RunningCyclesForCapability} cycles are already running for " +
                $"{request.Capability} against a ceiling of {limits.MaxConcurrentCyclesPerCapability}.");
        }

        if (request.QueuedTriggers >= limits.MaxQueuedTriggers)
        {
            return AdmissionDecision.Refused(
                AdmissionRefusal.QueueDepth,
                $"{request.QueuedTriggers} triggers are already waiting against a ceiling of " +
                $"{limits.MaxQueuedTriggers}. Accepting more would trade a visible backlog for an " +
                "invisible one.");
        }

        if (request.WatchId is not null &&
            request.FiringsForWatchInWindow >= limits.MaxFiringsPerWatchPerWindow)
        {
            return AdmissionDecision.Refused(
                AdmissionRefusal.WatchFiringRate,
                $"watch {request.WatchId} has already started {request.FiringsForWatchInWindow} cycles " +
                $"in the last {limits.Window} against an allowance of " +
                $"{limits.MaxFiringsPerWatchPerWindow}.");
        }

        return AdmissionDecision.Admitted;
    }
}
