namespace AI.Investment.Domain.Autonomy;

/// <summary>
/// Where an action's exposure sits relative to the grant that would cover it.
/// </summary>
/// <remarks>
/// <para>
/// One of the five dimensions autonomy is resolved from. It is expressed relative to the grant
/// rather than as absolute bands, because absolute bands would have to be denominated in some
/// currency, and this platform has no exchange rate anywhere in it. A band of "up to ten thousand"
/// that silently means dollars is the same silent currency coercion <c>Money</c> exists to prevent.
/// </para>
/// <para>
/// <see cref="Incomparable"/> is the reason this is an enum rather than a boolean. A ceiling in one
/// currency and an exposure in another is not "over" and not "under" - it is a question that cannot
/// be answered, and the only safe reading of an unanswerable question is refusal.
/// </para>
/// </remarks>
public enum ExposureBand
{
    /// <summary>Not determined. Denies.</summary>
    Unknown = 0,

    /// <summary>No exposure at all.</summary>
    None = 1,

    /// <summary>Within the ceiling the grant names.</summary>
    Within = 2,

    /// <summary>Above the ceiling the grant names. Escalates rather than executing.</summary>
    Above = 3,

    /// <summary>The ceiling and the exposure are in different currencies. Denies.</summary>
    Incomparable = 4,
}
