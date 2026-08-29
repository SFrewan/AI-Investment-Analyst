using System.Globalization;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Opportunities;

namespace AI.Investment.Application.Opportunities;

/// <summary>
/// Proposed recording of one discovered candidate, as the safety seam sees it.
/// </summary>
/// <remarks>
/// <para>
/// Describes what is being written down - which type, which instrument, which discoverer, how much
/// evidence - rather than the reasoning behind it. The audit trail is append-only and cannot be
/// redacted, and a licensed vendor's prices are exactly what should not be copied into it.
/// </para>
/// <para>
/// <see cref="Describe"/> is one of the components hashed into the action fingerprint an approval is
/// bound to, so every field that changes what would actually be recorded appears in it.
/// </para>
/// </remarks>
public sealed record OpportunityCandidateParameters : IActionParameters
{
    public OpportunityCandidateParameters(
        OpportunityType type,
        string discovererId,
        string instrument,
        int evidenceCount)
    {
        ArgumentNullException.ThrowIfNull(type);

        Type = type;
        DiscovererId = discovererId;
        Instrument = instrument;
        EvidenceCount = evidenceCount;
    }

    public OpportunityType Type { get; }

    /// <summary>The registered producer that found it.</summary>
    public string DiscovererId { get; }

    public string Instrument { get; }

    /// <summary>How many stored observations the candidate cites.</summary>
    public int EvidenceCount { get; }

    public string Describe() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Record a '{Type}' candidate for {Instrument}, found by {DiscovererId}, citing " +
            $"{EvidenceCount} observations. Recording only: no order, no venue, no capital.");
}
