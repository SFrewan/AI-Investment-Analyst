using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Analytics.Financial;

/// <summary>One figure and the sign it enters a sum with.</summary>
/// <remarks>
/// A coefficient rather than an operator so subtraction and addition are the same code path:
/// free cash flow is operating cash flow at +1 and capital expenditure at -1.
/// </remarks>
public sealed record SumTerm
{
    private SumTerm(string attribute, decimal coefficient)
    {
        Attribute = attribute;
        Coefficient = coefficient;
    }

    public string Attribute { get; }

    public decimal Coefficient { get; }

    public static SumTerm Create(string attribute, decimal coefficient)
    {
        if (string.IsNullOrWhiteSpace(attribute))
        {
            throw new DomainValidationException(
                nameof(attribute),
                "A term must name the figure it reads.");
        }

        if (coefficient == 0m)
        {
            throw new DomainValidationException(
                nameof(coefficient),
                $"A coefficient of zero removes '{attribute}' from the formula while leaving it " +
                "listed as an input, which would misstate what the result depends on.");
        }

        return new SumTerm(attribute.Trim().ToLowerInvariant(), coefficient);
    }

    public static SumTerm Plus(string attribute) => Create(attribute, 1m);

    public static SumTerm Minus(string attribute) => Create(attribute, -1m);

    public override string ToString() => Coefficient < 0 ? $"- {Attribute}" : $"+ {Attribute}";
}
