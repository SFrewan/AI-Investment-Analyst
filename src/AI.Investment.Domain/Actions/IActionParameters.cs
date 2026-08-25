namespace AI.Investment.Domain.Actions;

/// <summary>
/// Marker for the strongly-typed payload of an action.
/// </summary>
/// <remarks>
/// <para>
/// Each action defines its own parameter record - <c>CreateCompanyParameters</c>, and later
/// <c>PlaceOrderParameters</c> and so on - implementing this interface. The alternative, a
/// dictionary of strings on <c>ActionProposal</c>, would make every consumer parse untyped data
/// and would put the compiler out of the business of checking anything.
/// </para>
/// <para>
/// <strong>The policy engine never reads these parameters.</strong> It decides from capability,
/// action type, risk tier, economics and proposer alone. That is deliberate: policy evaluation
/// must depend on a small, fixed, reviewable set of inputs, not on the open-ended contents of
/// whatever payload an action happens to carry.
/// </para>
/// </remarks>
public interface IActionParameters
{
    /// <summary>
    /// A short human-readable summary written into the audit trail.
    /// </summary>
    /// <remarks>
    /// Must never include a secret, a credential or personal data: audit records are
    /// append-only and cannot be redacted after the fact.
    /// </remarks>
    string Describe();
}
