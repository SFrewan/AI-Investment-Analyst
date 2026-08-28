using System.Globalization;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Opportunities;

/// <summary>Identity of an opportunity.</summary>
/// <remarks>
/// A distinct type rather than a bare <see cref="Guid"/>, for the same reason <c>ClaimId</c> is one:
/// an opportunity identifier passed where a proposal identifier belongs is a mix-up the compiler
/// should catch rather than a test.
/// </remarks>
public readonly record struct OpportunityId(Guid Value)
{
    public static OpportunityId New() => new(Guid.NewGuid());

    public static OpportunityId Create(Guid value) =>
        value == Guid.Empty
            ? throw new DomainValidationException(nameof(value), "An opportunity identifier may not be empty.")
            : new OpportunityId(value);

    public override string ToString() => Value.ToString("d", CultureInfo.InvariantCulture);
}
