using System.Globalization;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Watching;

/// <summary>
/// A standing instruction to start an operating cycle when a stated, deterministic thing happens.
/// </summary>
/// <remarks>
/// <para>
/// This is what removes "the human opens the dashboard and asks". Everything about it is
/// deterministic: the condition is a comparison, the cooldown is a duration, and
/// <see cref="Evaluate"/> is a pure function of the watch, the observation and the clock. No model
/// is consulted, and an architecture test asserts that nothing in this namespace can even reference
/// the AI layer.
/// </para>
/// <para>
/// <strong>Cooldown is not polish.</strong> Without it, one volatile session produces a thousand
/// cycles, a large model bill, and a flood of escalations that trains the operator to click through
/// approvals without reading them - which is the most common way a human-in-the-loop control fails
/// in practice. It is enforced here, in the aggregate, so that a caller cannot forget it; the
/// admission control ceiling is a second, independent bound on the same failure.
/// </para>
/// <para>
/// <strong>A signal older than <see cref="MaxSignalAge"/> does not fire.</strong> A backlog being
/// replayed after an outage would otherwise start a cycle for every price move of the last two days,
/// all at once, all acting on prices that have since moved again.
/// </para>
/// </remarks>
public sealed class Watch
{
    public const int MaxNameLength = 120;

    public const int MaxTemplateLength = 100;

    /// <summary>The shortest cooldown a watch may be configured with.</summary>
    /// <remarks>
    /// A floor under an operator's optimism. A watch with no meaningful cooldown is the trigger
    /// storm, and the storm is expensive before anybody notices it.
    /// </remarks>
    public static readonly TimeSpan MinimumCooldown = TimeSpan.FromSeconds(30);

    private Watch(
        Guid watchId,
        string name,
        WatchTarget target,
        TriggerType triggerType,
        TriggerCondition condition,
        TimeSpan cooldown,
        TimeSpan maxSignalAge,
        int priority,
        Capability capability,
        string cycleTemplate,
        DateTime createdAtUtc)
    {
        WatchId = watchId;
        Name = name;
        Target = target;
        TriggerType = triggerType;
        Condition = condition;
        Cooldown = cooldown;
        MaxSignalAge = maxSignalAge;
        Priority = priority;
        Capability = capability;
        CycleTemplate = cycleTemplate;
        CreatedAtUtc = createdAtUtc;
        Enabled = true;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private Watch()
    {
        Name = string.Empty;
        Target = null!;
        Condition = null!;
        CycleTemplate = string.Empty;
    }

    public Guid WatchId { get; private set; }

    public string Name { get; private set; }

    public WatchTarget Target { get; private set; }

    public TriggerType TriggerType { get; private set; }

    public TriggerCondition Condition { get; private set; }

    /// <summary>The minimum interval between firings of this watch.</summary>
    public TimeSpan Cooldown { get; private set; }

    /// <summary>How old an observation may be and still be acted on.</summary>
    public TimeSpan MaxSignalAge { get; private set; }

    /// <summary>Queue ordering under load. Higher runs first.</summary>
    public int Priority { get; private set; }

    /// <summary>The capability the cycle this watch starts will operate under.</summary>
    public Capability Capability { get; private set; }

    /// <summary>Which operating-cycle template to start.</summary>
    public string CycleTemplate { get; private set; }

    public bool Enabled { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime? LastFiredAtUtc { get; private set; }

    public int FireCount { get; private set; }

    public string? DisabledReason { get; private set; }

    public static Watch Create(
        string name,
        WatchTarget target,
        TriggerType triggerType,
        TriggerCondition condition,
        TimeSpan cooldown,
        Capability capability,
        string cycleTemplate,
        DateTime nowUtc,
        TimeSpan? maxSignalAge = null,
        int priority = 0)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(condition);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (!Enum.IsDefined(triggerType) || triggerType == TriggerType.Unknown)
        {
            throw new DomainValidationException(
                nameof(triggerType),
                $"'{triggerType}' is not a trigger type a watch can wait for.");
        }

        if (!Enum.IsDefined(capability))
        {
            throw new DomainValidationException(nameof(capability), $"Unrecognised capability '{capability}'.");
        }

        if (cooldown < MinimumCooldown)
        {
            throw new DomainValidationException(
                nameof(cooldown),
                $"A watch cooldown may not be shorter than {MinimumCooldown}. One volatile session " +
                "against a watch with no cooldown is a thousand cycles and a bill nobody authorised.");
        }

        var age = maxSignalAge ?? TimeSpan.FromHours(1);

        if (age <= TimeSpan.Zero)
        {
            throw new DomainValidationException(
                nameof(maxSignalAge),
                "A watch must state how old an observation may be. Acting on a replayed backlog is " +
                "acting on prices that have since moved.");
        }

        return new Watch(
            Guid.NewGuid(),
            Text(name, nameof(name), MaxNameLength, "A watch must be named, so an operator can find it."),
            target,
            triggerType,
            condition,
            cooldown,
            age,
            priority,
            capability,
            Text(cycleTemplate, nameof(cycleTemplate), MaxTemplateLength,
                "A watch must say which cycle template it starts."),
            nowUtc);
    }

    /// <summary>
    /// Decides whether this observation fires this watch. Pure: it changes nothing.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="RecordFiring"/> so that deciding and acting are distinguishable,
    /// and so that a caller can evaluate a watch without committing to start a cycle - which is what
    /// admission control needs in order to refuse one on backpressure grounds without the watch
    /// believing it fired.
    /// </remarks>
    public WatchDecision Evaluate(TriggerSignal signal, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(signal);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (!Enabled)
        {
            return WatchDecision.Refused(
                WatchRefusal.Disabled,
                $"watch '{Name}' is disabled{(DisabledReason is null ? "." : ": " + DisabledReason)}");
        }

        if (signal.Type != TriggerType)
        {
            return WatchDecision.Refused(
                WatchRefusal.TypeMismatch,
                $"watch '{Name}' waits for {TriggerType} and the observation is {signal.Type}.");
        }

        if (!Target.Covers(signal.Target))
        {
            return WatchDecision.Refused(
                WatchRefusal.TargetMismatch,
                $"watch '{Name}' covers {Target} and the observation is about {signal.Target}.");
        }

        if (signal.ObservedAtUtc > nowUtc)
        {
            // A future-dated observation is a clock problem or a bad feed, and acting on it would
            // mean acting on something that has not happened.
            return WatchDecision.Refused(
                WatchRefusal.SignalInFuture,
                $"the observation is dated {signal.ObservedAtUtc:O}, which is after now.");
        }

        if (nowUtc - signal.ObservedAtUtc > MaxSignalAge)
        {
            return WatchDecision.Refused(
                WatchRefusal.SignalTooOld,
                $"the observation is {nowUtc - signal.ObservedAtUtc} old and watch '{Name}' acts on " +
                $"observations up to {MaxSignalAge}.");
        }

        // Cooldown before the condition, deliberately. The condition is the expensive question to
        // be wrong about at volume, and during a storm it is true every time.
        if (LastFiredAtUtc is not null && nowUtc - LastFiredAtUtc.Value < Cooldown)
        {
            return WatchDecision.Refused(
                WatchRefusal.WithinCooldown,
                $"watch '{Name}' last fired at {LastFiredAtUtc:O} and its cooldown is {Cooldown}.");
        }

        if (!Condition.IsMet(signal.Value, LastFiredAtUtc, CreatedAtUtc, nowUtc))
        {
            return WatchDecision.Refused(
                WatchRefusal.ConditionNotMet,
                $"watch '{Name}' waits for {Condition} and the observation does not meet it.");
        }

        return WatchDecision.Fired(
            $"watch '{Name}' fired on {signal}, starting template '{CycleTemplate}'.");
    }

    /// <summary>
    /// The deduplication key for the cycle this firing would start.
    /// </summary>
    /// <remarks>
    /// Derived from the watch and the observation's identity, so the same observation delivered
    /// twice produces the same key. The cycle store's unique index on it is what turns "the feed
    /// resent the last ten minutes" into one cycle rather than a hundred, and it does so in the
    /// database rather than in a check some caller might skip.
    /// </remarks>
    public string FiringKeyFor(TriggerSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"watch:{WatchId:d}:{signal.Type}:{signal.Target}:{signal.ObservedAtUtc:O}");
    }

