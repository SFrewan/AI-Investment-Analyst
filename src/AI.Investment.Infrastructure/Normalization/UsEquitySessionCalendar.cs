using AI.Investment.Infrastructure.Configuration;

namespace AI.Investment.Infrastructure.Normalization;

/// <summary>
/// Resolves a US exchange's session close for a given trading date, across daylight saving.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this fixes.</strong> <see cref="ExchangeSessionOptions.SessionCloseUtc"/> is a
/// single value, and its own documentation says a market whose close moves with daylight saving
/// needs two entries or a calendar. For a rolling year an operator can edit the value twice a
/// year. Over a five-year history they cannot: <strong>450 of the 1,304 weekday sessions between
/// 2021-09-01 and 2026-08-31 are on standard time</strong>, so one static value stamps a third of
/// the history an hour early.
/// </para>
/// <para>
/// <strong>Why an hour is not cosmetic.</strong> <c>PublishedAtUtc</c> is the field every
/// point-in-time judgement reads, and it is the close plus the publication delay. A standard-time
/// session correctly becomes public at 01:00Z the following day; stamped from the daylight value
/// it becomes public at 00:00Z. Both fall on the same calendar day, so the error survives any
/// check that compares dates - and a decision taken in that hour reads a close that had not been
/// published. Erring early is the one direction the provenance rules exist to forbid.
/// </para>
/// <para>
/// <strong>The configured value is honoured, not replaced.</strong> This does not decide what time
/// the market closes; the operator still states that. It reads the configured value as the
/// daylight-time close - which is what the configuration comment already says it is - and adds the
/// hour back on standard-time dates. An installation that changes the stated close moves both
/// answers together, and every non-US exchange is left exactly as it was.
/// </para>
/// <para>
/// <strong>Exact rather than approximate.</strong> US daylight saving runs from the second Sunday
/// of March to the first Sunday of November, both switching at 02:00 local. Every transition is
/// therefore a Sunday, and no US equity session trades on a Sunday. The two cases that make naive
/// local-time arithmetic wrong - the hour that does not exist and the hour that happens twice -
/// cannot touch a trading date, so this is a total function with no fallback branch.
/// </para>
/// <para>
/// <strong>Deliberately not a trading calendar.</strong> This says what time a session closed if
/// one was held. It does not know whether one was held, and it does not know about the half-days
/// that close at 13:00 New York. Those are a holiday calendar, they are a separate decision, and
/// inventing them here is the thing the connector exists not to do.
/// </para>
/// </remarks>
public static class UsEquitySessionCalendar
{
    /// <summary>The EODHD exchange code this rule applies to.</summary>
    public const string UnitedStates = "US";

    private static readonly TimeSpan StandardTimeOffset = TimeSpan.FromHours(1);

    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    /// <summary>
    /// The session close to use for one trading date on one exchange.
    /// </summary>
    /// <remarks>
    /// Non-US exchanges return the configured value unchanged, so this cannot alter any market the
    /// rule was not written for.
    /// </remarks>
    public static TimeSpan CloseOn(ExchangeSessionOptions session, DateTime tradingDateUtc)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!AppliesTo(session.Code) || IsDaylightTime(DateOnly.FromDateTime(tradingDateUtc)))
        {
            return session.SessionCloseUtc;
        }

        var standard = session.SessionCloseUtc + StandardTimeOffset;

        // A configured close within an hour of midnight would roll past the day boundary and put
        // the close on the wrong date. That is a misconfiguration rather than a market, so the
        // stated value is returned untouched and the caveat still records what was used.
        return standard >= Day ? session.SessionCloseUtc : standard;
    }

    /// <summary>Whether this rule governs the given exchange code.</summary>
    public static bool AppliesTo(string? exchangeCode) =>
        string.Equals(exchangeCode, UnitedStates, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a date falls inside US daylight saving.
    /// </summary>
    /// <remarks>
    /// Second Sunday of March through the first Sunday of November. The half-open interval is
    /// exact for this use because a Sunday is never a trading date.
    /// </remarks>
    public static bool IsDaylightTime(DateOnly date) =>
        date >= NthSunday(date.Year, 3, 2) && date < NthSunday(date.Year, 11, 1);

    /// <summary>The <paramref name="n"/>th Sunday of a month, 1-based.</summary>
    public static DateOnly NthSunday(int year, int month, int n)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);

        var first = new DateOnly(year, month, 1);
        var offset = ((int)DayOfWeek.Sunday - (int)first.DayOfWeek + 7) % 7;

        return first.AddDays(offset + (7 * (n - 1)));
    }
}
