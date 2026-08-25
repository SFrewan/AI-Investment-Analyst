using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Companies;

/// <summary>Identity of a company.</summary>
public readonly record struct CompanyId(Guid Value)
{
    public static CompanyId New() => new(Guid.NewGuid());

    public static CompanyId Create(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainValidationException(nameof(value), "A company identifier may not be empty.");
        }

        return new CompanyId(value);
    }

    public override string ToString() =>
        Value.ToString("d", System.Globalization.CultureInfo.InvariantCulture);
}
