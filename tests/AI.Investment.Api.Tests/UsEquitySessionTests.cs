using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Normalization;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The production session-close resolver, checked over every date of the sealed window.
/// </summary>
/// <remarks>
/// Pure arithmetic against the promoted <see cref="UsEquitySessionCalendar"/>: no database, no
/// container, no provider. The window is five years, so the exhaustive tests walk about 1,826
/// dates and still run in milliseconds - which is the argument for checking every one of them
/// rather than a handful of samples.
/// </remarks>
public sealed class UsEquitySessionTests
{
    private static readonly DateOnly WindowFrom = new(2021, 9, 1);
    private static readonly DateOnly WindowTo = new(2026, 8, 31);

    /// <summary>What configuration states, and what the whole window used to be stamped with.</summary>
    private static readonly TimeSpan ConfiguredClose = TimeSpan.FromHours(20);

    private static readonly TimeSpan PublicationDelay = TimeSpan.FromHours(4);

    private static readonly ExchangeSessionOptions UnitedStates = new()
    {
        Code = "US",
        SessionCloseUtc = ConfiguredClose,
        PublicationDelay = PublicationDelay,
    };

    private static readonly ExchangeSessionOptions London = new()
    {
        Code = "LSE",
        SessionCloseUtc = TimeSpan.FromHours(16),
        PublicationDelay = PublicationDelay,
    };

    [Fact]
    public void The_transitions_are_the_second_Sunday_of_March_and_the_first_of_November()
    {
        Assert.Equal(new DateOnly(2021, 3, 14), UsEquitySessionCalendar.NthSunday(2021, 3, 2));
        Assert.Equal(new DateOnly(2021, 11, 7), UsEquitySessionCalendar.NthSunday(2021, 11, 1));
        Assert.Equal(new DateOnly(2024, 3, 10), UsEquitySessionCalendar.NthSunday(2024, 3, 2));
        Assert.Equal(new DateOnly(2026, 11, 1), UsEquitySessionCalendar.NthSunday(2026, 11, 1));

        // A month that begins on a Sunday is the case an off-by-one gets wrong.
        Assert.Equal(new DateOnly(2026, 3, 1), UsEquitySessionCalendar.NthSunday(2026, 3, 1));
        Assert.Equal(new DateOnly(2026, 3, 8), UsEquitySessionCalendar.NthSunday(2026, 3, 2));
    }

    [Fact]
    public void A_session_closes_at_the_configured_hour_in_summer_and_an_hour_later_in_winter()
    {
        Assert.Equal(ConfiguredClose, CloseOn(2023, 6, 15));
        Assert.Equal(ConfiguredClose + TimeSpan.FromHours(1), CloseOn(2023, 1, 17));

        // The trading days either side of a spring transition, and of an autumn one.
        Assert.Equal(ConfiguredClose + TimeSpan.FromHours(1), CloseOn(2024, 3, 8));
        Assert.Equal(ConfiguredClose, CloseOn(2024, 3, 11));
        Assert.Equal(ConfiguredClose, CloseOn(2024, 11, 1));
        Assert.Equal(ConfiguredClose + TimeSpan.FromHours(1), CloseOn(2024, 11, 4));
    }

    /// <summary>
    /// The configured value is honoured rather than replaced, and no other market is touched.
    /// </summary>
    [Fact]
    public void The_rule_moves_with_configuration_and_leaves_every_other_exchange_alone()
    {
        var later = new ExchangeSessionOptions
        {
            Code = "US",
            SessionCloseUtc = TimeSpan.FromHours(20) + TimeSpan.FromMinutes(30),
            PublicationDelay = PublicationDelay,
        };

        var winter = new DateTime(2023, 1, 17, 0, 0, 0, DateTimeKind.Utc);

        // Both answers move together with the stated close.
        Assert.Equal(
            TimeSpan.FromHours(21) + TimeSpan.FromMinutes(30),
            UsEquitySessionCalendar.CloseOn(later, winter));

        // London keeps its configured close on the same date: this rule is not about it.
        Assert.Equal(
            London.SessionCloseUtc,
            UsEquitySessionCalendar.CloseOn(London, winter));
        Assert.False(UsEquitySessionCalendar.AppliesTo(London.Code));
        Assert.True(UsEquitySessionCalendar.AppliesTo("us"));

        // A close within an hour of midnight would roll onto the wrong date, so it is left alone.
        var nearMidnight = new ExchangeSessionOptions
        {
            Code = "US",
            SessionCloseUtc = TimeSpan.FromHours(23) + TimeSpan.FromMinutes(30),
            PublicationDelay = PublicationDelay,
        };

        Assert.Equal(
            nearMidnight.SessionCloseUtc,
            UsEquitySessionCalendar.CloseOn(nearMidnight, winter));
    }

