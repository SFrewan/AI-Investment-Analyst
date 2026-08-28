using System.Globalization;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Ai;

/// <summary>One named piece of evidence: what it is, and the claim that carries its value.</summary>
/// <remarks>
/// The name is not decoration. A bundle of anonymous numbers is not analysable by anything, human
/// or model - <c>1000</c> means nothing, <c>financials.revenue = 1000</c> means something - and
/// naming the item at the point it enters the bundle is what keeps the agent from having to infer
/// what a figure is from its magnitude.
/// </remarks>
public sealed record EvidenceItem
{
    public const int MaxNameLength = 120;

    private EvidenceItem(string name, Claim claim)
    {
        Name = name;
        Claim = claim;
    }

    /// <summary>What this is - <c>financials.revenue</c>, <c>news.headline</c>.</summary>
    public string Name { get; }

    public Claim Claim { get; }

    public static EvidenceItem Create(string name, Claim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainValidationException(
                nameof(name),
                "A piece of evidence must be named. An unnamed value cannot be cited, compared or " +
                "checked against anything.");
        }

        var trimmed = name.Trim();

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainValidationException(
                nameof(name),
                $"An evidence name may not exceed {MaxNameLength} characters.");
        }

        return new EvidenceItem(trimmed, claim);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Name}={Claim.UntypedValue}");
}
