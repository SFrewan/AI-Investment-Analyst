namespace AI.Investment.Domain.Enums;

/// <summary>
/// The epistemic status of a value: how the system came to believe it.
/// </summary>
/// <remarks>
/// This is the mechanical form of the platform's mandatory FACT / CALCULATION /
/// AI INTERPRETATION / PREDICTION distinction. It exists as a type rather than a UI convention
/// so that no downstream code can present a model's guess in the same way as a filed figure.
/// </remarks>
public enum ClaimKind
{
    /// <summary>An observation obtained from a source. Carries provenance, never confidence.</summary>
    Fact = 0,

    /// <summary>Derived arithmetically from other claims. Exact given its inputs.</summary>
    Calculation = 1,

    /// <summary>A model's reading of evidence. Requires confidence and supporting evidence.</summary>
    AiInterpretation = 2,

    /// <summary>A claim about the future. Requires confidence and supporting evidence.</summary>
    Prediction = 3,
}
