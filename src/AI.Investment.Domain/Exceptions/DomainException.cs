namespace AI.Investment.Domain.Exceptions;

/// <summary>
/// Base type for every failure raised by the domain because a rule was broken.
/// </summary>
/// <remarks>
/// Distinct from a general <see cref="InvalidOperationException"/> so that the API layer can
/// map "the caller sent something the business rules reject" (a 4xx) apart from "something in
/// the system is broken" (a 5xx) without inspecting messages.
/// </remarks>
public abstract class DomainException : Exception
{
    protected DomainException(string message)
        : base(message)
    {
    }

    protected DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