    /// <summary>
    /// The property that makes this exactly solvable instead of approximately.
    /// </summary>
    /// <remarks>
    /// Naive local-time arithmetic is dangerous because one local hour a year does not exist and
    /// another happens twice. Neither can touch an equity session: both transitions are Sundays,
    /// and no US equity session trades on a Sunday. Walked over the window rather than argued,
    /// because the argument is only as good as the calendar it assumed.
    /// </remarks>
    [Fact]
    public void No_trading_date_in_the_window_can_land_on_a_transition()
    {
        for (var year = WindowFrom.Year; year <= WindowTo.Year; year++)
        {
            foreach (var transition in new[]
            {
                UsEquitySessionCalendar.NthSunday(year, 3, 2),
                UsEquitySessionCalendar.NthSunday(year, 11, 1),
            })
            {
                Assert.Equal(DayOfWeek.Sunday, transition.DayOfWeek);
            }
        }
    }

    [Fact]
    public void Every_date_in_the_sealed_window_resolves_to_one_of_two_closes()
    {
        var standard = ConfiguredClose + TimeSpan.FromHours(1);

        for (var date = WindowFrom; date <= WindowTo; date = date.AddDays(1))
        {
            var close = UsEquitySessionCalendar.CloseOn(
                UnitedStates,
                date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

            Assert.True(
                close == ConfiguredClose || close == standard,
                $"{date:O} resolved to {close}, which is neither close.");

            // The close never rolls onto another day, so the trading date is never shifted.
            Assert.True(close < TimeSpan.FromDays(1));
        }
    }

    /// <summary>
    /// The size of the defect, counted rather than described.
    /// </summary>
    /// <remarks>
    /// This is the number that decided whether the current configuration could carry a five-year
    /// backfill. It is pinned so a regression has to move it, and so nobody has to re-derive it
    /// from a paragraph.
    /// </remarks>
    [Fact]
    public void A_single_static_close_would_be_an_hour_early_on_a_third_of_the_windows_sessions()
    {
        var weekdays = 0;
        var corrected = 0;

        for (var date = WindowFrom; date <= WindowTo; date = date.AddDays(1))
        {
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            weekdays++;

            var difference = UsEquitySessionCalendar.CloseOn(
                UnitedStates,
                date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)) - ConfiguredClose;

            // Never earlier than the stated value, and never later by more than an hour.
            Assert.InRange(difference, TimeSpan.Zero, TimeSpan.FromHours(1));

            if (difference > TimeSpan.Zero)
            {
                corrected++;
            }
        }

        Assert.Equal(1304, weekdays);
        Assert.Equal(450, corrected);
        Assert.InRange((double)corrected / weekdays, 0.34d, 0.35d);
    }

    /// <summary>
    /// The look-ahead the hour bought, stated as an instant rather than as a worry.
    /// </summary>
    [Fact]
    public void The_old_static_close_published_a_standard_time_session_before_it_existed()
    {
        var winter = new DateTime(2023, 1, 17, 0, 0, 0, DateTimeKind.Utc);

        var correct = winter + UsEquitySessionCalendar.CloseOn(UnitedStates, winter) + PublicationDelay;
        var old = winter + ConfiguredClose + PublicationDelay;

        Assert.Equal(new DateTime(2023, 1, 18, 1, 0, 0, DateTimeKind.Utc), correct);
        Assert.Equal(new DateTime(2023, 1, 18, 0, 0, 0, DateTimeKind.Utc), old);

        // Both land on the following day, so the error was invisible to any check comparing dates.
        Assert.Equal(DateOnly.FromDateTime(correct), DateOnly.FromDateTime(old));
        Assert.Equal(1d, (correct - old).TotalHours);

        // A summer session is unchanged, so the defect was never uniform and could not have been
        // corrected by shifting every stamp.
        var summer = new DateTime(2023, 6, 15, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(
            ConfiguredClose,
            UsEquitySessionCalendar.CloseOn(UnitedStates, summer));
    }

    private static TimeSpan CloseOn(int year, int month, int day) =>
        UsEquitySessionCalendar.CloseOn(
            UnitedStates,
            new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc));
}
