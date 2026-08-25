namespace AI.Investment.Domain.Exceptions;

/// <summary>
/// Thrown when arithmetic is attempted between two <c>Money</c> values in different currencies.
/// </summary>
/// <remarks>
/// There is deliberately no implicit conversion and no ambient exchange rate. Silent currency
/// coercion is one of the two classic sources of expensive, invisible error in financial
/// software (the other being an unconstrained instrument identifier). Conversion, when it
/// exists, will be an explicit operation carrying its own rate, source and timestamp.
/// </remarks>
public sealed class CurrencyMismatchException : DomainException
{
    public CurrencyMismatchException(string left, string right)
        : base($"Cannot combine amounts in different currencies: '{left}' and '{right}'. An explicit conversion with a dated rate is required.")
    {
        Left = left;
        Right = right;
    }

    public string Left { get; }

    public string Right { get; }
}
