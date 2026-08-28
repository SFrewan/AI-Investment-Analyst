using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Autonomy;

/// <summary>
/// The answer to "how much may this action do without a human?", with the reasoning attached.
/// </summary>
/// <remarks>
/// <para>
/// Total: there is a resolution for every request, including the requests nothing covers. A missing
/// grant produces <see cref="AutonomyMode.Unknown"/> with a reason, not an exception and not a null,
/// because a caller that has to handle an exception is a caller that can catch it and continue.
/// </para>
/// <para>
/// Immutable and with no widening method. A resolution is the output of
/// <see cref="AutonomyResolver"/> and nothing downstream can raise it - the policy engine may narrow
/// an outcome on the strength of one, never broaden it.
/// </para>
/// </remarks>
public sealed record AutonomyResolution
{
    private AutonomyResolution(
        AutonomyMode mode,
        ExposureBand band,
        Guid? autonomyGrantId,
        string reason)
    {
        Mode = mode;
        Band = band;
        AutonomyGrantId = autonomyGrantId;
        Reason = reason;
    }

    public AutonomyMode Mode { get; }

    public ExposureBand Band { get; }

    /// <summary>The grant this resolution rests on, when one was found.</summary>
    public Guid? AutonomyGrantId { get; }

    /// <summary>Why, in terms somebody reading the audit trail can act on.</summary>
    public string Reason { get; }

    /// <summary>True when the resolved mode permits an action to execute with no human in the loop.</summary>
    public bool PermitsUnattendedExecution => Mode >= AutonomyMode.AutoExecuteBounded;

    /// <summary>True when the resolved mode permits nothing at all.</summary>
    public bool Denies => Mode <= AutonomyMode.Off;

    /// <summary>
    /// The mode one level above the resolved one, which is what shadow mode measures against.
    /// </summary>
    /// <remarks>
    /// Capped at <see cref="AutonomyMode.ContinuousBounded"/>, and it is a value used to <em>record
    /// what would have happened</em>. Nothing consumes it as authority: see <c>ShadowDecision</c>,
    /// which has no execution surface at all.
    /// </remarks>
    public AutonomyMode NextModeUp =>
        Mode >= AutonomyMode.ContinuousBounded ? AutonomyMode.ContinuousBounded : Mode + 1;

    internal static AutonomyResolution Create(
        AutonomyMode mode,
        ExposureBand band,
        Guid? autonomyGrantId,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainValidationException(
                nameof(reason),
                "An autonomy resolution must state a reason. A refusal nobody can explain is a " +
                "refusal nobody can fix, and a permission nobody can explain is worse.");
        }

        return new AutonomyResolution(mode, band, autonomyGrantId, reason.Trim());
    }

    public override string ToString() => $"{Mode} ({Band}): {Reason}";
}
