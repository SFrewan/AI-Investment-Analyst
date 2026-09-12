using AI.Investment.Domain.Common;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Universe;

/// <summary>
/// One sealed universe version. A row here <em>is</em> a version; there is no separate version type.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Sealed, never edited.</strong> A new population, a new window or a corrected member list
/// is a new row with a new fingerprint, and the old row stays exactly as it was. That is not a
/// convention applied by discipline: the identity is a digest of the content, so an altered universe
/// cannot keep its name. A backtest that cites a fingerprint therefore cites something that cannot
/// have changed underneath it.
/// </para>
/// <para>
/// <strong>There is no "current universe" here, deliberately.</strong> No flag, no latest-wins
/// column, no mutable pointer. A point-in-time read selects the version sealed at or before its
/// <c>asOf</c>, which is the only selection that cannot let today's population answer a question
/// about a past date. A current-universe flag would be read by everything within a release and would
/// quietly make every historical answer a present-tense one.
/// </para>
/// <para>
/// <strong>The span is not stored.</strong> This row carries the cohort cut dates and the window;
/// <c>MembershipSpan.Derive</c> computes a member's span from those on read. Storing the span would
/// be storing a derived value as if it were evidence, and would need rewriting whenever the
/// derivation was corrected.
/// </para>
/// <para>
/// <strong>The declaration does not go away.</strong> The sealed JSON under <c>declarations/</c>
/// stays as the operator-readable copy; this table is the queryable projection of the same seal, and
/// <see cref="ManifestContentHash"/> is what holds the two to each other.
/// </para>
/// </remarks>
public sealed class Universe : AggregateRoot<UniverseId>
{
    public const int MaxRuleTextLength = 2000;

    private readonly List<DateOnly> _cohortCutDates = [];

    private Universe(
        UniverseId id,
        DateOnly windowFrom,
        DateOnly windowTo,
        IEnumerable<DateOnly> cohortCutDates,
        DateTime sealedAtUtc,
        ContentHash manifestContentHash,
        string populationDefinition,
        string membershipRule,
        DateTime recordedAtUtc)
        : base(id)
    {
        WindowFrom = windowFrom;
        WindowTo = windowTo;
        _cohortCutDates.AddRange(cohortCutDates);
        SealedAtUtc = sealedAtUtc;
        ManifestContentHash = manifestContentHash;
        PopulationDefinition = populationDefinition;
        MembershipRule = membershipRule;
        RecordedAtUtc = recordedAtUtc;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private Universe()
    {
        ManifestContentHash = null!;
        PopulationDefinition = string.Empty;
        MembershipRule = string.Empty;
    }

    /// <summary>First day of the sealed window.</summary>
    public DateOnly WindowFrom { get; private set; }

    /// <summary>Last day of the sealed window.</summary>
    public DateOnly WindowTo { get; private set; }

    /// <summary>Every cohort cut date in this version, ascending and distinct.</summary>
    public IReadOnlyList<DateOnly> CohortCutDates => _cohortCutDates;

    /// <summary>
    /// The latest cut. A member still present at it had not left when the cutting stopped.
    /// </summary>
    /// <remarks>
    /// Derived rather than stored, because it is nothing but the last element and a stored copy
    /// could disagree with the list beside it.
    /// </remarks>
    public DateOnly FinalCut => _cohortCutDates[^1];

    /// <summary>When this version was sealed. Point-in-time selection orders on this.</summary>
    public DateTime SealedAtUtc { get; private set; }

    /// <summary>The hash of the sealed manifest document this row projects.</summary>
    public ContentHash ManifestContentHash { get; private set; }

    /// <summary>
    /// How the population was defined, in the words the manifest records.
    /// </summary>
    /// <remarks>
    /// Text, and only text. Nothing evaluates it, and nothing should: there is no code anywhere in
    /// this repository that applies a membership rule, and the result of having applied one is the
    /// cohort list on each member. Keeping the prose makes the seal readable without making it
    /// executable.
    /// </remarks>
    public string PopulationDefinition { get; private set; }

    /// <summary>How membership was decided, in the words the manifest records. Text only.</summary>
    public string MembershipRule { get; private set; }

    /// <summary>When this row was written here. Not when the universe was sealed.</summary>
    public DateTime RecordedAtUtc { get; private set; }

    /// <summary>
    /// Records a sealed universe version. There is no counterpart that changes one.
    /// </summary>
    public static Universe Seal(
        UniverseId id,
        DateOnly windowFrom,
        DateOnly windowTo,
        IEnumerable<DateOnly> cohortCutDates,
        DateTime sealedAtUtc,
        ContentHash manifestContentHash,
        string populationDefinition,
        string membershipRule,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(cohortCutDates);
        ArgumentNullException.ThrowIfNull(manifestContentHash);

        DateRange.EnsureUtc(sealedAtUtc, nameof(sealedAtUtc));
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (windowTo < windowFrom)
        {
            throw new DomainRuleViolationException(
                "Universe.WindowOrdered",
                $"A universe window cannot end ({windowTo:O}) before it starts ({windowFrom:O}).");
        }

        var cuts = cohortCutDates.Distinct().OrderBy(d => d).ToList();

        if (cuts.Count == 0)
        {
            throw new DomainValidationException(
                nameof(cohortCutDates),
                "A sealed universe must state the cohort cut dates it was built from. Without them "
                + "no member's span can be derived.");
        }

        return new Universe(
            id,
            windowFrom,
            windowTo,
            cuts,
            sealedAtUtc,
            manifestContentHash,
            ValidateRuleText(populationDefinition, nameof(populationDefinition)),
            ValidateRuleText(membershipRule, nameof(membershipRule)),
            nowUtc);
    }

    /// <summary>
    /// Whether this version was sealed at or before <paramref name="asOf"/>.
    /// </summary>
    /// <remarks>
    /// The point-in-time selection rule in one predicate: a reader standing at an instant may see
    /// the versions already sealed by then and no others. Choosing the greatest such version is the
    /// caller's job, because which store it reads from is not this type's business.
    /// </remarks>
    public bool WasSealedBy(DateTime asOfUtc)
    {
        DateRange.EnsureUtc(asOfUtc, nameof(asOfUtc));

        return SealedAtUtc <= asOfUtc;
    }

    private static string ValidateRuleText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(
                parameterName,
                "A sealed universe records how its population and membership were decided.");
        }

        var trimmed = value.Trim();

        if (trimmed.Length > MaxRuleTextLength)
        {
            throw new DomainValidationException(
                parameterName,
                $"This text may not exceed {MaxRuleTextLength} characters.");
        }

        return trimmed;
    }
}
