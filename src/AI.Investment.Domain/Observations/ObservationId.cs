using System.Globalization;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Observations;

/// <summary>Identity of a single stored observation.</summary>
public readonly record struct ObservationId(Guid Value)
{
    public static ObservationId New() => new(Guid.NewGuid());

    public static ObservationId Create(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainValidationException(nameof(value), "An observation identifier may not be empty.");
        }

        return new ObservationId(value);
    }

    public override string ToString() => Value.ToString("d", CultureInfo.InvariantCulture);
}
