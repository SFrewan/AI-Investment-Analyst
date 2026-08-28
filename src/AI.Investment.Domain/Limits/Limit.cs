using System.Globalization;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Limits;

/// <summary>
/// One configured ceiling: what kind, optionally for which capability, and the value.
/// </summary>
/// <remarks>
/// <para>
/// A limit carries exactly one of a money amount, a count or a duration, and which one is
/// determined by the kind rather than chosen by the caller. Constructing a "max daily loss" from a
/// count is refused, because a limit that compares the wrong dimension does not fail loudly - it
/// silently never binds.
/// </para>
/// <para>
/// <see cref="Capability"/> is optional. A limit with none applies to everything; a limit with one
/// applies only to actions using that capability, which is what makes "twenty ingestion actions a
/// day" expressible without also capping analysis.
/// </para>
/// </remarks>
public sealed record Limit
{
    private Limit(
        LimitKind kind,
        Capability? capability,
        Money? amount,
        int? count,
        TimeSpan? duration,
        Percentage? ratio)
    {
        Kind = kind;
        Capability = capability;
        Amount = amount;
        Count = count;
        Duration = duration;
        Ratio = ratio;
    }

    public LimitKind Kind { get; }

    /// <summary>The capability this limit is scoped to, or null when it applies to everything.</summary>
    public Capability? Capability { get; }

    public Money? Amount { get; }

    public int? Count { get; }

    public TimeSpan? Duration { get; }

    public Percentage? Ratio { get; }

    /// <summary>A ceiling denominated in money: position size, exposure, loss, drawdown, cost.</summary>
    public static Limit OfMoney(LimitKind kind, Money amount, Capability? capability = null)
    {
        ArgumentNullException.ThrowIfNull(amount);
        EnsureKind(kind);

        if (kind is not (LimitKind.MaxPositionSize or LimitKind.MaxTotalExposure or
            LimitKind.MaxDailyLoss or LimitKind.MaxDrawdown or LimitKind.MaxCostPerCycle))
        {
            throw new DomainValidationException(
                nameof(kind),
                $"A {kind} limit is not denominated in money. A limit that compares the wrong " +
                "dimension never binds, and its absence is invisible.");
        }

        if (amount.IsNegative)
        {
            throw new DomainValidationException(nameof(amount), "A limit may not be negative.");
        }

        return new Limit(kind, capability, amount, null, null, null);
    }

    /// <summary>A ceiling denominated in a count of actions.</summary>
    public static Limit OfCount(LimitKind kind, int count, Capability? capability = null)
    {
        EnsureKind(kind);

        if (kind != LimitKind.MaxActionsPerCapabilityPerDay)
        {
            throw new DomainValidationException(
                nameof(kind),
                $"A {kind} limit is not denominated in a count.");
        }

        if (count < 0)
        {
            throw new DomainValidationException(nameof(count), "A limit may not be negative.");
        }

        return new Limit(kind, capability, null, count, null, null);
    }

    /// <summary>A cooldown: how long to stand down after a realised loss.</summary>
    public static Limit OfDuration(LimitKind kind, TimeSpan duration, Capability? capability = null)
    {
        EnsureKind(kind);

        if (kind != LimitKind.CooldownAfterLoss)
        {
            throw new DomainValidationException(
                nameof(kind),
                $"A {kind} limit is not denominated in time.");
        }

        if (duration < TimeSpan.Zero)
        {
            throw new DomainValidationException(nameof(duration), "A cooldown may not be negative.");
        }

        return new Limit(kind, capability, null, null, duration, null);
    }

    /// <summary>A concentration ceiling: the share of total exposure one instrument may hold.</summary>
    public static Limit OfRatio(LimitKind kind, Percentage ratio, Capability? capability = null)
    {
        ArgumentNullException.ThrowIfNull(ratio);
        EnsureKind(kind);

        if (kind != LimitKind.MaxConcentration)
        {
            throw new DomainValidationException(
                nameof(kind),
                $"A {kind} limit is not denominated as a ratio.");
        }

        if (ratio.Ratio is < 0m or > 1m)
        {
            throw new DomainValidationException(
                nameof(ratio),
                "A concentration limit must be between 0 and 1.");
        }

        return new Limit(kind, capability, null, null, null, ratio);
    }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Kind}{(Capability is null ? string.Empty : $"/{Capability}")}=" +
            $"{Amount?.ToString() ?? Count?.ToString(CultureInfo.InvariantCulture) ?? Duration?.ToString() ?? Ratio?.ToString()}");

    private static void EnsureKind(LimitKind kind)
    {
        if (kind == LimitKind.Unknown || !Enum.IsDefined(kind))
        {
            throw new DomainValidationException(
                nameof(kind),
                $"'{kind}' is not a configurable limit kind.");
        }
    }
}
