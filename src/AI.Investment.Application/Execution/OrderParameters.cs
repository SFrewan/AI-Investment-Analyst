using System.Globalization;
using AI.Investment.Domain.Actions;

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
    public string Describe()
    {
        var culture = CultureInfo.InvariantCulture;

        return string.Concat(
            "instrument='", Instrument,
            "', side=", Side.ToString(),
            ", quantity=", Quantity.ToString(culture),
            ", price=", Price.ToString(culture),
            " ", CurrencyCode);
    }
}
