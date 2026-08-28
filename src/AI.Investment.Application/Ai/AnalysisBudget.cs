using System.Globalization;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Application.Ai;

/// <summary>
/// The cost and call ceiling for one analysis run, enforced by the orchestrator.
/// </summary>
/// <remarks>
/// <para>
/// Spending money is an action, and this platform does not let anything spend without a stated
/// limit. The ceiling is hard: when it is reached the run stops with
/// <c>AgentStatus.BudgetExceeded</c> rather than continuing on a best-effort basis, because a
/// budget that bends under pressure is a budget that is discovered on an invoice.
/// </para>
/// <para>
/// There is deliberately no unlimited budget. A caller that wants a large one states a large
/// number, which is a decision somebody made and a number that appears in the audit trail.
/// </para>
/// <para>
/// Guarded by a lock because the specialist agents fan out in parallel. Two agents starting at the
/// same instant against an unsynchronised counter would each see room for one more call.
/// </para>
/// </remarks>
public sealed class AnalysisBudget
{
    private readonly object _gate = new();

    private decimal _spentUsd;
    private int _calls;

    private AnalysisBudget(decimal maxCostUsd, int maxCalls)
    {
        MaxCostUsd = maxCostUsd;
        MaxCalls = maxCalls;
    }

    public decimal MaxCostUsd { get; }

    public int MaxCalls { get; }

    public decimal SpentUsd
    {
        get
        {
            lock (_gate)
            {
                return _spentUsd;
            }
        }
    }

    public int Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls;
            }
        }
    }

    public static AnalysisBudget Create(decimal maxCostUsd, int maxCalls)
    {
        if (maxCostUsd < 0m)
        {
            throw new DomainValidationException(nameof(maxCostUsd), "A cost ceiling may not be negative.");
        }

        if (maxCalls < 1)
        {
            throw new DomainValidationException(
                nameof(maxCalls),
                "A budget that permits no calls is a run that cannot start. Say so explicitly instead.");
        }

        return new AnalysisBudget(maxCostUsd, maxCalls);
    }

    /// <summary>
    /// Claims room for one more provider call, or explains why there is none.
    /// </summary>
    /// <returns>True when the call may proceed.</returns>
    public bool TryBeginCall(out string? refusal)
    {
        lock (_gate)
        {
            if (_calls >= MaxCalls)
            {
                refusal = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Call budget exhausted: {_calls} of {MaxCalls} calls already made.");

                return false;
            }

            if (_spentUsd >= MaxCostUsd)
            {
                refusal = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Cost budget exhausted: {_spentUsd:0.####} of {MaxCostUsd:0.####} USD already spent.");

                return false;
            }

            _calls++;
            refusal = null;

            return true;
        }
    }

    /// <summary>Records what a completed call actually cost.</summary>
    public void RecordSpend(decimal costUsd)
    {
        if (costUsd < 0m)
        {
            throw new DomainValidationException(nameof(costUsd), "A recorded spend may not be negative.");
        }

        lock (_gate)
        {
            _spentUsd += costUsd;
        }
    }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{SpentUsd:0.####}/{MaxCostUsd:0.####} USD, {Calls}/{MaxCalls} calls");
}
