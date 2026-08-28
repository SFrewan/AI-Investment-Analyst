using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Execution;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AI.Investment.Infrastructure.Execution;

/// <summary>
/// The only execution venue this platform has: it fills orders on paper, deterministically.
/// </summary>
/// <remarks>
/// <para>
/// Everything about a real execution happens here except the money: the proposal was policy-checked,
/// the limits were evaluated, an approval token was consumed, ledger entries are posted and audit
/// records written. That is the point of §L.6 - the simulated path is the production path, so
/// switching to a real venue changes one registration rather than revealing which parts were never
/// exercised.
/// </para>
/// <para>
/// Deterministic on purpose. It fills at the price the caller stated rather than inventing slippage,
/// and its venue reference is derived from the order's idempotency key, so a replayed order produces
/// the same reference and a duplicate is visible rather than merely likely. Modelled slippage is a
/// backtesting concern and belongs where the model can be stated and varied, not buried in a venue
/// that quietly makes every result slightly worse.
/// </para>
/// <para>
/// It refuses a currency it does not settle in rather than converting. A silent conversion buries
/// an exchange rate nobody recorded in the middle of a fill.
/// </para>
/// </remarks>
public sealed class SimulatedVenue : IExecutionVenue
{
    public const string Id = "simulated";

    private readonly IOptions<SimulatedVenueOptions> _options;
    private readonly IClock _clock;

    public SimulatedVenue(IOptions<SimulatedVenueOptions> options, IClock clock)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public string VenueId => Id;

    public bool IsSimulated => true;

    public Task<VenueResult> PlaceAsync(VenueOrder order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        cancellationToken.ThrowIfCancellationRequested();

        var settings = _options.Value;
        var currency = Currency.Create(settings.CurrencyCode);

        if (order.Price.Currency != currency)
        {
            return Task.FromResult(VenueResult.Rejected(
                $"This venue settles in {currency} but the order is priced in {order.Price.Currency}. " +
                "Converting silently would bury an exchange rate nobody recorded."));
        }

        var notional = order.Notional;
        var commission = notional.Abs().MultiplyBy(settings.CommissionRate);
        var minimum = Money.Create(settings.MinimumFee, currency);
        var fees = commission.IsGreaterThan(minimum) ? commission : minimum;

        return Task.FromResult(VenueResult.Ok(
            VenueFill.Create(
                Reference(order),
                order.Quantity,
                order.Price,
                fees,
                _clock.UtcNow)));
    }

    /// <summary>
    /// A stable reference for this order.
    /// </summary>
    /// <remarks>
    /// Derived from the idempotency key rather than randomly generated, so that a replay of the same
    /// order yields the same reference. A random one would make two fills of the same order look
    /// like two different fills, which is exactly the confusion idempotency exists to remove.
    /// </remarks>
    private static string Reference(VenueOrder order)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(order.IdempotencyKey));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Id}-{Convert.ToHexString(digest)[..16].ToLowerInvariant()}");
    }
}
