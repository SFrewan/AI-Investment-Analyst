namespace AI.Investment.Domain.Analytics;

/// <summary>
/// What a measured number actually means.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Ratio"/> and <see cref="Percent"/> are separate on purpose. 0.184 and 18.4 are the
/// same growth expressed two ways, and a stored number that does not say which it is invites the
/// hundred-fold error that is the most common defect in financial analytics. Making the unit part
/// of the value forces the question to be answered once, where the number is produced.
/// </para>
/// <para>
/// Deliberately domain-neutral. These units describe barrels, patients and delivery times as
/// readily as revenue.
/// </para>
/// </remarks>
public enum UnitOfMeasure
{
    /// <summary>Never valid on a stored measurement; present so <c>default</c> names a case.</summary>
    Unknown = 0,

    /// <summary>A dimensionless proportion, where 0.184 means 18.4%.</summary>
    Ratio = 1,

    /// <summary>A proportion already scaled to percentage points, where 18.4 means 18.4%.</summary>
    Percent = 2,

    /// <summary>An amount of money, which is meaningless without its currency.</summary>
    Money = 3,

    /// <summary>A dimensionless tally of things.</summary>
    Count = 4,

    /// <summary>A duration expressed in days.</summary>
    Days = 5,
}
