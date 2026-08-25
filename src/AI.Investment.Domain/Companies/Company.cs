using AI.Investment.Domain.Common;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Companies;

/// <summary>
/// A company the platform tracks. Reference data, and the first aggregate in the system.
/// </summary>
/// <remarks>
/// <para>
/// The pre-Phase-1 version of this type was a POCO with public setters on every property, which
/// meant <c>new Company { Ticker = "" }</c> was a legal object and every consumer downstream had
/// to defend against it. Here, state changes only through named operations, and an invalid
/// company cannot be constructed at all.
/// </para>
/// <para>
/// Deliberately NOT modelled yet: securities and share classes (a company can have several
/// listings), durable identifiers such as FIGI or ISIN, delisting state, and corporate actions.
/// All belong to the reference-data work in Phase 2. Retaining delisted companies matters for
/// survivorship bias, but there is nothing to delist until there is an ingestion pipeline.
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
        Ticker ticker,
        Exchange? exchange,
        string? sector,
        string? industry,
        string? country,
        string? description,
        DateTime createdAtUtc,
        DateTime updatedAtUtc)
        : base(id)
    {
        Name = name;
        Ticker = ticker;
        Exchange = exchange;
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
        Ticker = null!;
    }

    public string Name { get; private set; }

    public Ticker Ticker { get; private set; }

    public Exchange? Exchange { get; private set; }

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
    public static Company Create(
        CompanyId id,
        string name,
        Ticker ticker,
        DateTime nowUtc,
        Exchange? exchange = null,
        string? sector = null,
        string? industry = null,
        string? country = null,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(ticker);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        var validatedName = ValidateName(name);

        return new Company(
            id,
            validatedName,
            ticker,
            exchange,
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

    /// <summary>
    /// Re-lists the company under a different symbol or venue.
    /// </summary>
    /// <remarks>
    /// A ticker change is a real corporate event, not a correction, and treating it as an
    /// ordinary field update loses that. Phase 2 records the history; Phase 1 at least makes it
    /// an explicit operation rather than a setter.
    /// </remarks>
    public void ChangeListing(Ticker ticker, Exchange? exchange, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(ticker);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        Ticker = ticker;
        Exchange = exchange;
        Touch(nowUtc);
    }

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
