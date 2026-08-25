using System.Globalization;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Sources;

/// <summary>
/// How long a source's data may be kept, when its terms say so at all.
/// </summary>
/// <remarks>
/// <para>
/// A property of the <em>licence</em>, not of the platform. There is deliberately no global
/// retention period anywhere in this system: retention obligations attach to sources
/// individually, and a single configured number would either exceed some source's contractual
/// cap or discard another source's data for no reason. Enforcement reads the obligation from
/// the source's <see cref="LicensingTerms"/>, so the rule and the terms cannot drift apart.
/// </para>
/// <para>
/// <see cref="Unlimited"/> means the terms impose no cap - public-domain government records, for
/// example. It does not mean "keep forever unconditionally"; a storage policy may still reclaim
/// space. It means nothing <em>legal</em> compels deletion, which is a different question and the
/// only one this type answers.
/// </para>
/// <para>
/// The absence of a cap is modelled explicitly rather than as a null <see cref="TimeSpan"/>,
/// because "no obligation" and "obligation not yet established" must not look alike. An
/// unestablished licence is <see cref="LicensingTerms.Unknown"/>, which permits nothing at all.
/// </para>
/// </remarks>
public sealed record RetentionLimit
{
    private RetentionLimit(TimeSpan? maximumAge) => MaximumAge = maximumAge;

    /// <summary>The longest the data may be kept, or null when the terms impose no cap.</summary>
    public TimeSpan? MaximumAge { get; }

    /// <summary>True when the licence caps how long data may be kept.</summary>
    public bool IsBounded => MaximumAge.HasValue;

    /// <summary>The terms impose no retention cap.</summary>
    public static RetentionLimit Unlimited { get; } = new((TimeSpan?)null);

    /// <summary>The terms require deletion after <paramref name="maximumAge"/>.</summary>
    public static RetentionLimit Of(TimeSpan maximumAge)
    {
        if (maximumAge <= TimeSpan.Zero)
        {
            throw new DomainValidationException(
                nameof(maximumAge),
                $"A retention limit must be positive. Received {maximumAge}. A zero or negative " +
                "limit would require deleting data before it could be used, which is a mistake " +
                "rather than a licence term.");
        }

        return new RetentionLimit(maximumAge);
    }

    /// <summary>Convenience for the common contractual shape.</summary>
    public static RetentionLimit OfDays(int days) => Of(TimeSpan.FromDays(days));

    /// <summary>
    /// Whether data retrieved at <paramref name="retrievedAtUtc"/> has outlived this limit.
    /// </summary>
    public bool IsExceededBy(DateTime retrievedAtUtc, DateTime nowUtc) =>
        MaximumAge is { } maximum && nowUtc - retrievedAtUtc > maximum;

    public override string ToString() =>
        MaximumAge is { } maximum ? maximum.ToString("c", CultureInfo.InvariantCulture) : "unlimited";
}
