using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Autonomy;

/// <summary>Why a live execution venue may not be activated.</summary>
/// <remarks>
/// <see cref="None"/> is zero and never returned by <see cref="LiveVenueGate.Evaluate"/> in this
/// phase, because no authorisation exists that could produce it. It exists so that the type is total
/// rather than as a state anything can currently reach.
/// </remarks>
public enum LiveVenueRefusal
{
    /// <summary>Activation permitted. Unreachable today.</summary>
    None = 0,

    /// <summary>No authorisation record exists. The default, and the safe one.</summary>
    NotAuthorised = 1,

    /// <summary>The authorisation exists but has been withdrawn.</summary>
    Withdrawn = 2,

    /// <summary>The authorisation exists but has expired.</summary>
    Expired = 3,

    /// <summary>The promotion warrant it rests on is missing or no longer valid.</summary>
    WarrantNoLongerValid = 4,

    /// <summary>Only one person signed it. A live venue takes two.</summary>
    SecondSignatureMissing = 5,

    /// <summary>It names an environment other than the one asking.</summary>
    EnvironmentMismatch = 6,

    /// <summary>It names a venue other than the one asking.</summary>
    VenueMismatch = 7,

    /// <summary>The request came from configuration rather than from a decision.</summary>
    ConfigurationIsNotAuthorisation = 8,
}

/// <summary>
/// A written decision by two named people that one venue, in one environment, may move real money.
/// </summary>
/// <remarks>
/// <para>
/// This is the formal gate the roadmap calls "a separate decision", modelled as an artefact rather
/// than as a setting. The distinction is the whole point. A boolean in configuration can be flipped
/// by anybody with deployment access, arrives with no reasoning attached, and looks identical in a
/// diff whether it was considered for a month or typed at midnight. An authorisation is a record
/// with two names, a justification, a warrant it rests on, an expiry, and an audit trail.
/// </para>
/// <para>
/// <strong>Two people, and they must be different people.</strong> The one control that is hard to
/// defeat by accident is requiring somebody else to agree. The type refuses an authorisation whose
/// two signatures are the same person, compared case-insensitively so that a different capitalisation
/// of the same name does not pass for a second reviewer.
/// </para>
/// <para>
/// <strong>Nothing in this phase creates one.</strong> There is no live venue to authorise, no
/// registered implementation that is not simulated, and no promotion warrant that could underwrite
/// one. The gate is implemented and audited so that the day somebody wants to activate a venue, the
/// path they have to walk already exists and already refuses - rather than being designed under the
/// pressure of wanting the answer to be yes.
/// </para>
/// </remarks>
public sealed class LiveVenueAuthorization
{
    public const string SameSignatoryRule = "LiveVenueAuthorization.TwoDifferentPeople";

    public const string NoWarrantRule = "LiveVenueAuthorization.RequiresAPromotionWarrant";

    /// <summary>The longest a live-venue authorisation may run before it is re-argued.</summary>
    public const int MaxValidityDays = 7;

    public const int MaxSignatoryLength = 120;

    public const int MaxVenueIdLength = 60;

    public const int MaxJustificationLength = 2000;

