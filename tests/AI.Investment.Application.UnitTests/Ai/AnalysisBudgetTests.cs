using AI.Investment.Application.Ai;
using AI.Investment.Domain.Exceptions;
using Xunit;

namespace AI.Investment.Application.UnitTests.Ai;

public sealed class AnalysisBudgetTests
{
    [Fact]
    public void A_fresh_budget_permits_a_call()
    {
        var budget = AnalysisBudget.Create(1m, 3);

        Assert.True(budget.TryBeginCall(out var refusal));
        Assert.Null(refusal);
        Assert.Equal(1, budget.Calls);
    }

    [Fact]
    public void The_call_ceiling_is_hard()
    {
        var budget = AnalysisBudget.Create(1m, 2);

        Assert.True(budget.TryBeginCall(out _));
        Assert.True(budget.TryBeginCall(out _));
        Assert.False(budget.TryBeginCall(out var refusal));
        Assert.Contains("Call budget exhausted", refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cost_ceiling_is_hard()
    {
        var budget = AnalysisBudget.Create(0.001m, 100);

        Assert.True(budget.TryBeginCall(out _));
        budget.RecordSpend(0.002m);

        Assert.False(budget.TryBeginCall(out var refusal));
        Assert.Contains("Cost budget exhausted", refusal!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The specialists fan out in parallel. Against an unsynchronised counter two agents starting
    /// together would each see room for one more call.
    /// </summary>
    [Fact]
    public async Task Concurrent_callers_cannot_both_claim_the_last_call()
    {
        var budget = AnalysisBudget.Create(100m, 50);

        var granted = await Task.WhenAll(
            Enumerable.Range(0, 200).Select(attempt => Task.Run(() => budget.TryBeginCall(out _))));

        Assert.Equal(50, granted.Count(allowed => allowed));
        Assert.Equal(50, budget.Calls);
    }

    /// <summary>A budget permitting no calls is a run that cannot start; say so explicitly instead.</summary>
    [Fact]
    public void A_budget_with_no_calls_is_refused() =>
        Assert.Throws<DomainValidationException>(() => AnalysisBudget.Create(1m, 0));

    [Fact]
    public void A_negative_ceiling_is_refused() =>
        Assert.Throws<DomainValidationException>(() => AnalysisBudget.Create(-1m, 1));

    [Fact]
    public void A_negative_spend_is_refused() =>
        Assert.Throws<DomainValidationException>(() => AnalysisBudget.Create(1m, 1).RecordSpend(-0.01m));
}
