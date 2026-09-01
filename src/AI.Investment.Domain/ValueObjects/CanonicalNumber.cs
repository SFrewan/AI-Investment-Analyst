using System.Globalization;

namespace AI.Investment.Domain.ValueObjects;

/// <summary>
/// One decimal, one string, whatever scale the value happens to carry.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> A .NET <see cref="decimal"/> remembers its scale, so
/// <c>0m</c> renders as <c>"0"</c> and <c>0.0000m</c> renders as <c>"0.0000"</c> even though the
/// two compare equal and hash equal. Scale is not part of the value; it is an artefact of how the
/// number was constructed or, once it has been stored, of the column's declared precision.
/// PostgreSQL <c>numeric(18,4)</c> returns every amount at scale four, so a value that was
/// <c>"0 USD"</c> in memory comes back as <c>"0.0000 USD"</c>.
/// </para>
/// <para>
/// <strong>Why it matters more than a display quirk.</strong> <c>ActionFingerprint</c> hashes the
/// text of a proposal's economics, and an approval token is bound to that hash. If the text moves
/// when the proposal is written to a database and read back, then a token issued against the
/// proposal a human was shown will not verify against the same proposal reloaded - every approval
/// fails, and the tempting fix is to loosen the fingerprint, which is the one thing that must not
/// happen. Canonicalising here removes the problem at its source instead.
/// </para>
/// <para>
/// <strong>The rule: significant digits, never padding.</strong> Trailing zeros after the decimal
/// point are removed; every digit that carries information is kept, up to a decimal's full scale.
/// This is deliberately scale-independent rather than fixed-width: a fixed two or four places
/// would presume a currency's minor unit (JPY has none, and a crypto pair wants more), and would
/// break again the moment a column's declared scale changed.
/// </para>
/// </remarks>
public static class CanonicalNumber
{
    /// <summary>
    /// Twenty-eight placeholders, which is the most fractional digits a decimal can hold, so no
    /// significant digit is ever truncated. '#' emits a digit only when one is present, which is
    /// what strips the padding.
    /// </summary>
    private const string Format = "0.############################";

    /// <summary>The canonical text of a decimal: invariant, unpadded, scale-independent.</summary>
    public static string Text(decimal value) =>
        value.ToString(Format, CultureInfo.InvariantCulture);
}
