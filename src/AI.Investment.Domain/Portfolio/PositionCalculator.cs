using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Portfolio;

/// <summary>
/// Replays position events into positions. Deterministic, and the only place that does it.
/// </summary>
/// <remarks>
/// <para>
/// One implementation, used by the read model, by the exposure provider that feeds the limit engine
/// and by every test. Two replays that disagreed would mean the number a concentration ceiling is
/// compared against was not the number an operator was shown.
/// </para>
/// <para>
/// <strong>Order is by occurrence, then by the venue's reference.</strong> The timestamp alone is
/// not a total order - two fills of the same instrument can share one - and a replay whose result
/// depended on the order rows came back from a database would be a position that changed when an
/// index did. The reference is unique, so the composite is total.
/// </para>
/// <para>
/// A disposal that exceeds the holding refuses rather than being clamped. Clamping would silently
/// convert a defect - a fill applied against the wrong instrument, a missing acquisition - into a
/// plausible position.
/// </para>
/// </remarks>
public static class PositionCalculator
{
    /// <summary>Every instrument that has ever had an event, closed positions included.</summary>
    /// <remarks>
    /// Closed positions are kept in the result rather than dropped: their realised profit is part of
    /// the portfolio's account of itself, and an instrument that vanished the moment it was sold
    /// would take that with it.
    /// </remarks>
    public static IReadOnlyList<Position> Replay(IEnumerable<PositionEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var byInstrument = new Dictionary<string, Position>(StringComparer.OrdinalIgnoreCase);

        foreach (var change in Ordered(events))
        {
            var current = byInstrument.TryGetValue(change.Instrument, out var existing)
                ? existing
                : Position.Flat(change.Instrument, change.Price.Currency);

            byInstrument[change.Instrument] = Apply(current, change);
        }

        return byInstrument.Values
            .OrderBy(position => position.Instrument, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>One instrument's position, or a flat one when it has no events.</summary>
    public static Position ReplayFor(
        string instrument,
        Currency currency,
        IEnumerable<PositionEvent> events)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instrument);
        ArgumentNullException.ThrowIfNull(currency);
        ArgumentNullException.ThrowIfNull(events);

        var relevant = events.Where(e =>
            string.Equals(e.Instrument, instrument.Trim(), StringComparison.OrdinalIgnoreCase));

        var position = Position.Flat(instrument.Trim(), currency);

        foreach (var change in Ordered(relevant))
        {
            position = Apply(position, change);
        }

        return position;
    }

    private static Position Apply(Position position, PositionEvent change) =>
        change.Change switch
        {
            PositionChange.Acquired => position.Acquire(change.Quantity, change.Notional),
            PositionChange.Disposed => position.Dispose(change.Quantity, change.Notional),

            // Unreachable: the event refuses to be constructed with any other value. Stated so
            // that a future member added to the enum fails here rather than being ignored.
            _ => throw new DomainRuleViolationException(
                "PositionCalculator.UnknownChange",
                $"A position event carries an unrecognised change: {change.Change}."),
        };

    private static IEnumerable<PositionEvent> Ordered(IEnumerable<PositionEvent> events) =>
        events
            .OrderBy(e => e.OccurredAtUtc)
            .ThenBy(e => e.VenueReference, StringComparer.Ordinal);
}
