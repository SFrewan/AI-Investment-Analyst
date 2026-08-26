using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Ingestion;

/// <summary>
/// SHA-256 of a retrieved payload, lower-case hexadecimal.
/// </summary>
/// <remarks>
/// <para>
/// The raw-response archive is content-addressed, and this is the address. Phase 2's exit
/// criterion is that any analysis replays byte-identically from stored raw responses, which
/// requires that the bytes an analysis saw can be produced again exactly. Naming a payload by its
/// own hash makes that checkable rather than assumed: a payload that has been altered no longer
/// answers to the name the claim recorded.
/// </para>
/// <para>
/// It also makes the archive naturally deduplicating. A daily poll that returns an unchanged
/// document stores one copy and records two retrievals, which is both cheaper and a more accurate
/// account of what happened.
/// </para>
/// <para>
/// SHA-256 is chosen for collision resistance, not for secrecy. Nothing here is a security
/// control and no payload is authenticated by its hash.
/// </para>
/// </remarks>
public sealed record ContentHash
{
    /// <summary>SHA-256 produces 32 bytes, so 64 hexadecimal characters.</summary>
    public const int HexLength = 64;

    private ContentHash(string value) => Value = value;

    public string Value { get; }

    /// <summary>Hashes the payload.</summary>
    /// <remarks>
    /// Takes a span rather than an array, and deliberately offers no array overload: a
    /// <c>byte[]</c> converts implicitly, while two overloads would make a collection expression
    /// such as <c>Compute([])</c> ambiguous.
    /// </remarks>
    public static ContentHash Compute(ReadOnlySpan<byte> payload)
    {
        var digest = SHA256.HashData(payload);

        return new ContentHash(Convert.ToHexString(digest).ToLowerInvariant());
    }

    /// <summary>Parses a stored hash. Normalises case; rejects anything that is not 64 hex digits.</summary>
    public static ContentHash Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(nameof(value), "A content hash is required.");
        }

        var normalised = value.Trim().ToLowerInvariant();

        if (normalised.Length != HexLength)
        {
            throw new DomainValidationException(
                nameof(value),
                $"A SHA-256 content hash is exactly {HexLength} hexadecimal characters. " +
                $"Received {normalised.Length}.");
        }

        foreach (var c in normalised)
        {
            if (!char.IsAsciiDigit(c) && (c < 'a' || c > 'f'))
            {
                throw new DomainValidationException(
                    nameof(value),
                    $"A content hash may contain only hexadecimal characters. Received '{value}'.");
            }
        }

        return new ContentHash(normalised);
    }

    /// <summary>
    /// Parses a stored hash, returning false instead of throwing when it is not one.
    /// </summary>
    /// <remarks>
    /// For callers reading names they did not write - the archive walking its own directories, for
    /// instance, where an interrupted write leaves a temporary file by design. Skipping a
    /// non-hash is the expected path there, and expected paths should not be exceptions.
    /// <see cref="Create"/> remains the right choice anywhere a malformed value means something is
    /// actually wrong.
    /// </remarks>
    public static bool TryCreate(string? value, [NotNullWhen(true)] out ContentHash? hash)
    {
        hash = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalised = value.Trim().ToLowerInvariant();

        if (normalised.Length != HexLength)
        {
            return false;
        }

        foreach (var c in normalised)
        {
            if (!char.IsAsciiDigit(c) && (c < 'a' || c > 'f'))
            {
                return false;
            }
        }

        hash = new ContentHash(normalised);

        return true;
    }

    /// <summary>The leading characters, for logs and identifiers that humans read.</summary>
    public string Abbreviated => Value[..12];

    public override string ToString() => Value;
}
