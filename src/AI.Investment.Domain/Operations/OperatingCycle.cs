using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Operations;

/// <summary>
/// One pass of the operating loop, persisted as a state machine rather than run as a method.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Durable and resumable.</strong> Every stage transition is written down before the next
/// one begins, so a process that dies mid-cycle leaves a record saying exactly how far it got. A
/// worker picking the cycle up afterwards continues from the persisted stage; it does not restart,
/// and it does not re-run an effect the previous worker already performed - the seam's idempotency
/// keys are what make the second guarantee true rather than hoped for.
/// </para>
/// <para>
/// <strong>Deterministic transitions.</strong> A cycle advances to the next stage in
/// <see cref="CycleStages.Ordered"/> or stays where it is. It cannot skip, cannot go back, and
/// cannot advance at all once it has stopped. Advancing to the stage it is already in is a no-op
/// that reports so, which is what lets a retried worker be safe rather than merely lucky.
/// </para>
/// <para>
/// <strong>Concurrent workers.</strong> A cycle is leased before it is worked on, and a lease
/// expires. Two mechanisms, because they fail differently: the lease stops two healthy workers from
/// picking up the same cycle, and its expiry stops a crashed worker from holding one forever. The
/// database's own concurrency token is the third and final arbiter - see the persistence
/// configuration - because a lease check in memory cannot see a caller in another process.
/// </para>
/// <para>
/// <strong>Budget exhaustion suspends. It never truncates.</strong> A cycle that ran out of budget
/// stops where it is and escalates; it does not proceed to a decision on partial evidence, because
/// that output is indistinguishable from a complete one.
/// </para>
/// </remarks>
public sealed class OperatingCycle
{
    public const int MaxTemplateLength = 100;

    public const int MaxTriggerKeyLength = 200;

    public const int MaxReasonLength = 500;

    public const int MaxWorkerLength = 120;

    private OperatingCycle(
        Guid cycleId,
        CorrelationId correlationId,
        Capability capability,
        string templateName,
        string triggerKey,
        Guid? watchId,
        CycleBudget budget,
        CycleConsumption consumption,
        DateTime startedAtUtc)
    {
        CycleId = cycleId;
        CorrelationId = correlationId;
        Capability = capability;
        TemplateName = templateName;
        TriggerKey = triggerKey;
        WatchId = watchId;
        Budget = budget;
        Consumption = consumption;
        StartedAtUtc = startedAtUtc;
        UpdatedAtUtc = startedAtUtc;
        Status = CycleStatus.Running;
        Stage = CycleStages.First;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private OperatingCycle()
    {
        CorrelationId = null!;
        TemplateName = string.Empty;
        TriggerKey = string.Empty;
        Budget = null!;
        Consumption = null!;
    }

    public Guid CycleId { get; private set; }

    /// <summary>Threads every proposal, decision, execution and audit row of this cycle together.</summary>
    public CorrelationId CorrelationId { get; private set; }

    public Capability Capability { get; private set; }

    /// <summary>Which cycle template the trigger asked for.</summary>
    public string TemplateName { get; private set; }

    /// <summary>
    /// What made this cycle, in a form that repeats. Unique across cycles.
    /// </summary>
    /// <remarks>
    /// The deduplication key for trigger storms. A watch firing twice for the same observation
    /// produces the same key, and the store's unique index refuses the second cycle - so a volatile
    /// session produces one cycle rather than a thousand, and it does so in the database rather than
    /// in a check some caller might forget.
    /// </remarks>
    public string TriggerKey { get; private set; }

    /// <summary>The watch that fired, when a watch fired. Null for a manually started cycle.</summary>
    public Guid? WatchId { get; private set; }

    public CycleStatus Status { get; private set; }

    public CycleStage Stage { get; private set; }

    public CycleBudget Budget { get; private set; }

    public CycleConsumption Consumption { get; private set; }

    public DateTime StartedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    public DateTime? StoppedAtUtc { get; private set; }

    /// <summary>Why it stopped, when it stopped for a reason worth reading.</summary>
    public string? StoppedReason { get; private set; }

    public string? LeaseOwner { get; private set; }

    public DateTime? LeaseExpiresAtUtc { get; private set; }

    /// <summary>How many times this cycle has asked a human.</summary>
    public int EscalationCount { get; private set; }

    public bool IsRunning => Status == CycleStatus.Running;

    public bool IsFinished => Status is CycleStatus.Completed or CycleStatus.Failed;

    public TimeSpan Elapsed(DateTime nowUtc) => nowUtc - StartedAtUtc;

    public static OperatingCycle Start(
        CorrelationId correlationId,
        Capability capability,
        string templateName,
        string triggerKey,
        CycleBudget budget,
        Currency spendCurrency,
        DateTime nowUtc,
        Guid? watchId = null)
    {
        ArgumentNullException.ThrowIfNull(correlationId);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(spendCurrency);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (!Enum.IsDefined(capability))
        {
            throw new DomainValidationException(nameof(capability), $"Unrecognised capability '{capability}'.");
        }

        return new OperatingCycle(
            Guid.NewGuid(),
            correlationId,
            capability,
            Text(templateName, nameof(templateName), MaxTemplateLength,
                "A cycle must name the template it is running. A cycle nobody can name is a cycle " +
                "nobody can reproduce."),
            Text(triggerKey, nameof(triggerKey), MaxTriggerKeyLength,
                "A cycle must carry the key of what triggered it. Without one, the same observation " +
                "arriving twice starts two cycles, and the second does the work again."),
            watchId,
            budget,
            CycleConsumption.None(spendCurrency),
            nowUtc);
    }

    /// <summary>True when <paramref name="worker"/> currently holds this cycle's lease.</summary>
    public bool HoldsLease(string worker, DateTime nowUtc) =>
        LeaseOwner is not null &&
        string.Equals(LeaseOwner, worker, StringComparison.Ordinal) &&
        LeaseExpiresAtUtc is not null &&
        LeaseExpiresAtUtc > nowUtc;

    /// <summary>
    /// Claims the cycle for one worker for a bounded period.
    /// </summary>
    /// <remarks>
    /// Refuses while another worker's lease is live, and grants when that lease has expired. The
    /// expiry is what makes a crash recoverable without an operator: a worker that dies holding a
    /// lease releases it by not renewing it, and nothing has to notice the process is gone.
    /// </remarks>
    public bool TryLease(string worker, DateTime nowUtc, TimeSpan leaseFor)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        var owner = Text(worker, nameof(worker), MaxWorkerLength, "A lease must name its worker.");

        if (leaseFor <= TimeSpan.Zero)
        {
            throw new DomainValidationException(
                nameof(leaseFor),
                "A lease must expire. A lease that never expires becomes a cycle nothing can recover.");
        }

        if (!IsRunning)
        {
            return false;
        }

        var heldByAnother =
            LeaseOwner is not null &&
            !string.Equals(LeaseOwner, owner, StringComparison.Ordinal) &&
            LeaseExpiresAtUtc is not null &&
            LeaseExpiresAtUtc > nowUtc;

        if (heldByAnother)
        {
            return false;
        }

        LeaseOwner = owner;
        LeaseExpiresAtUtc = nowUtc.Add(leaseFor);
        UpdatedAtUtc = nowUtc;

        return true;
    }

