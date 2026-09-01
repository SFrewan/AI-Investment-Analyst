using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Sources;

/// <summary>
/// How much corroboration a source's information needs before it may be trusted.
/// </summary>
/// <remarks>
/// <para>
/// The mechanism behind the rule that unverified information must never silently become a fact.
/// A regulatory filing stands alone; a community aggregator does not, however plausible it
/// sounds. Expressing that as data on the source - rather than as a judgement made at each call
/// site - means the rule is applied consistently and can be reviewed in one place.
/// </para>
/// <para>
/// The defaults are conservative: a source that cannot confirm alone, needing two independent
/// corroborations. Registering a source without thinking about this yields caution, not trust.
/// </para>
/// </remarks>
public sealed record VerificationPolicy
{
    public const int MaxRequiredCorroborations = 5;

    private VerificationPolicy(bool canConfirmAlone, int requiredIndependentSources)
    {
        CanConfirmAlone = canConfirmAlone;
        RequiredIndependentSources = requiredIndependentSources;
    }

    /// <summary>
    /// Whether this source alone can produce information treated as
    /// <see cref="ConfirmationState.Confirmed"/>.
    /// </summary>
    public bool CanConfirmAlone { get; }

    /// <summary>
    /// How many independent sources must agree before information reaches
    /// <see cref="ConfirmationState.Confirmed"/> when this source cannot confirm alone.
    /// </summary>
    public int RequiredIndependentSources { get; }

    /// <summary>
    /// Self-sufficient: the originating record. A regulator, an exchange, a company about itself.
    /// </summary>
    /// <remarks>
    /// <strong>A fresh instance on every access, not a cached singleton.</strong> This type is
    /// mapped as an owned entity, and the persistence provider associates an owned instance with
    /// its owner by reference. One shared instance held by two owners in the same save is one
    /// object with two owners, which the provider resolves by writing one of them as null - it
    /// surfaced here as a not-null violation the first time two sources were seeded together.
    /// Value equality is unaffected: this is a record, so two instances with the same values are
    /// equal and hash alike. Same rule, same reason, as <c>LedgerAccount</c>.
    /// </remarks>
    public static VerificationPolicy Authoritative => new(true, 1);

    /// <summary>Needs one other source to agree. A fresh instance per access - see above.</summary>
    public static VerificationPolicy RequiresCorroboration => new(false, 2);

    /// <summary>
    /// The default: two independent corroborations, and never confirming alone. Fresh per access.
    /// </summary>
    public static VerificationPolicy Cautious => new(false, 3);

    public static VerificationPolicy Create(bool canConfirmAlone, int requiredIndependentSources)
    {
        if (requiredIndependentSources is < 1 or > MaxRequiredCorroborations)
        {
            throw new DomainValidationException(
                nameof(requiredIndependentSources),
                $"Required independent sources must be between 1 and {MaxRequiredCorroborations}. " +
                $"Received {requiredIndependentSources}.");
        }

        if (canConfirmAlone && requiredIndependentSources != 1)
        {
            throw new DomainValidationException(
                nameof(requiredIndependentSources),
                "A source that can confirm alone requires exactly one source - itself.");
        }

        return new VerificationPolicy(canConfirmAlone, requiredIndependentSources);
    }

    /// <summary>
    /// Classifies information carried by <paramref name="agreeingSourceCount"/> independent
    /// sources under this policy. Deterministic; no conflict resolution.
    /// </summary>
    public ConfirmationState Classify(int agreeingSourceCount)
    {
        if (agreeingSourceCount <= 0)
        {
            return ConfirmationState.Unverified;
        }

        if (CanConfirmAlone || agreeingSourceCount >= RequiredIndependentSources)
        {
            return ConfirmationState.Confirmed;
        }

        return agreeingSourceCount > 1
            ? ConfirmationState.PartiallyConfirmed
            : ConfirmationState.Unverified;
    }

    public override string ToString() =>
        CanConfirmAlone
            ? "confirms alone"
            : $"requires {RequiredIndependentSources} independent sources";
}
