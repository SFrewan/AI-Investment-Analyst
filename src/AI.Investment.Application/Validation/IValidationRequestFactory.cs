namespace AI.Investment.Application.Validation;

/// <summary>Supplies the declared validation request.</summary>
/// <remarks>
/// <para>
/// The window, the horizon, the event threshold and the benchmark are the four choices that decide
/// what a validation result means, and all four can be used to manufacture a favourable one. They are
/// therefore not parameters a caller supplies: they come from configuration under change control, and
/// this port is how the measurement reads them.
/// </para>
/// <para>
/// It lives in the application layer rather than beside its implementation so that a controller can
/// depend on it without depending on Infrastructure - the same rule every other port here follows,
/// and one an architecture test enforces.
/// </para>
/// </remarks>
public interface IValidationRequestFactory
{
    ValidationRequest Create();
}
