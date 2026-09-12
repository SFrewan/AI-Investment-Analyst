using AI.Investment.Domain.Common;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Securities;

/// <summary>
/// A place where a security may trade. Reference data, scoped to the interval it describes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Not to be confused with the execution venue.</strong> <c>VenueOrder</c>,
/// <c>VenueFill</c> and <c>VenueResult</c> in the application layer are about sending an order
/// somewhere and hearing back. This is reference data: the venue as a fact about the world, which
/// exists whether or not the platform ever trades on it.
/// </para>
/// <para>
/// <strong>Why it carries an interval.</strong> Venues are slowly changing, not permanent - they
/// merge, rename and close. A venue record therefore states the window it is true for, so that a
/// listing event dated inside that window can be read against the venue as it then was rather than
/// as it is now. A null <see cref="ValidTo"/> means no end has been observed, which is not the same
/// as a claim that it operates forever.
/// </para>
/// </remarks>
public sealed class Venue : AggregateRoot<VenueId>
{
    public const int MaxNameLength = 200;
    public const int CountryCodeLength = 2;

    private Venue(
        VenueId id,
        string name,
        string? countryCode,
        DateOnly validFrom,
        DateOnly? validTo,
        DateTime recordedAtUtc)
        : base(id)
    {
        Name = name;
        CountryCode = countryCode;
        ValidFrom = validFrom;
        ValidTo = validTo;
        RecordedAtUtc = recordedAtUtc;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private Venue() => Name = string.Empty;

    public string Name { get; private set; }

    /// <summary>ISO 3166-1 alpha-2, or null when the source did not state one.</summary>
    public string? CountryCode { get; private set; }

    /// <summary>First date this reference record is true for.</summary>
    public DateOnly ValidFrom { get; private set; }

    /// <summary>Last date it is true for, or null when no end has been observed.</summary>
    public DateOnly? ValidTo { get; private set; }

    /// <summary>When this row was written here.</summary>
    public DateTime RecordedAtUtc { get; private set; }

    public static Venue Create(
        VenueId id,
        string name,
        DateOnly validFrom,
        DateTime nowUtc,
        string? countryCode = null,
        DateOnly? validTo = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        var trimmedName = ValidateName(name);
        var country = NormaliseCountry(countryCode);

        if (validTo is { } end && end < validFrom)
        {
            throw new DomainRuleViolationException(
                "Venue.IntervalOrdered",
                $"A venue cannot stop being valid ({end:O}) before it starts ({validFrom:O}).");
        }

        return new Venue(id, trimmedName, country, validFrom, validTo, nowUtc);
    }

    /// <summary>Whether this venue record is true for <paramref name="asOf"/>.</summary>
    public bool CoversDate(DateOnly asOf) =>
        asOf >= ValidFrom && (ValidTo is null || asOf <= ValidTo);

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainValidationException(nameof(name), "A venue name is required.");
        }

        var trimmed = name.Trim();

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainValidationException(
                nameof(name),
                $"A venue name may not exceed {MaxNameLength} characters.");
        }

        return trimmed;
    }

    private static string? NormaliseCountry(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode))
        {
            return null;
        }

        var country = countryCode.Trim().ToUpperInvariant();

        if (country.Length != CountryCodeLength)
        {
            throw new DomainValidationException(
                nameof(countryCode),
                $"A country code must be exactly {CountryCodeLength} characters.");
        }

        return country;
    }
}