    private LiveVenueAuthorization(
        Guid liveVenueAuthorizationId,
        string venueId,
        string environmentName,
        Guid promotionWarrantId,
        string authorisedBy,
        string counterSignedBy,
        string justification,
        Money exposureCeiling,
        DateTime authorisedAtUtc,
        DateTime expiresAtUtc)
    {
        LiveVenueAuthorizationId = liveVenueAuthorizationId;
        VenueId = venueId;
        EnvironmentName = environmentName;
        PromotionWarrantId = promotionWarrantId;
        AuthorisedBy = authorisedBy;
        CounterSignedBy = counterSignedBy;
        Justification = justification;
        ExposureCeiling = exposureCeiling;
        AuthorisedAtUtc = authorisedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    private LiveVenueAuthorization()
    {
        VenueId = string.Empty;
        EnvironmentName = string.Empty;
        AuthorisedBy = string.Empty;
        CounterSignedBy = string.Empty;
        Justification = string.Empty;
        ExposureCeiling = null!;
    }

    public Guid LiveVenueAuthorizationId { get; private set; }

    public string VenueId { get; private set; }

    public string EnvironmentName { get; private set; }

    /// <summary>The promotion warrant this rests on. A live venue is never authorised on its own.</summary>
    public Guid PromotionWarrantId { get; private set; }

    public string AuthorisedBy { get; private set; }

    /// <summary>The second person. Must be a different one.</summary>
    public string CounterSignedBy { get; private set; }

    public string Justification { get; private set; }

    /// <summary>The most real money this authorisation covers.</summary>
    public Money ExposureCeiling { get; private set; }

    public DateTime AuthorisedAtUtc { get; private set; }

    public DateTime ExpiresAtUtc { get; private set; }

    public DateTime? WithdrawnAtUtc { get; private set; }

    public string? WithdrawalReason { get; private set; }

    public bool IsWithdrawn => WithdrawnAtUtc is not null;

    public bool HasExpired(DateTime nowUtc) => nowUtc >= ExpiresAtUtc;

    public static LiveVenueAuthorization Create(
        string venueId,
        string environmentName,
        PromotionWarrant warrant,
        string authorisedBy,
        string counterSignedBy,
        string justification,
        Money exposureCeiling,
        DateTime nowUtc,
        TimeSpan validFor)
    {
        ArgumentNullException.ThrowIfNull(exposureCeiling);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (warrant is null)
        {
            throw new DomainRuleViolationException(
                NoWarrantRule,
                "a live venue is never authorised on its own. It rests on a promotion warrant, which " +
                "rests on measured evidence, which somebody argued from.");
        }

        if (!warrant.IsActive(nowUtc))
        {
            throw new DomainRuleViolationException(
                NoWarrantRule,
                $"promotion warrant {warrant.PromotionWarrantId:d} is not active, so nothing may be " +
                "authorised on the strength of it.");
        }

        var first = Text(authorisedBy, nameof(authorisedBy), MaxSignatoryLength,
            "A live-venue authorisation must name the person who made the decision.");

        var second = Text(counterSignedBy, nameof(counterSignedBy), MaxSignatoryLength,
            "A live-venue authorisation must name a second person who agreed with it.");

        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainRuleViolationException(
                SameSignatoryRule,
                $"'{first}' cannot be both signatures. The one control that is hard to defeat by " +
                "accident is requiring somebody else to agree, and a self-countersigned decision " +
                "defeats it exactly.");
        }

        if (exposureCeiling.IsNegative || exposureCeiling.IsZero)
        {
            throw new DomainValidationException(
                nameof(exposureCeiling),
                "A live-venue authorisation must state a positive ceiling on real money. An " +
                "unbounded or absent one is the authorisation nobody meant to give.");
        }

        if (validFor <= TimeSpan.Zero || validFor > TimeSpan.FromDays(MaxValidityDays))
        {
            throw new DomainValidationException(
                nameof(validFor),
                $"A live-venue authorisation must expire, and may not run longer than " +
                $"{MaxValidityDays} days. It is the most consequential permission in the system.");
        }

        return new LiveVenueAuthorization(
            Guid.NewGuid(),
            Text(venueId, nameof(venueId), MaxVenueIdLength, "An authorisation names one venue."),
            Text(environmentName, nameof(environmentName), 60, "An authorisation names one environment."),
            warrant.PromotionWarrantId,
            first,
            second,
            Text(justification, nameof(justification), MaxJustificationLength,
                "An authorisation must record why, at length. This is the record somebody reads " +
                "afterwards when they are trying to understand what was being thought."),
            exposureCeiling,
            nowUtc,
            nowUtc.Add(validFor));
    }

    public void Withdraw(string reason, DateTime nowUtc)
    {
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (IsWithdrawn)
        {
            return;
        }

        WithdrawnAtUtc = nowUtc;
        WithdrawalReason = Text(reason, nameof(reason), AutonomyGrant.MaxReasonLength,
            "A withdrawal must state a reason.");
    }

    public override string ToString() =>
        $"live-venue authorisation {LiveVenueAuthorizationId} for {VenueId} @{EnvironmentName}, " +
        $"expires {ExpiresAtUtc:O}";

    private static string Text(string? value, string parameterName, int maxLength, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(parameterName, message);
        }

        var trimmed = value.Trim();

        if (trimmed.Length > maxLength)
        {
            throw new DomainValidationException(
                parameterName,
                $"'{parameterName}' may not exceed {maxLength} characters.");
        }

        return trimmed;
    }
}

/// <summary>What is being asked, and on whose authority.</summary>
/// <param name="VenueId">The venue asking to be activated.</param>
/// <param name="EnvironmentName">The environment it would be activated in.</param>
/// <param name="Authorization">The authorisation record, when one exists.</param>
/// <param name="Warrant">The promotion warrant it rests on, when one exists.</param>
/// <param name="RequestedFromConfiguration">
/// True when the request originated in a configuration value rather than in a decision. Always
/// refused: see <see cref="LiveVenueRefusal.ConfigurationIsNotAuthorisation"/>.
/// </param>
public sealed record LiveVenueRequest(
    string VenueId,
    string EnvironmentName,
    LiveVenueAuthorization? Authorization,
    PromotionWarrant? Warrant,
    bool RequestedFromConfiguration);