    /// <summary>Records that this watch started a cycle. Starts the cooldown.</summary>
    public void RecordFiring(DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        LastFiredAtUtc = nowUtc;
        FireCount++;
    }

    /// <summary>Switches the watch off. The circuit breaker an operator reaches for.</summary>
    public void Disable(string reason, DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        Enabled = false;
        DisabledReason = Text(reason, nameof(reason), MaxNameLength,
            "Disabling a watch records why, so the next person knows whether to turn it back on.");
    }

    /// <summary>
    /// Puts a scheduled watch on a different interval, leaving everything else alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A schedule is a statement about how often something is worth looking at, and that is a
    /// judgement an operator revises - after a market changes, or to prove a pipeline without
    /// waiting a day for it. Revising it is an ordinary domain change and goes through the seam
    /// like any other: the write guard permits a watch only its firing record without an
    /// authorisation window, so this cannot be persisted outside one.
    /// </para>
    /// <para>
    /// <strong>Only the interval moves.</strong> <see cref="CreatedAtUtc"/>,
    /// <see cref="LastFiredAtUtc"/> and <see cref="FireCount"/> are untouched, so the watch's
    /// history survives and the next firing is still measured from when it last fired. Rewriting
    /// those would be forging the record of what ran, which is the one thing a reschedule must
    /// never do. <see cref="Cooldown"/> is untouched too: it bounds a storm and is a separate
    /// decision from how often the schedule comes round.
    /// </para>
    /// <para>
    /// Refused for any trigger type but <see cref="TriggerType.Schedule"/>. A price-move or
    /// threshold watch waits for a comparison rather than for time, and giving it an interval
    /// would produce a condition it can never meet.
    /// </para>
    /// </remarks>
    public void Reschedule(TimeSpan interval, DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (TriggerType != TriggerType.Schedule)
        {
            throw new DomainRuleViolationException(
                "Watch.RescheduleRequiresSchedule",
                $"Watch '{Name}' waits for {TriggerType}, which is not a schedule. An interval " +
                "would give it a condition it can never meet.");
        }

        // Every() refuses a zero or negative interval, so the same rule that governs creation
        // governs this. It is not restated here, because two copies of a rule eventually disagree.
        Condition = TriggerCondition.Every(interval);
    }

    /// <summary>Switches the watch back on and clears the recorded reason.</summary>
    public void Enable(DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        Enabled = true;
        DisabledReason = null;
    }

    public override string ToString() =>
        $"watch '{Name}' [{TriggerType} on {Target}] -> {CycleTemplate}";

    private static string Text(string? value, string parameterName, int maxLength, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(parameterName, message);
        }

        var trimmed = value.Trim();

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
