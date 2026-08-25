using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.ValueObjects;

public sealed class DateRangeTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_valid_utc_range_is_accepted() =>
        Assert.Equal(Start, DateRange.Create(Start, End).StartUtc);

    /// <summary>
    /// Not pedantry: comparing a local timestamp with a UTC one is wrong by an offset that
    /// changes twice a year, and in a system where "what was known on this date" decides whether
    /// a backtest means anything, that error is unrecoverable after the fact.
    /// </summary>
    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Non_utc_endpoints_are_rejected(DateTimeKind kind)
    {
        var start = DateTime.SpecifyKind(Start, kind);
        Assert.Throws<DomainValidationException>(() => DateRange.Create(start, End));
    }

    [Fact]
    public void An_end_before_its_start_is_rejected() =>
        Assert.Throws<DomainValidationException>(() => DateRange.Create(End, Start));

    [Fact]
    public void A_single_instant_range_is_valid() =>
        Assert.Equal(TimeSpan.Zero, DateRange.Create(Start, Start).Duration);

    [Fact]
    public void Contains_includes_both_endpoints()
    {
        var range = DateRange.Create(Start, End);

        Assert.True(range.Contains(Start));
        Assert.True(range.Contains(End));
        Assert.True(range.Contains(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.False(range.Contains(new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Overlaps_detects_touching_ranges()
    {
        var first = DateRange.Create(Start, End);
        var second = DateRange.Create(End, End.AddDays(10));

        Assert.True(first.Overlaps(second));
        Assert.False(first.Overlaps(DateRange.Create(End.AddDays(1), End.AddDays(10))));
    }
}
