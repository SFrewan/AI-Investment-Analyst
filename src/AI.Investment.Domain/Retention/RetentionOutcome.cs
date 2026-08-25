namespace AI.Investment.Domain.Retention;

/// <summary>What a source's licensing terms require of a stored payload.</summary>
public enum RetentionOutcome
{
    /// <summary>
    /// Nothing compels deletion. The default value, deliberately.
    /// </summary>
    /// <remarks>
    /// Everywhere else in this platform the safe default is to deny. Here it is to keep, because
    /// the irreversible operation is the deletion: a payload wrongly retained can be deleted
    /// tomorrow, while a payload wrongly deleted takes an audit trail and a backtest with it. An
    /// unset value must therefore read as "keep", not as "delete".
    /// </remarks>
    Retain = 0,

    /// <summary>
    /// The licence's retention limit has been exceeded and the payload must be deleted.
    /// </summary>
    /// <remarks>
    /// A requirement, not a permission. This is the only reason this platform deletes archived
    /// evidence; there is no "delete because it is old" or "delete to save space" path.
    /// </remarks>
    DeleteRequired = 1,
}
