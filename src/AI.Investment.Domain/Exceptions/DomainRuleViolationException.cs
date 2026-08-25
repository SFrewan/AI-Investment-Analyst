namespace AI.Investment.Domain.Exceptions;

/// <summary>
/// Thrown when an operation on a valid entity would break an invariant.
/// </summary>
public sealed class DomainRuleViolationException : DomainException
{
    public DomainRuleViolationException(string rule, string message)
        : base($"{rule}: {message}")
    {
        Rule = rule;
    }

    public string Rule { get; }
}
