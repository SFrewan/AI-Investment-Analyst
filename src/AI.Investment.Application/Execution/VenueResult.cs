using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Application.Execution;

/// <summary>A venue either filled the order or refused it, and says which.</summary>
/// <remarks>
/// A refusal is a value rather than an exception, for the same reason a provider failure is: the
/// caller has to record the attempt either way, and an exception thrown across this boundary loses
/// that unless every call site remembers to catch it.
/// </remarks>
public sealed record VenueResult
{
    private VenueResult(VenueFill? fill, string? refusal)
    {
        Fill = fill;
        Refusal = refusal;
    }

    /// <summary>Present when the order was filled.</summary>
    public VenueFill? Fill { get; }

    /// <summary>Present when it was not.</summary>
    public string? Refusal { get; }

    public bool Filled => Fill is not null;

    public static VenueResult Ok(VenueFill fill)
    {
        ArgumentNullException.ThrowIfNull(fill);

        return new VenueResult(fill, null);
    }

    public static VenueResult Rejected(string refusal)
    {
        if (string.IsNullOrWhiteSpace(refusal))
        {
            throw new DomainValidationException(
                nameof(refusal),
                "A venue refusal must state a reason. 'Rejected' alone is not something an operator " +
                "can act on or a retry policy can reason about.");
        }

        return new VenueResult(null, refusal.Trim());
    }

    /// <summary>The fill, or an exception naming the refusal.</summary>
    public VenueFill RequireFill() =>
        Fill ?? throw new DomainRuleViolationException(
            "VenueResult.NotFilled",
            $"The venue refused the order: {Refusal}");

    public override string ToString() => Filled ? Fill!.ToString() : $"rejected: {Refusal}";
}
