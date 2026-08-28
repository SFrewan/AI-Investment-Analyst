using System.Globalization;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Watching;

/// <summary>
/// One observation offered to the watches: what kind of thing, about what, how much, and when.
/// </summary>
/// <remarks>
/// <para>
/// The only input a watch is evaluated against. It carries numbers and timestamps and nothing else -
/// no text for a model to read, no free-form payload, no reference to the thing that produced it.
/// That is deliberate: a signal that carried arbitrary content would be a channel through which
/// whatever produced the content could influence whether the platform wakes up, and everything
/// upstream of here includes sources this platform does not control.
/// </para>
/// <para>
/// <see cref="ObservedAtUtc"/> is part of the identity of an observation, and it is what makes the
/// same observation arriving twice deduplicable: two deliveries of the same reading produce the same
/// firing key and therefore one cycle.
/// </para>
/// </remarks>
public sealed record TriggerSignal
{
    private TriggerSignal(TriggerType type, WatchTarget target, decimal? value, DateTime observedAtUtc)
    {
        Type = type;
        Target = target;
        Value = value;
        ObservedAtUtc = observedAtUtc;
    }

    public TriggerType Type { get; }

    public WatchTarget Target { get; }

    /// <summary>The observed number, when the trigger type has one.</summary>
    public decimal? Value { get; }

    public DateTime ObservedAtUtc { get; }

    public static TriggerSignal Create(
        TriggerType type,
        WatchTarget target,
        DateTime observedAtUtc,
        decimal? value = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        DateRange.EnsureUtc(observedAtUtc, nameof(observedAtUtc));

        if (!Enum.IsDefined(type) || type == TriggerType.Unknown)
        {
            throw new DomainValidationException(
                nameof(type),
                $"'{type}' is not a trigger type an observation can carry.");
        }

        return new TriggerSignal(type, target, value, observedAtUtc);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Type} {Target} {Value} @{ObservedAtUtc:O}");
}
