namespace AI.Investment.Application.Abstractions;

/// <summary>Thrown when a request fails input validation before reaching the domain.</summary>
/// <remarks>
/// Application-level validation catches shape problems - a missing field, a page size out of
/// range - so that callers get a clear list rather than the first domain exception that
/// happens to fire. It does not duplicate domain invariants: the domain remains the authority
/// on what a valid company is.
/// </remarks>
public sealed class ValidationFailedException : Exception
{
    public ValidationFailedException(IEnumerable<string> errors)
        : base("The request failed validation.")
    {
        Errors = errors?.ToList() ?? [];
    }

    public IReadOnlyList<string> Errors { get; }
}
