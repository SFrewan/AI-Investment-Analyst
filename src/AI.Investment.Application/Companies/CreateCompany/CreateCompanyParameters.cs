using AI.Investment.Domain.Actions;

namespace AI.Investment.Application.Companies.CreateCompany;

/// <summary>
/// The typed payload carried by the <c>company.create</c> action proposal.
/// </summary>
/// <remarks>
/// Implements <see cref="IActionParameters"/> so the proposal stays strongly typed rather than
/// degenerating into a dictionary of strings. The policy engine never reads this - it decides
/// from capability, risk tier, economics and proposer alone.
/// </remarks>
public sealed record CreateCompanyParameters(string Name) : IActionParameters
{
    /// <summary>
    /// Audit summary. Contains only the identifying fields; free-text description and
    /// classification are omitted because audit rows are permanent and unredactable, and there
    /// is no benefit to copying arbitrary caller text into them.
    /// </summary>
    public string Describe() => $"name='{Name}'";
}
