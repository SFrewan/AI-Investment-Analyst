using AI.Investment.Domain.Common;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Companies;

/// <summary>
/// The legal entity: who the issuer is, not what it trades under.
/// </summary>
/// <remarks>
/// <para>
/// The pre-Phase-1 version of this type was a POCO with public setters on every property, which
/// meant a half-built company was a legal object and every consumer downstream had to defend
/// against it. Here, state changes only through named operations, and an invalid company cannot be
/// constructed at all.
/// </para>
/// <para>
/// <strong>It no longer carries a ticker or an exchange, and that is the point of this stage.</strong>
/// Both were current-state market identity sitting on a legal-identity aggregate: one value per
/// company, overwritten by <c>ChangeListing</c>, with the previous value gone. That made every
/// historical question unanswerable from production data and made a symbol the de-facto key of a
/// company. A ticker is now an identifier assertion on a <c>Security</c>, carrying the interval and
/// the source that say when it was true; an exchange is a <c>Venue</c>, reached through a
/// <c>Listing</c> whose status history is per venue.
/// </para>
/// <para>
/// <strong>The practical consequence is why it was done now.</strong> A required ticker made 170 of
/// the 763 companies the platform holds SEC evidence for impossible to construct - they are the
/// deregistered ones, whose current filings name no symbol, and they are exactly the companies a
/// universe built to resist survivorship bias must retain.
/// </para>
/// <para>
/// <strong>Current-state, and it claims nothing more.</strong> There is no version, no validity
/// interval and no history here, and this type must never be read as answering "who was this
/// company at time T". That question belongs to <c>Listing</c>, <c>UniverseMembership</c>,
/// <c>Determination</c> and evidence publication time. Making <c>Company</c> temporal is a separate
/// decision with its own gate.
/// </para>
/// </remarks>
public sealed class Company : AggregateRoot<CompanyId>
{
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 4000;
    public const int MaxClassificationLength = 100;

    private Company(
        CompanyId id,
        string name,
        Cik? cik,
        string? sector,
        string? industry,
        string? country,
        string? description,
        DateTime createdAtUtc,
        DateTime updatedAtUtc)
        : base(id)
    {
        Name = name;
        Cik = cik;
        Sector = sector;
        Industry = industry;
        Country = country;
        Description = description;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private Company()
    {
        Name = string.Empty;
    }

    public string Name { get; private set; }

    /// <summary>
    /// The SEC filer identity, when one is known. Null is ordinary, not missing data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Legal identity, and the company's alone.</strong> One CIK may issue several
    /// securities, so it belongs here and a <c>Security</c> reaches it only through
    /// <c>CompanyId</c>. Nothing gives a <c>Security</c> a CIK of its own.
    /// </para>
    /// <para>
    /// <strong>Null means no SEC identity is held, not that one is pending.</strong> A company
    /// outside SEC reporting has no CIK to record, and a required field would have forced one to be
    /// invented. Unique when present, so two companies cannot claim one filer.
    /// </para>
    /// <para>
    /// <strong>It answers a question about now, never about the past.</strong> This aggregate is
    /// current-state by decision, and no history is kept here. A point-in-time question belongs to
    /// <c>Listing</c>, <c>UniverseMembership</c>, <c>Determination</c> or evidence publication time,
    /// and this field must not be read as if it answered one.
    /// </para>
    /// </remarks>
    public Cik? Cik { get; private set; }

    public string? Sector { get; private set; }

    public string? Industry { get; private set; }

    public string? Country { get; private set; }

    public string? Description { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>
    /// Creates a company. Every argument is validated; there is no path to a partially-valid
    /// instance.
    /// </summary>
    /// <param name="nowUtc">
    /// The current time, supplied by the caller rather than read from <see cref="DateTime.UtcNow"/>.
    /// The domain does not read the clock: a type that reaches out for ambient state cannot be
    /// tested deterministically, and in a system that will replay historical decisions, "now"
    /// is genuinely an input.
    /// </param>
    /// <param name="cik">
    /// The SEC filer identity, when one is known. Optional and last in the list so that every
    /// existing caller keeps compiling unchanged - a company outside SEC reporting has none, and
    /// making it required would have forced one to be invented.
    /// </param>
    public static Company Create(
        CompanyId id,
        string name,
        DateTime nowUtc,
        string? sector = null,
        string? industry = null,
        string? country = null,
        string? description = null,
        Cik? cik = null)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        var validatedName = ValidateName(name);

        return new Company(
            id,
            validatedName,
            cik,
            NormaliseClassification(sector, nameof(sector)),
            NormaliseClassification(industry, nameof(industry)),
            NormaliseClassification(country, nameof(country)),
            NormaliseDescription(description),
            nowUtc,
            nowUtc);
    }

    /// <summary>Changes the company's legal or trading name.</summary>
    public void Rename(string name, DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        Name = ValidateName(name);
        Touch(nowUtc);
    }

    // ChangeListing is gone with the fields it changed. A ticker change is a real corporate event
    // and was never an ordinary field update - which is precisely why overwriting one value with
    // the next was the wrong model. It is now a new identifier assertion on the security, carrying
    // the interval and the source that say when each symbol was true, and a listing transition
    // where a venue is involved. Nothing here needs a replacement.

    /// <summary>Updates the descriptive profile. Passing null clears a field.</summary>
    public void UpdateProfile(
        string? sector,
        string? industry,
        string? country,
        string? description,
        DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        Sector = NormaliseClassification(sector, nameof(sector));
        Industry = NormaliseClassification(industry, nameof(industry));
        Country = NormaliseClassification(country, nameof(country));
        Description = NormaliseDescription(description);
        Touch(nowUtc);
    }

    private void Touch(DateTime nowUtc)
    {
        if (nowUtc < CreatedAtUtc)
        {
            throw new DomainRuleViolationException(
                "Company.UpdateFollowsCreation",
                $"A company cannot be modified ({nowUtc:O}) before it was created ({CreatedAtUtc:O}).");
        }

        UpdatedAtUtc = nowUtc;
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainValidationException(nameof(name), "A company name is required.");
        }

        var trimmed = name.Trim();

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainValidationException(
                nameof(name),
                $"A company name may not exceed {MaxNameLength} characters.");
        }

        return trimmed;
    }

    private static string? NormaliseClassification(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        if (trimmed.Length > MaxClassificationLength)
        {
            throw new DomainValidationException(
                parameterName,
                $"This value may not exceed {MaxClassificationLength} characters.");
        }

        return trimmed;
    }

    private static string? NormaliseDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var trimmed = description.Trim();

        if (trimmed.Length > MaxDescriptionLength)
        {
            throw new DomainValidationException(
                nameof(description),
                $"A description may not exceed {MaxDescriptionLength} characters.");
        }

        return trimmed;
    }
}
