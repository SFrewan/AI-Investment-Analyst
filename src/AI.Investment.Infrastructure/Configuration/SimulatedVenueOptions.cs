using System.ComponentModel.DataAnnotations;

namespace AI.Investment.Infrastructure.Configuration;

/// <summary>What the simulated venue charges, and in what currency.</summary>
/// <remarks>
/// Configurable rather than hard-coded because the cost of trading is the part of a strategy's
/// result that is knowable in advance and most often left out - and a simulation that charges
/// nothing produces a result that cannot be compared with a real one.
/// </remarks>
public sealed class SimulatedVenueOptions
{
    public const string SectionName = "SimulatedVenue";

    /// <summary>Commission as a fraction of consideration.</summary>
    [Range(0d, 0.1d)]
    public decimal CommissionRate { get; init; } = 0.001m;

    /// <summary>The floor under the commission, in <see cref="CurrencyCode"/>.</summary>
    [Range(0d, 1000d)]
    public decimal MinimumFee { get; init; } = 1m;

    /// <summary>ISO code of the currency this venue settles in.</summary>
    [Required]
    [MinLength(3)]
    [MaxLength(3)]
    public string CurrencyCode { get; init; } = "USD";
}
