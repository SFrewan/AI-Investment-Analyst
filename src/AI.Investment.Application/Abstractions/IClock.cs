namespace AI.Investment.Application.Abstractions;

/// <summary>Supplies the current time.</summary>
/// <remarks>
/// Nothing in the domain or the application reads <see cref="DateTime.UtcNow"/> directly. Two
/// reasons, and the second matters more than the first: a type that reaches for ambient state
/// cannot be tested deterministically, and this platform will eventually replay historical
/// decisions - at which point "now" is genuinely an input rather than a fact about the machine.
/// </remarks>
public interface IClock
{
    /// <summary>The current instant, always <see cref="DateTimeKind.Utc"/>.</summary>
    DateTime UtcNow { get; }
}
