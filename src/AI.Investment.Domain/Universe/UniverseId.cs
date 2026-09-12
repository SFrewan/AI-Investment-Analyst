using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Universe;

/// <summary>
/// The identity of one sealed universe version: its content fingerprint.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Identity is derived from content, and that is what makes a version immutable.</strong>
/// The fingerprint is <c>{base-name}@{12 hex}</c>, where the hex is the leading half of a SHA-256
/// over the canonical manifest with its own fingerprint field nulled -
/// <c>us-pit-sample400-2021-09-to-2026-08@f78cfe45b962</c> is the sealed universe in force today.
/// Edit any part of a universe and the fingerprint changes, so an edited universe is a
/// <em>different</em> universe rather than the same one altered. Immutability is therefore a
/// property of the identity rather than a rule somebody has to remember to apply.
/// </para>
/// <para>
/// <strong>Why not a surrogate.</strong> A <see cref="Guid"/> would say nothing, and the thing every
/// declaration, every authorisation and every sealed report already names a universe by is this
/// string. <c>ContentHash</c> keys the evidence archive on the same principle for the same reason.
/// </para>
/// <para>
/// The base name is not parsed for meaning. It reads as a window and a population because whoever
/// sealed it chose to make it readable, and nothing here depends on that continuing.
/// </para>
/// </remarks>
public sealed record UniverseId
{
    /// <summary>Hex characters of the digest the fingerprint carries.</summary>
    public const int DigestLength = 12;

    /// <summary>The longest a whole fingerprint may be.</summary>
    public const int MaxLength = 128;

    private UniverseId(string value, string baseName, string digest)
    {
        Value = value;
        BaseName = baseName;
        Digest = digest;
    }

    /// <summary>The whole fingerprint, as every declaration spells it.</summary>
    public string Value { get; }

    /// <summary>The readable half, before the separator.</summary>
    public string BaseName { get; }

    /// <summary>The content digest, lower-case hex.</summary>
    public string Digest { get; }

    /// <summary>Creates an identity from a sealed fingerprint, refusing anything malformed.</summary>
    public static UniverseId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(nameof(value), "A universe fingerprint is required.");
        }

        var trimmed = value.Trim();

        if (trimmed.Length > MaxLength)
        {
            throw new DomainValidationException(
                nameof(value),
                $"A universe fingerprint may not exceed {MaxLength} characters.");
        }

        var separator = trimmed.LastIndexOf('@');

        if (separator <= 0 || separator == trimmed.Length - 1)
        {
            throw new DomainValidationException(
                nameof(value),
                "A universe fingerprint is '{base-name}@{digest}'. Received: " + trimmed);
        }

        var baseName = trimmed[..separator];
        var digest = trimmed[(separator + 1)..];

        if (digest.Length != DigestLength)
        {
            throw new DomainValidationException(
                nameof(value),
                $"A universe fingerprint's digest must be {DigestLength} hex characters. Received "
                + $"'{digest}' ({digest.Length}).");
        }

        foreach (var c in digest)
        {
            if (!char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'))
            {
                throw new DomainValidationException(
                    nameof(value),
                    "A universe fingerprint's digest must be lower-case hex. Received: " + digest);
            }
        }

        return new UniverseId(trimmed, baseName, digest);
    }

    public override string ToString() => Value;
}
