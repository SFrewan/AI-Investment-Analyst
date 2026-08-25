namespace AI.Investment.Domain.Enums;

/// <summary>
/// State of the global stop control.
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> exists and is not a bug. If the switch's state cannot be determined -
/// the configuration is missing, the store is unreachable - the system must behave as though
/// it were engaged. A control that fails open is not a control.
/// </remarks>
public enum KillSwitchState
{
    /// <summary>State could not be determined. Treated exactly like <see cref="Engaged"/>.</summary>
    Unknown = 0,

    /// <summary>Stop. Nothing executes.</summary>
    Engaged = 1,

    /// <summary>Normal operation.</summary>
    Disengaged = 2,
}
