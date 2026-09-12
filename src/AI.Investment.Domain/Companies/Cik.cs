using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Companies;

/// <summary>
/// The SEC's Central Index Key for a filer: ten digits, zero-padded, leading zeroes significant.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It identifies the legal entity, never the instrument.</strong> One CIK can issue several
/// securities - share classes, preferred lines, warrants - so a CIK on a <c>Security</c> would be
/// ambiguous in exactly the cases that matter. It lives here, on <c>Company</c>, and
/// <c>Security</c> reaches it only through <c>CompanyId</c>. It is not a ticker and not a trading
/// identifier: nothing routes an order by it.
/// </para>
/// <para>
/// <strong>Ten digits, and the zeroes are part of the value.</strong> EDGAR writes CIK 320193 as
/// <c>0000320193</c> and a URL path that drops the padding resolves to nothing. Storing it as a
/// number would lose exactly that, which is why this is a string and why the length is fixed rather
/// than a maximum.
/// </para>
/// <para>
/// <strong>This type stores; it does not parse vendor input.</strong>
/// <c>SecEdgarEndpoints.NormaliseCik</c> already turns <c>CIK-320193</c>, <c>320193</c> and
/// <c>0000320193</c> into one canonical form, and it stays provider-side. What arrives here is
/// expected to be canonical already, and anything else is refused rather than quietly repaired -
/// a normaliser that guesses is how a wrong issuer gets a right-looking identifier.
/// </para>
/// <para>
/// <strong>No validity interval, deliberately.</strong> Whether a CIK can change for a company is
/// not something this repository states, and the D3 decision keeps <c>Company</c> current-state.
/// If that question is ever answered, it opens its own gate rather than being retrofitted here.
/// </para>
/// </remarks>
public sealed record Cik
{
    /// <summary>A CIK is exactly this many digits once padded. Not a maximum.</summary>
    public const int Digits = 10;

    private Cik(string value) => Value = value;

    /// <summary>The canonical ten-digit form, zero-padded.</summary>
    public string Value { get; }

    /// <summary>
    /// Creates a CIK from its canonical ten-digit form.
    /// </summary>
    /// <remarks>
    /// Whitespace either side is trimmed, because that is a transport artefact rather than a
    /// different identifier. Nothing else is repaired: a short value is not padded and a prefixed
    /// one is not stripped, since both would mean accepting a shape the provider's own normaliser
    /// exists to produce.
    /// </remarks>
    public static Cik Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(nameof(value), "A CIK is required.");
        }

        var trimmed = value.Trim();

        if (trimmed.Length != Digits)
        {
            throw new DomainValidationException(
                nameof(value),
                $"A CIK is exactly {Digits} digits, zero-padded. Received '{trimmed}' "
                + $"({trimmed.Length} characters).");
        }

        foreach (var c in trimmed)
        {
            if (!char.IsAsciiDigit(c))
            {
                throw new DomainValidationException(
                    nameof(value),
                    $"A CIK contains digits only. Received '{trimmed}'.");
            }
        }

        if (long.Parse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture) == 0)
        {
            throw new DomainValidationException(
                nameof(value),
                "'0000000000' is not a CIK. A filer with no index key is not a filer, and accepting "
                + "it would let an unidentified company look identified.");
        }

        return new Cik(trimmed);
    }

    /// <summary>Creates a CIK, or reports that the value is not one. Throws nothing.</summary>
    /// <remarks>
    /// The shape <c>Ticker.TryCreate</c> established, for the same reason: a validator that reports
    /// every problem at once cannot use an exception to learn about the first.
    /// </remarks>
    public static bool TryCreate(string? value, [NotNullWhen(true)] out Cik? cik)
    {
        try
        {
            cik = Create(value!);
            return true;
        }
        catch (DomainValidationException)
        {
            cik = null;
            return false;
        }
    }

    public override string ToString() => Value;
}
