using System.Globalization;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Infrastructure.Ingestion.Providers;

/// <summary>
/// Builds EDGAR request paths. Pure, so the part of a connector most likely to be wrong is the
/// part that can be tested without a network.
/// </summary>
/// <remarks>
/// EDGAR identifies companies by CIK - a number, zero-padded to ten digits, prefixed <c>CIK</c> in
/// these paths. Tickers are not accepted, so a subject identifier is normalised here rather than
/// assumed to be well formed: "320193", "0000320193" and "CIK0000320193" all name Apple, and a
/// connector that accepted only one of them would fail on data entered by a human.
/// </remarks>
internal static class SecEdgarEndpoints
{
    public const int CikDigits = 10;

    /// <summary>Company filing history, including every submission EDGAR holds.</summary>
    public static string Submissions(string cik) => $"submissions/CIK{cik}.json";

    /// <summary>Every XBRL fact the company has reported.</summary>
    public static string CompanyFacts(string cik) => $"api/xbrl/companyfacts/CIK{cik}.json";

    /// <summary>The subject kind a cross-sectional frame is about: a period, not a company.</summary>
    public const string PeriodSubjectKind = "Period";

    /// <summary>The only taxonomy this connector reads, here as everywhere else.</summary>
    public const string Taxonomy = "us-gaap";

    /// <summary>The longest any one segment of a frame identifier may be.</summary>
    public const int MaxFrameSegmentLength = 64;

    /// <summary>
    /// One period's cross-section of a reported concept, across every filer that reported it.
    /// </summary>
    /// <remarks>
    /// EDGAR spells these <c>api/xbrl/frames/us-gaap/Assets/USD/CY2021Q3I.json</c>: taxonomy,
    /// concept, unit, period. The period suffix carries meaning - <c>CY2021Q3I</c> is an instant at
    /// the end of that quarter, <c>CY2021Q3</c> a duration over it - and is passed through as the
    /// operator wrote it rather than assembled here, because a connector that built period codes
    /// would be deciding which quarter a caller meant.
    /// </remarks>
    public static string Frames(FrameSubject frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        return $"api/xbrl/frames/{frame.Taxonomy}/{frame.Concept}/{frame.Unit}/{frame.Period}.json";
    }

    /// <summary>
    /// Parses a frame subject identifier, or returns null when it is not one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is a security boundary, not a convenience.</strong> Every segment lands
    /// directly in a URL path, so an identifier carrying a separator, a dot-segment or an escape
    /// would let a subject reach an endpoint nobody authorised - the same class of defect the
    /// EODHD connector refuses symbols for. Segments are therefore restricted to ASCII letters and
    /// digits and nothing else: no dots, no slashes, no hyphens, no percent signs. Every EDGAR
    /// taxonomy, concept, unit and period code is expressible within that.
    /// </para>
    /// <para>
    /// The taxonomy must be <see cref="Taxonomy"/>. A caller asking for a different one is asking
    /// for facts this connector's normalisers have never been written against.
    /// </para>
    /// </remarks>
    public static FrameSubject? ParseFrame(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var segments = identifier.Trim().Split('/');

        if (segments.Length != 4)
        {
            return null;
        }

        foreach (var segment in segments)
        {
            if (!IsSafeSegment(segment))
            {
                return null;
            }
        }

        return string.Equals(segments[0], Taxonomy, StringComparison.Ordinal)
            ? new FrameSubject(segments[0], segments[1], segments[2], segments[3])
            : null;
    }

    /// <summary>
    /// ASCII letters, digits and the hyphen. Nothing else, and the hyphen is not a concession.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first version of this rule allowed letters and digits only, and rejected every frame
    /// there is: the taxonomy it insists on is spelled <c>us-gaap</c>, and EDGAR writes compound
    /// units as <c>USD-per-shares</c>. A boundary that refuses the only values it accepts is not a
    /// strict boundary, it is a broken one - and it failed closed loudly here rather than at the
    /// first request, which is the whole reason the parser is tested apart from the network.
    /// </para>
    /// <para>
    /// A hyphen is safe in a path segment: it is not a separator, it cannot form a dot-segment, and
    /// it needs no escaping. The characters that would matter - <c>.</c>, <c>/</c>, <c>%</c>,
    /// <c>?</c>, <c>#</c> and whitespace - remain refused, so an identifier still cannot climb out
    /// of the path it was given or smuggle a query onto the end of it.
    /// </para>
    /// </remarks>
    private static bool IsSafeSegment(string segment)
    {
        if (segment.Length == 0 || segment.Length > MaxFrameSegmentLength)
        {
            return false;
        }

        var named = false;

        foreach (var c in segment)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                named = true;

                continue;
            }

            if (c != '-')
            {
                return false;
            }
        }

        // A segment of punctuation alone names nothing.
        return named;
    }

    /// <summary>
    /// The path serving <paramref name="category"/>, or null when EDGAR has nothing for it.
    /// </summary>
    /// <remarks>
    /// Returning null rather than throwing keeps this total: the capability check has already
    /// refused unsupported categories with a named rule, and a second exception path for the same
    /// condition would be a worse error message reached by a harder route.
    /// </remarks>
    public static string? ForCategory(DataCategory category, string cik) => category switch
    {
        DataCategory.RegulatoryFilings => Submissions(cik),
        DataCategory.CompanyProfile => Submissions(cik),
        DataCategory.EarningsDisclosure => Submissions(cik),
        DataCategory.FinancialStatements => CompanyFacts(cik),

        // Deliberately null, and deliberately listed rather than left to the wildcard: a
        // market-wide frame is about a period and has no CIK to put in a path. Falling through to
        // a company endpoint would answer a question about one filer and label it a cross-section.
        DataCategory.MarketWideDisclosure => null,

        _ => null,
    };

    /// <summary>
    /// Normalises a subject identifier to ten digits, or returns null when it cannot be one.
    /// </summary>
    public static string? NormaliseCik(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var trimmed = identifier.Trim();

        if (trimmed.StartsWith("CIK", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[3..];
        }

        trimmed = trimmed.TrimStart('-', ' ');

        if (trimmed.Length == 0 || trimmed.Length > CikDigits)
        {
            return null;
        }

        foreach (var c in trimmed)
        {
            if (!char.IsAsciiDigit(c))
            {
                return null;
            }
        }

        // Parsed and reformatted rather than padded as text, so "0000000000" is rejected as the
        // non-identifier it is rather than accepted as a company.
        var value = long.Parse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture);

        return value == 0 ? null : value.ToString("D10", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// A cross-sectional frame's coordinates: taxonomy, concept, unit and period.
/// </summary>
/// <remarks>
/// Parsed from a subject identifier of the form <c>us-gaap/Assets/USD/CY2021Q3I</c> and never
/// constructed from unvalidated text, so nothing reaches a URL path that
/// <see cref="SecEdgarEndpoints.ParseFrame"/> has not accepted.
/// </remarks>
internal sealed record FrameSubject(string Taxonomy, string Concept, string Unit, string Period)
{
    public override string ToString() => $"{Taxonomy}/{Concept}/{Unit}/{Period}";
}
