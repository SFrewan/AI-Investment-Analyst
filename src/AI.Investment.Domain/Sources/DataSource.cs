using AI.Investment.Domain.Common;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Sources;

/// <summary>
/// A registered place the platform gets information from, and the terms on which it is trusted.
/// </summary>
/// <remarks>
/// <para>
/// The entry point of the data plane. Nothing enters the system from an unregistered origin: an
/// ingestion run names the source it drew from, every resulting claim carries that identity in
/// its provenance, and "where did this come from and how much does it count?" becomes a lookup
/// rather than an investigation.
/// </para>
/// <para>
/// Deliberately says nothing about HOW to fetch anything - no URLs, no credentials, no protocol.
/// Those belong to the connector, which is infrastructure. This is the trust model, and keeping
/// it free of transport detail is what lets the same source be reached over a REST API today and
/// a bulk file tomorrow without touching what the platform believes about it.
/// </para>
/// <para>
/// Registered inactive by default. A source appearing in the registry is a statement that it has
/// been assessed, not that it is switched on - activation is a separate, deliberate act.
/// </para>
/// </remarks>
public sealed class DataSource : AggregateRoot<SourceId>
{
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 2000;

    private readonly HashSet<DataCategory> _categories;

    private DataSource(
        SourceId id,
        string name,
        SourceType type,
        SourceAuthority authority,
        Region region,
        HashSet<DataCategory> categories,
        UpdateCadence cadence,
        LicensingTerms licensing,
        VerificationPolicy verification,
        string? description,
        DateTime registeredAtUtc)
        : base(id)
    {
        Name = name;
        Type = type;
        Authority = authority;
        Region = region;
        _categories = categories;
        Cadence = cadence;
        Licensing = licensing;
        Verification = verification;
        Description = description;
        RegisteredAtUtc = registeredAtUtc;
        UpdatedAtUtc = registeredAtUtc;
        Reliability = ReliabilityGrade.Unrated;
        IsActive = false;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private DataSource()
    {
        Name = string.Empty;
        Region = null!;
        _categories = [];
        Cadence = null!;
        Licensing = null!;
        Verification = null!;
    }

    public string Name { get; private set; }

    public SourceType Type { get; private set; }

    /// <summary>What the source IS. Contrast <see cref="Reliability"/>, which is what it has done.</summary>
    public SourceAuthority Authority { get; private set; }

    public Region Region { get; private set; }

    /// <summary>What kinds of information this source supplies.</summary>
    public IReadOnlyCollection<DataCategory> Categories => _categories;

    public UpdateCadence Cadence { get; private set; }

    /// <summary>What the platform is permitted to do with this source's data.</summary>
    public LicensingTerms Licensing { get; private set; }

    public VerificationPolicy Verification { get; private set; }

    /// <summary>Measured performance. <see cref="ReliabilityGrade.Unrated"/> until earned.</summary>
    public ReliabilityGrade Reliability { get; private set; }

    /// <summary>Whether ingestion may draw from this source. False on registration.</summary>
    public bool IsActive { get; private set; }

    public string? Description { get; private set; }

    public DateTime RegisteredAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>
    /// True when this source's word alone is enough for information to be treated as confirmed.
    /// </summary>
    public bool IsAuthoritative =>
        Authority == SourceAuthority.Primary && Verification.CanConfirmAlone;

    public static DataSource Register(
        SourceId id,
        string name,
        SourceType type,
        SourceAuthority authority,
        Region region,
        IEnumerable<DataCategory> categories,
        UpdateCadence cadence,
        LicensingTerms licensing,
        VerificationPolicy verification,
        DateTime nowUtc,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(cadence);
        ArgumentNullException.ThrowIfNull(licensing);
        ArgumentNullException.ThrowIfNull(verification);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        var validatedName = ValidateName(name);
        var categorySet = BuildCategories(categories);

        if (!Enum.IsDefined(type))
        {
            throw new DomainValidationException(nameof(type), $"Unrecognised source type '{type}'.");
        }

        if (!Enum.IsDefined(authority))
        {
            throw new DomainValidationException(
                nameof(authority),
                $"Unrecognised source authority '{authority}'.");
        }

        // A source cannot be both unassessed and self-sufficient. Without this, a registration
        // that skipped the authority question could quietly mint confirmed facts.
        if (authority == SourceAuthority.Unverified && verification.CanConfirmAlone)
        {
            throw new DomainRuleViolationException(
                "DataSource.UnverifiedCannotConfirmAlone",
                "A source of unverified authority may not confirm information on its own. " +
                "Unverified information must never silently become a fact.");
        }

        // Community and aggregator content is never the originating record, whatever the
        // registration claims. Structural, so a mistaken entry cannot elevate it.
        if (type == SourceType.CommunityOrAggregator && authority == SourceAuthority.Primary)
        {
            throw new DomainRuleViolationException(
                "DataSource.AggregatorIsNotPrimary",
                "A community or aggregator source is never primary - by definition it republishes " +
                "someone else's record.");
        }

        return new DataSource(
            id,
            validatedName,
            type,
            authority,
            region,
            categorySet,
            cadence,
            licensing,
            verification,
            NormaliseDescription(description),
            nowUtc);
    }

    /// <summary>Permits ingestion to draw from this source.</summary>
    /// <remarks>
    /// Refuses a source whose licensing permits nothing. Switching on a feed the platform is not
    /// allowed to store or process is a compliance problem, and the registry is the right place
    /// to catch it - before an ingestion run has already retained the data.
    /// </remarks>
    public void Activate(DateTime nowUtc)
    {
        EnsureModificationFollowsRegistration(nowUtc);

        if (!Licensing.StorageAllowed && !Licensing.AutomatedProcessingAllowed)
        {
            throw new DomainRuleViolationException(
                "DataSource.ActivationRequiresUsableLicence",
                $"Source '{Id}' may neither be stored nor processed automatically, so activating it " +
                "would permit ingestion that its terms forbid. Establish the licensing terms first.");
        }

        IsActive = true;
        Touch(nowUtc);
    }

    public void Deactivate(DateTime nowUtc)
    {
        EnsureModificationFollowsRegistration(nowUtc);

        IsActive = false;
        Touch(nowUtc);
    }

    /// <summary>
    /// Records a measured reliability grade.
    /// </summary>
    /// <remarks>
    /// Separate from registration because reliability is earned, not declared. The evaluation
    /// phase calls this from measured outcomes; nothing in the ingestion path may.
    /// </remarks>
    public void RecordReliability(ReliabilityGrade grade, DateTime nowUtc)
    {
        EnsureModificationFollowsRegistration(nowUtc);

        if (!Enum.IsDefined(grade))
        {
            throw new DomainValidationException(nameof(grade), $"Unrecognised reliability grade '{grade}'.");
        }

        Reliability = grade;
        Touch(nowUtc);
    }

    /// <summary>Updates the licensing terms, for example after a legal review.</summary>
    public void UpdateLicensing(LicensingTerms licensing, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(licensing);
        EnsureModificationFollowsRegistration(nowUtc);

        Licensing = licensing;
        Touch(nowUtc);

        // Terms can narrow. A source already switched on must not keep running under terms that
        // no longer permit what ingestion does.
        if (IsActive && !Licensing.StorageAllowed && !Licensing.AutomatedProcessingAllowed)
        {
            IsActive = false;
        }
    }

    public void UpdateCoverage(IEnumerable<DataCategory> categories, DateTime nowUtc)
    {
        EnsureModificationFollowsRegistration(nowUtc);

        var updated = BuildCategories(categories);

        _categories.Clear();

        foreach (var category in updated)
        {
            _categories.Add(category);
        }

        Touch(nowUtc);
    }

    /// <summary>Whether this source supplies <paramref name="category"/> for <paramref name="region"/>.</summary>
    public bool Supplies(DataCategory category, Region region)
    {
        ArgumentNullException.ThrowIfNull(region);
        return _categories.Contains(category) && Region.Covers(region);
    }

    public override string ToString() => $"{Id} ({Authority}/{Type})";

    /// <summary>
    /// Validates the modification timestamp. Called at the START of every mutator, before any
    /// state changes.
    /// </summary>
    /// <remarks>
    /// This used to live inside <c>Touch</c>, which runs last - so a call with an impossible
    /// timestamp threw only after the aggregate had already been mutated, leaving it changed by
    /// an operation that failed. Validate first, mutate second.
    /// </remarks>
    private void EnsureModificationFollowsRegistration(DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (nowUtc < RegisteredAtUtc)
        {
            throw new DomainRuleViolationException(
                "DataSource.UpdateFollowsRegistration",
                $"A source cannot be modified ({nowUtc:O}) before it was registered ({RegisteredAtUtc:O}).");
        }
    }

    private void Touch(DateTime nowUtc) => UpdatedAtUtc = nowUtc;

    private static HashSet<DataCategory> BuildCategories(IEnumerable<DataCategory> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);

        var set = new HashSet<DataCategory>();

        foreach (var category in categories)
        {
            if (!Enum.IsDefined(category))
            {
                throw new DomainValidationException(
                    nameof(categories),
                    $"Unrecognised data category '{category}'.");
            }

            if (category == DataCategory.Unknown)
            {
                throw new DomainValidationException(
                    nameof(categories),
                    "A source must declare what it actually supplies; 'Unknown' is not a coverage claim.");
            }

            set.Add(category);
        }

        if (set.Count == 0)
        {
            throw new DomainValidationException(
                nameof(categories),
                "A source must declare at least one data category, otherwise nothing can route to it.");
        }

        return set;
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainValidationException(nameof(name), "A source name is required.");
        }

        var trimmed = name.Trim();

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainValidationException(
                nameof(name),
                $"A source name may not exceed {MaxNameLength} characters.");
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
                $"A source description may not exceed {MaxDescriptionLength} characters.");
        }

        return trimmed;
    }
}
