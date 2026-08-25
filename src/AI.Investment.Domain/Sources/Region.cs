using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Sources;

/// <summary>
/// The geography a source covers - an ISO 3166-1 alpha-2 country code, or <c>GLOBAL</c>.
/// </summary>
/// <remarks>
/// Present from the start because coverage is a correctness question, not a label. A U.S.
/// regulator is authoritative about a U.S. registrant and says nothing about a Japanese one, and
/// a system that cannot express that will eventually apply the wrong source to the wrong
/// instrument. Only the shape is validated - maintaining the live ISO list is reference-data work
/// for a later phase, and rejecting a valid but unlisted code would be worse than accepting a
/// well-formed unknown one.
/// </remarks>
public sealed record Region
{
    private const string GlobalCode = "GLOBAL";

    private Region(string code) => Code = code;

    public string Code { get; }

    /// <summary>Not geographically bounded.</summary>
    public static Region Global { get; } = new(GlobalCode);

    /// <summary>The first production domain's home market.</summary>
    public static Region UnitedStates { get; } = new("US");

    public bool IsGlobal => string.Equals(Code, GlobalCode, StringComparison.Ordinal);

    public static Region Create(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new DomainValidationException(nameof(code), "A region code is required.");
        }

        var normalised = code.Trim().ToUpperInvariant();

        if (string.Equals(normalised, GlobalCode, StringComparison.Ordinal))
        {
            return Global;
        }

        if (normalised.Length != 2)
        {
            throw new DomainValidationException(
                nameof(code),
                $"A region must be a 2-letter ISO 3166-1 country code or '{GlobalCode}'. Received '{code}'.");
        }

        foreach (var c in normalised)
        {
            if (!char.IsAsciiLetterUpper(c))
            {
                throw new DomainValidationException(
                    nameof(code),
                    $"A region code must contain only letters. Received '{code}'.");
            }
        }

        return new Region(normalised);
    }

    /// <summary>True when this region covers <paramref name="other"/>.</summary>
    public bool Covers(Region other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return IsGlobal || string.Equals(Code, other.Code, StringComparison.Ordinal);
    }

    public override string ToString() => Code;
}
