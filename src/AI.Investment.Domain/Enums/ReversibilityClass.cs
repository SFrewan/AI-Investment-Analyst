namespace AI.Investment.Domain.Enums;

/// <summary>
/// Whether the effect of an action can be undone.
/// </summary>
/// <remarks>
/// Reversibility, not amount, is the primary axis of risk in this system. A small irreversible
/// action - a sent email, a placed order, a published listing, a signed supplier commitment -
/// deserves more scrutiny than a large reversible one such as a rebalance in a simulated
/// account. Ordering is meaningful: higher values are harder to undo.
/// </remarks>
public enum ReversibilityClass
{
    /// <summary>Can be undone completely, at no cost.</summary>
    Reversible = 0,

    /// <summary>Can be undone, but the reversal itself costs money, time or reputation.</summary>
    ReversibleWithCost = 1,

    /// <summary>Cannot be undone once performed.</summary>
    Irreversible = 2,
}