    /// <summary>Gives the lease back so another worker can pick the cycle up immediately.</summary>
    public void ReleaseLease(string worker, DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (LeaseOwner is null || !string.Equals(LeaseOwner, worker, StringComparison.Ordinal))
        {
            // Not an error. A worker releasing a lease it no longer holds - because the lease
            // expired and somebody else took it - must not throw, or crash recovery would depend on
            // the crashed worker never coming back.
            return;
        }

        LeaseOwner = null;
        LeaseExpiresAtUtc = null;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>
    /// Moves to <paramref name="stage"/>. Returns false when the cycle is already there.
    /// </summary>
    /// <remarks>
    /// Returning false rather than throwing for a repeat is what makes a retried worker safe: it
    /// re-reads the cycle, sees the stage it intended to reach is already reached, and skips the
    /// work rather than doing it twice. Skipping a stage, going backwards, or advancing a stopped
    /// cycle are all defects rather than retries, and all throw.
    /// </remarks>
    public bool Advance(CycleStage stage, DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (!Enum.IsDefined(stage) || stage == CycleStage.Unknown)
        {
            throw new DomainValidationException(
                nameof(stage),
                $"'{stage}' is not a stage a cycle can be in.");
        }

        if (!IsRunning)
        {
            throw new DomainRuleViolationException(
                "OperatingCycle.NotRunning",
                $"Cycle {CycleId} is {Status} and cannot advance. A stopped cycle that could still " +
                "move would make suspension advisory.");
        }

        if (stage == Stage)
        {
            return false;
        }

        if (stage < Stage)
        {
            throw new DomainRuleViolationException(
                "OperatingCycle.NoRewind",
                $"Cycle {CycleId} is at {Stage} and cannot go back to {stage}. Replaying a stage " +
                "would repeat effects the seam has already recorded as performed.");
        }

        var next = CycleStages.Next(Stage);

        if (next != stage)
        {
            throw new DomainRuleViolationException(
                "OperatingCycle.NoSkip",
                $"Cycle {CycleId} is at {Stage}; the next stage is {next} and not {stage}. Skipping " +
                "a stage would mean deciding on evidence that was never collected.");
        }

        Stage = stage;
        UpdatedAtUtc = nowUtc;

        return true;
    }

    /// <summary>
    /// Records usage and reports whether the cycle is still inside its budget.
    /// </summary>
    /// <remarks>
    /// The consumption is recorded before the check, so an over-spend is visible in the data rather
    /// than only in the verdict. When a ceiling is reached the cycle suspends itself here - the
    /// caller cannot record the spend and then decide to carry on, because the decision is not the
    /// caller's to make.
    /// </remarks>
    public BudgetVerdict Consume(Money modelSpend, int providerCalls, int actions, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(modelSpend);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (!IsRunning)
        {
            throw new DomainRuleViolationException(
                "OperatingCycle.NotRunning",
                $"Cycle {CycleId} is {Status} and cannot consume budget.");
        }

        Consumption = Consumption.Plus(modelSpend, providerCalls, actions);
        UpdatedAtUtc = nowUtc;

        var verdict = Budget.Check(Consumption, Elapsed(nowUtc));

        if (verdict.IsExhausted)
        {
            Suspend($"budget exhausted: {verdict.Explanation}", nowUtc);
        }

        return verdict;
    }

    /// <summary>Checks the budget without recording anything. Suspends if a ceiling has been passed.</summary>
    public BudgetVerdict CheckBudget(DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        var verdict = Budget.Check(Consumption, Elapsed(nowUtc));

        if (verdict.IsExhausted && IsRunning)
        {
            Suspend($"budget exhausted: {verdict.Explanation}", nowUtc);
        }

        return verdict;
    }

    /// <summary>Stops the cycle pending something a human decides.</summary>
    public void Suspend(string reason, DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        var trimmed = Text(reason, nameof(reason), MaxReasonLength,
            "A suspension must state its reason. A cycle that stopped for no recorded reason is a " +
            "cycle nobody can decide whether to resume.");

        if (IsFinished)
        {
            throw new DomainRuleViolationException(
                "OperatingCycle.AlreadyFinished",
                $"Cycle {CycleId} is {Status} and cannot be suspended.");
        }

        Status = CycleStatus.Suspended;
        StoppedAtUtc = nowUtc;
        StoppedReason = trimmed;
        UpdatedAtUtc = nowUtc;
        LeaseOwner = null;
        LeaseExpiresAtUtc = null;
    }

    /// <summary>Records that this cycle asked a human, and stops until one answers.</summary>
    public void Escalate(string reason, DateTime nowUtc)
    {
        EscalationCount++;
        Suspend(reason, nowUtc);
    }

    /// <summary>Stops the cycle on an error it could not recover from.</summary>
    public void Fail(string reason, DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        var trimmed = Text(reason, nameof(reason), MaxReasonLength, "A failure must state its reason.");

        if (Status == CycleStatus.Completed)
        {
            throw new DomainRuleViolationException(
                "OperatingCycle.AlreadyCompleted",
                $"Cycle {CycleId} completed and cannot then fail. Rewriting the outcome of finished " +
                "work would make the record of it worthless.");
        }

        Status = CycleStatus.Failed;
        StoppedAtUtc = nowUtc;
        StoppedReason = trimmed;
        UpdatedAtUtc = nowUtc;
        LeaseOwner = null;
        LeaseExpiresAtUtc = null;
    }

    /// <summary>
    /// Restarts a suspended cycle. Requires a named authoriser, because nothing resumes on its own.
    /// </summary>
    public void Resume(string authorisedBy, DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        var who = Text(authorisedBy, nameof(authorisedBy), MaxWorkerLength,
            "Resuming a suspended cycle names who decided to. A cycle that suspended itself on a " +
            "budget and resumed itself has no budget.");

        if (Status != CycleStatus.Suspended)
        {
            throw new DomainRuleViolationException(
                "OperatingCycle.NotSuspended",
                $"Cycle {CycleId} is {Status} and is not waiting to be resumed.");
        }

        Status = CycleStatus.Running;
        StoppedAtUtc = null;
        StoppedReason = $"resumed by {who}";
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Finishes a cycle that reached the last stage.</summary>
    public void Complete(DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (!IsRunning)
        {
            throw new DomainRuleViolationException(
                "OperatingCycle.NotRunning",
                $"Cycle {CycleId} is {Status} and cannot complete.");
        }

        if (Stage != CycleStages.Last)
        {
            throw new DomainRuleViolationException(
                "OperatingCycle.IncompleteStages",
                $"Cycle {CycleId} is at {Stage} and has not reached {CycleStages.Last}. Completing " +
                "early would record work as done that was never attempted.");
        }

        Status = CycleStatus.Completed;
        StoppedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
        LeaseOwner = null;
        LeaseExpiresAtUtc = null;
    }

    public override string ToString() =>
        $"cycle {CycleId} [{Capability}/{TemplateName}] {Status} at {Stage}";

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
