using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Evidence;

/// <summary>Identity of a single claim.</summary>
/// <remarks>
/// A distinct type rather than a bare <see cref="Guid"/> so that a claim identity cannot be
/// passed where a company or proposal identity is expected. Every such mix-up caught by the
/// compiler is one that does not have to be caught by a test.
/// </remarks>
public readonly record struct ClaimId(Guid Value)
{
    public static ClaimId New() => new(Guid.NewGuid());

    public static ClaimId Create(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainValidationException(nameof(value), "A claim identifier may not be empty.");
        }

        return new ClaimId(value);
    }

    public override string ToString() => Value.ToString("d", System.Globalization.CultureInfo.InvariantCulture);
}
