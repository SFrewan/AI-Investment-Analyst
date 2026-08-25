using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Domain.Ingestion;

/// <summary>
/// What a connector can actually do, declared by the connector itself.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="DataSource"/>. The registry says what the platform is
/// <em>permitted</em> to take from a source; this says what the connector is <em>able</em> to
/// fetch. Both must agree before a request is made, and keeping them apart is what lets the same
/// source be served by a rate-limited API today and an unlimited bulk file tomorrow without
/// changing anything the platform believes about it.
/// </para>
/// <para>
/// Declared rather than discovered. A connector that finds out what it supports by trying is a
/// connector that discovers a provider's restrictions by violating them.
/// </para>
/// <para>
/// <see cref="SubjectKinds"/> is compared case-insensitively and is a set of strings for the same
/// reason <see cref="IngestionSubject"/> is: the platform's scope spans companies, products,
/// suppliers, currencies, routes and domains not yet named, and an enumeration here would have to
/// be edited every time a new one appeared.
/// </para>
/// </remarks>
public sealed class ProviderCapabilities
{
    private readonly HashSet<DataCategory> _categories;
    private readonly List<Region> _regions;
    private readonly HashSet<string> _subjectKinds;

    private ProviderCapabilities(
        HashSet<DataCategory> categories,
        List<Region> regions,
        HashSet<string> subjectKinds,
        bool supportsWindow,
        TimeSpan? maxWindowDuration,
        ProviderQuota? quota)
    {
        _categories = categories;
        _regions = regions;
        _subjectKinds = subjectKinds;
        SupportsWindow = supportsWindow;
        MaxWindowDuration = maxWindowDuration;
        Quota = quota;
    }

    public IReadOnlyCollection<DataCategory> Categories => _categories;

    public IReadOnlyList<Region> Regions => _regions;

    /// <summary>Subject kinds this connector understands, compared case-insensitively.</summary>
    public IReadOnlyCollection<string> SubjectKinds => _subjectKinds;

    /// <summary>Whether the connector accepts a period, as opposed to only "latest".</summary>
    public bool SupportsWindow { get; }

    /// <summary>The longest period the connector will accept in one request, when bounded.</summary>
    public TimeSpan? MaxWindowDuration { get; }

    /// <summary>The rate the provider permits, when it declares one.</summary>
    public ProviderQuota? Quota { get; }

    public static ProviderCapabilities Create(
        IEnumerable<DataCategory> categories,
        IEnumerable<Region> regions,
        IEnumerable<string> subjectKinds,
        bool supportsWindow = false,
        TimeSpan? maxWindowDuration = null,
        ProviderQuota? quota = null)
    {
        var categorySet = BuildCategories(categories);
        var regionList = BuildRegions(regions);
        var subjectKindSet = BuildSubjectKinds(subjectKinds);

        if (!supportsWindow && maxWindowDuration is not null)
        {
            throw new DomainValidationException(
                nameof(maxWindowDuration),
                "A connector that does not accept a period cannot declare a maximum period. One of " +
                "the two is a mistake, and guessing which would hide it.");
        }

        if (maxWindowDuration is { } max && max <= TimeSpan.Zero)
        {
            throw new DomainValidationException(
                nameof(maxWindowDuration),
                $"A maximum window must be positive. Received {max}.");
        }

        return new ProviderCapabilities(
            categorySet,
            regionList,
            subjectKindSet,
            supportsWindow,
            maxWindowDuration,
            quota);
    }

    public bool Supports(DataCategory category) => _categories.Contains(category);

    /// <summary>True when some declared region covers <paramref name="region"/>.</summary>
    public bool Covers(Region region)
    {
        ArgumentNullException.ThrowIfNull(region);

        foreach (var declared in _regions)
        {
            if (declared.Covers(region))
            {
                return true;
            }
        }

        return false;
    }

    public bool Understands(string? subjectKind) =>
        !string.IsNullOrWhiteSpace(subjectKind) && _subjectKinds.Contains(subjectKind.Trim());

    public override string ToString() =>
        $"{_categories.Count} categories, {_regions.Count} regions, " +
        $"{_subjectKinds.Count} subject kinds, window={SupportsWindow}";

    private static HashSet<DataCategory> BuildCategories(IEnumerable<DataCategory> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);

        var set = new HashSet<DataCategory>();

        foreach (var category in categories)
        {
            if (!Enum.IsDefined(category) || category == DataCategory.Unknown)
            {
                throw new DomainValidationException(
                    nameof(categories),
                    $"'{category}' is not a data category a connector can declare.");
            }

            set.Add(category);
        }

        if (set.Count == 0)
        {
            throw new DomainValidationException(
                nameof(categories),
                "A connector must declare at least one data category, otherwise nothing can route to it.");
        }

        return set;
    }

    private static List<Region> BuildRegions(IEnumerable<Region> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);

        var list = new List<Region>();

        foreach (var region in regions)
        {
            ArgumentNullException.ThrowIfNull(region);

            if (!list.Contains(region))
            {
                list.Add(region);
            }
        }

        if (list.Count == 0)
        {
            throw new DomainValidationException(
                nameof(regions),
                "A connector must declare at least one region.");
        }

        return list;
    }

    private static HashSet<string> BuildSubjectKinds(IEnumerable<string> subjectKinds)
    {
        ArgumentNullException.ThrowIfNull(subjectKinds);

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kind in subjectKinds)
        {
            if (string.IsNullOrWhiteSpace(kind))
            {
                throw new DomainValidationException(
                    nameof(subjectKinds),
                    "A blank subject kind is not a declaration.");
            }

            set.Add(kind.Trim());
        }

        if (set.Count == 0)
        {
            throw new DomainValidationException(
                nameof(subjectKinds),
                "A connector must declare at least one subject kind.");
        }

        return set;
    }
}
