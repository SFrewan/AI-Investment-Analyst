namespace AI.Investment.Domain.Exceptions;

/// <summary>
/// Thrown when a value object or entity is constructed from input it cannot legally represent.
/// </summary>
/// <remarks>
/// This is the mechanism that makes "invalid states are unrepresentable" true rather than
/// aspirational: an invalid value never becomes an instance in the first place, so no
/// downstream code has to defend against it.
/// </remarks>
public sealed class DomainValidationException : DomainException
{
    public DomainValidationException(string parameterName, string message)
        : base($"{parameterName}: {message}")
    {
        ParameterName = parameterName;
        Reason = message;
    }

    public string ParameterName { get; }

    public string Reason { get; }
}
