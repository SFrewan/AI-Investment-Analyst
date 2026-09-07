using System.Globalization;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The arithmetic and the admission rules of the price-acquisition stage, with nothing attached.
/// </summary>
/// <remarks>
/// <para>
/// Pure and separate from any stage that would execute it, so the decisions can be checked before
/// a single request is authorised. Every number here is derived from the repository - the sealed
/// manifest's window, the connectors' actual endpoints, the identity artefact's own counts - and
/// none of it reaches a network.
/// </para>
/// <para>
/// <strong>The rule this class exists to enforce.</strong> A member that cannot be identified is
/// excluded from <em>acquisition</em> and never from the <em>universe</em>. Those are different
/// sets, and the moment they are allowed to become the same set the survivorship control the
/// sealed universe was built for is gone.
/// </para>
/// </remarks>
internal static class AcquisitionPlanning
{
    /// <summary>The sealed window, taken from the manifest rather than restated.</summary>
    public const string WindowFrom = "2021-09-01";

    /// <summary>The sealed window's end.</summary>
    public const string WindowTo = "2026-08-31";

    /// <summary>EODHD's virtual exchange for every US listing, including OTC.</summary>
    /// <remarks>
    /// The only exchange with a stated session in configuration. A symbol built for any other
    /// suffix would normalise into <c>market-data.unstated-session@1</c> and quarantine, so the
    /// suffix is not a formatting detail: it decides whether the payload becomes observations.
    /// </remarks>
    public const string UsSuffix = ".US";

    /// <summary>
    /// Requests per member: one price series, one splits series. There is no batch endpoint.
    /// </summary>
    /// <remarks>
    /// Both connectors take a single symbol in the path - <c>api/eod/{symbol}</c> and
    /// <c>api/splits/{symbol}</c> - so the cost is linear in members and there is nothing to
    /// negotiate. Stated as a constant because the budget is the thing most worth being unable to
    /// get wrong by accident.
    /// </remarks>
    public const int RequestsPerMember = 2;

    /// <summary>Gate 2's pre-registered floor. Stated here to be compared against, never moved.</summary>
    public const decimal SurvivorshipFloor = 0.05m;

    /// <summary>The symbol to request, from an SEC-authoritative bare ticker.</summary>
    /// <remarks>
    /// EDGAR writes <c>NTRS</c>; EODHD wants <c>NTRS.US</c>. A ticker that already carries a
    /// suffix keeps the one it has rather than gaining a second, because <c>X.US.US</c> is not a
    /// symbol and would fail as a not-found rather than as the configuration error it is.
    /// </remarks>
    public static string SymbolFor(string ticker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticker);

        var trimmed = ticker.Trim().ToUpperInvariant();

        return trimmed.Contains('.', StringComparison.Ordinal) ? trimmed : trimmed + UsSuffix;
    }

    /// <summary>
    /// Whether a member may be sent to a price provider at all.
    /// </summary>
    /// <remarks>
    /// Deliberately a re-statement of the readiness verdict rather than a second opinion about it.
    /// A planning stage that re-derived readiness could disagree with the identity stage, and the
    /// disagreement would be resolved in favour of whichever ran last.
    /// </remarks>
    public static bool MayAcquire(string status) => IdentityResolution.IsAuthoritative(status);

    /// <summary>Total billable requests for a given number of acquirable members.</summary>
    public static int BillableRequests(int members)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(members);

        return members * RequestsPerMember;
    }

    /// <summary>
    /// The share of an acquisition set that stopped appearing before the window closed.
    /// </summary>
    /// <remarks>
    /// The measure Gate 2 is made of, applied to the set that would actually be priced rather
    /// than to the manifest. A universe can be survivorship-free and still yield a survivorship-
    /// biased panel, if the members that died are exactly the ones whose symbols could not be
    /// established - which is not a hypothetical here.
    /// </remarks>
    public static decimal DropoutShare(int dropouts, int members)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dropouts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(members);

        return (decimal)dropouts / members;
    }

    /// <summary>Whether a set would satisfy the survivorship floor without it being moved.</summary>
    public static bool ClearsSurvivorshipFloor(int dropouts, int members) =>
        DropoutShare(dropouts, members) >= SurvivorshipFloor;

    /// <summary>Sessions a US equity trades in a period, for sizing rather than for judging.</summary>
    /// <remarks>
    /// About 252 a year. Used to state what a complete series would look like, never to decide
    /// that a shorter one is faulty: a company acquired in 2023 has a short series because it was
    /// acquired, and a coverage rule that could not tell those apart would delete the evidence
    /// this universe was built to hold.
    /// </remarks>
    public static int ExpectedSessions(DateOnly from, DateOnly to)
    {
        var days = to.DayNumber - from.DayNumber + 1;

        return days <= 0 ? 0 : (int)Math.Round(days * 252.0 / 365.25, MidpointRounding.AwayFromZero);
    }

    /// <summary>The sealed window as dates, parsed once from the constants above.</summary>
    public static (DateOnly From, DateOnly To) Window() =>
        (DateOnly.ParseExact(WindowFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateOnly.ParseExact(WindowTo, "yyyy-MM-dd", CultureInfo.InvariantCulture));
}
