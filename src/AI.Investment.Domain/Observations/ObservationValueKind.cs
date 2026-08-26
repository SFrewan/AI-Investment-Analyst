namespace AI.Investment.Domain.Observations;

/// <summary>
/// The shape of an observed value, in the small set the platform stores canonically.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately narrow. A <see cref="Evidence.Claim{T}"/> can hold any type, which is right in
/// memory and impossible in a table: an open generic cannot be mapped, and a column per type would
/// grow with every normaliser. Four kinds cover what normalisation actually produces - a name, a
/// number, a flag, a date - and anything else is a sign that a value has structure that should be
/// several observations rather than one.
/// </para>
/// <para>
/// <see cref="Unknown"/> is the default so that an unset value never reads as valid text.
/// </para>
/// </remarks>
public enum ObservationValueKind
{
    Unknown = 0,
    Text = 1,
    Number = 2,
    Boolean = 3,
    Timestamp = 4,
}
