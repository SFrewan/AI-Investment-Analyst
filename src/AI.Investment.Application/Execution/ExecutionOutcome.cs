using AI.Investment.Domain.Approvals;
using AI.Investment.Domain.Limits;

namespace AI.Investment.Application.Execution;

/// <summary>What happened when the platform tried to act on an opportunity.</summary>
public sealed record ExecutionOutcome
{
    private readonly List<LimitBreach> _breaches;

    private ExecutionOutcome(
        ExecutionStatus status,
        string explanation,
        VenueFill? fill,
        List<LimitBreach> breaches,
        ApprovalRefusal approvalRefusal)
    {
        Status = status;
        Explanation = explanation;
        Fill = fill;
        _breaches = breaches;
        ApprovalRefusal = approvalRefusal;
    }

    public ExecutionStatus Status { get; }

    /// <summary>Why it ended this way, in a form an operator can act on.</summary>
    public string Explanation { get; }

    /// <summary>Present only when the order was filled.</summary>
    public VenueFill? Fill { get; }

    /// <summary>Every ceiling that would have been exceeded, when limits refused it.</summary>
    public IReadOnlyList<LimitBreach> Breaches => _breaches;

    /// <summary>Why the approval could not be used, when that is what stopped it.</summary>
    public ApprovalRefusal ApprovalRefusal { get; }

    public bool Executed => Status == ExecutionStatus.Executed;

    public static ExecutionOutcome Filled(VenueFill fill)
    {
        ArgumentNullException.ThrowIfNull(fill);

        return new ExecutionOutcome(
            ExecutionStatus.Executed,
            $"Filled: {fill}",
            fill,
            [],
            ApprovalRefusal.None);
    }

    public static ExecutionOutcome Refused(ExecutionStatus status, string explanation) =>
        new(status, explanation, null, [], ApprovalRefusal.None);

    public static ExecutionOutcome RefusedByLimits(LimitVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        return new ExecutionOutcome(
            ExecutionStatus.RefusedByLimits,
            verdict.Explain(),
            null,
            verdict.Breaches.ToList(),
            ApprovalRefusal.None);
    }

    public static ExecutionOutcome RefusedByApproval(ApprovalRefusal refusal, string explanation) =>
        new(ExecutionStatus.RefusedByApproval, explanation, null, [], refusal);

    public override string ToString() => $"{Status}: {Explanation}";
}
