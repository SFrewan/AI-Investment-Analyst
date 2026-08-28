using AI.Investment.Domain.Operations;

namespace AI.Investment.Application.Abstractions;

/// <summary>Supplies the budget a cycle of a given template runs under.</summary>
/// <remarks>
/// Per template rather than global, because the templates differ in what they legitimately cost: a
/// scheduled freshness check and a full analysis of a new filing are not the same amount of work,
/// and one budget covering both is either too tight for the second or meaningless for the first.
/// An implementation that cannot read its configuration must return the most restrictive budget it
/// can rather than a permissive default.
/// </remarks>
public interface ICycleBudgetProvider
{
    Task<CycleBudget> GetAsync(string templateName, CancellationToken cancellationToken = default);
}
