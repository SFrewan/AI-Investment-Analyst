using AI.Investment.Domain.Actions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Execution;

/// <summary>
/// The typed payload of an execution proposal, and the text the approver's fingerprint is taken
/// over.
/// </summary>
/// <remarks>
/// <see cref="Describe"/> is not a log line. It is one of the components hashed into the
/// <c>ActionFingerprint</c> an approval token is bound to, so every field that changes what would
/// actually happen must appear in it - otherwise a token issued for one order would authorise a
/// different one with the same capability, type and exposure.
/// </remarks>
public sealed record OrderParameters(
    string Instrument,
    OrderSide Side,
    decimal Quantity,
    decimal Price,
    string CurrencyCode) : IActionParameters
{
    /// <remarks>
    /// The two decimals go through <see cref="CanonicalNumber"/> rather than a bare
    /// <c>ToString</c>, for the reason this type's own remarks give: the text is hashed, so it
    /// must be a function of the values and not of the scale they happen to carry. A quantity
    /// read back from a numeric column arrives padded, and padding would move the fingerprint.
    /// </remarks>
    public string Describe() =>
        string.Concat(
            "instrument='", Instrument,
            "', side=", Side.ToString(),
            ", quantity=", CanonicalNumber.Text(Quantity),
            ", price=", CanonicalNumber.Text(Price),
            " ", CurrencyCode);
}