/// <summary>The verdict, and why.</summary>
public sealed record LiveVenueDecision(LiveVenueRefusal Refusal, string Explanation)
{
    public bool MayActivate => Refusal == LiveVenueRefusal.None;

    public override string ToString() => Explanation;
}

/// <summary>
/// Whether a live execution venue may be activated. Pure, total, and refusing by construction.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Configuration is not authorisation.</strong> The first thing this checks is whether the
/// request came from a settings file or an environment variable, and if it did the answer is no
/// whatever else is true. That check is first rather than last on purpose: it means an installation
/// that has somehow acquired a valid authorisation still cannot activate a venue by writing
/// <c>true</c> somewhere, because the path a configuration value takes is refused before the
/// authorisation is even looked at.
/// </para>
/// <para>
/// Everything else is the ordinary list: an authorisation must exist, be neither withdrawn nor
/// expired, rest on a live warrant, carry two different names, and name this venue in this
/// environment. Each failure is its own refusal so that the audit record says which one.
/// </para>
/// </remarks>
public static class LiveVenueGate
{
    public static LiveVenueDecision Evaluate(LiveVenueRequest request, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateRange.EnsureUtc(nowUtc, nameof(nowUtc));

        if (request.RequestedFromConfiguration)
        {
            return Refuse(
                LiveVenueRefusal.ConfigurationIsNotAuthorisation,
                "a live venue cannot be activated by a configuration value. A setting can be flipped " +
                "by anybody with deployment access, arrives with no reasoning attached, and looks the " +
                "same in a diff whether it was considered for a month or typed at midnight.");
        }

        if (request.Authorization is null)
        {
            return Refuse(
                LiveVenueRefusal.NotAuthorised,
                "no live-venue authorisation exists. This is the default state, and the correct one.");
        }

        var authorization = request.Authorization;

        if (authorization.IsWithdrawn)
        {
            return Refuse(
                LiveVenueRefusal.Withdrawn,
                $"authorisation {authorization.LiveVenueAuthorizationId:d} was withdrawn: " +
                $"{authorization.WithdrawalReason}");
        }

        if (authorization.HasExpired(nowUtc))
        {
            return Refuse(
                LiveVenueRefusal.Expired,
                $"authorisation {authorization.LiveVenueAuthorizationId:d} expired at " +
                $"{authorization.ExpiresAtUtc:O}.");
        }

        if (request.Warrant is null ||
            !request.Warrant.IsActive(nowUtc) ||
            request.Warrant.PromotionWarrantId != authorization.PromotionWarrantId)
        {
            return Refuse(
                LiveVenueRefusal.WarrantNoLongerValid,
                "the promotion warrant this authorisation rests on is missing, expired, revoked or a " +
                "different warrant entirely.");
        }

        if (string.Equals(authorization.AuthorisedBy, authorization.CounterSignedBy, StringComparison.OrdinalIgnoreCase))
        {
            // Unreachable through Create, which refuses it. Checked again here because this is the
            // last question asked before real money could move, and a rule this important is worth
            // asking twice from two directions.
            return Refuse(
                LiveVenueRefusal.SecondSignatureMissing,
                "the authorisation carries one person's name twice.");
        }

        if (!string.Equals(authorization.EnvironmentName, request.EnvironmentName?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Refuse(
                LiveVenueRefusal.EnvironmentMismatch,
                $"the authorisation covers '{authorization.EnvironmentName}', not " +
                $"'{request.EnvironmentName}'.");
        }

        if (!string.Equals(authorization.VenueId, request.VenueId?.Trim(), StringComparison.Ordinal))
        {
            return Refuse(
                LiveVenueRefusal.VenueMismatch,
                $"the authorisation covers venue '{authorization.VenueId}', not '{request.VenueId}'.");
        }

        return new LiveVenueDecision(
            LiveVenueRefusal.None,
            $"venue '{authorization.VenueId}' is authorised in '{authorization.EnvironmentName}' by " +
            $"{authorization.AuthorisedBy}, counter-signed by {authorization.CounterSignedBy}, until " +
            $"{authorization.ExpiresAtUtc:O}.");
    }

    private static LiveVenueDecision Refuse(LiveVenueRefusal refusal, string explanation) =>
        new(refusal, explanation);
}
