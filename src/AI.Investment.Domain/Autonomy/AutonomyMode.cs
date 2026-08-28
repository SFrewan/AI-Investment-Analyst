namespace AI.Investment.Domain.Autonomy;

/// <summary>
/// How much a capability may do without a human, resolved per action.
/// </summary>
/// <remarks>
/// <para>
/// The ordering is meaningful and load-bearing: higher values are more permissive, and resolution
/// takes the <em>minimum</em> of everything that applies. That is what makes narrowing the only
/// direction a rule can move an answer - a ceiling can lower a mode, and nothing can raise one.
/// </para>
/// <para>
/// <see cref="Unknown"/> is zero on purpose. A default-initialised field, a deserialisation that
/// missed a value and a resolution that found nothing all land here, and all of them deny. The
/// alternative - making the safe value non-zero - means every one of those paths reads as a
/// permission nobody granted.
/// </para>
/// </remarks>
public enum AutonomyMode
{
    /// <summary>No grant resolved, or resolution failed. Denies, exactly like <see cref="Off"/>.</summary>
    Unknown = 0,

    /// <summary>L0. The capability is switched off entirely.</summary>
    Off = 1,

    /// <summary>L1. May collect and analyse. Produces no proposals.</summary>
    ResearchOnly = 2,

    /// <summary>L2. May produce recommendations. A human initiates everything.</summary>
    Advise = 3,

    /// <summary>L3. Assembles complete, executable proposals. A human approves each one.</summary>
    PrepareForApproval = 4,

    /// <summary>L4. Executes automatically within a named limit set. Anything outside escalates.</summary>
    AutoExecuteBounded = 5,

    /// <summary>L5. Operates on its own schedule within policy, escalating only exceptions.</summary>
    ContinuousBounded = 6,
}
