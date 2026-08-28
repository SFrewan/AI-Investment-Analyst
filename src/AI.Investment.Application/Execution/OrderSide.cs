namespace AI.Investment.Application.Execution;

/// <summary>Which way an order goes.</summary>
/// <remarks>
/// <see cref="Unknown"/> is zero and is refused at construction. Defaulting to <c>Buy</c> would mean
/// an order assembled with a missing field commits capital rather than releasing it, which is the
/// wrong direction for a value nobody set.
/// </remarks>
public enum OrderSide
{
    /// <summary>Never valid on a real order.</summary>
    Unknown = 0,

    /// <summary>Acquire. Commits capital.</summary>
    Buy = 1,

    /// <summary>Dispose. Releases capital and realises a result.</summary>
    Sell = 2,
}
