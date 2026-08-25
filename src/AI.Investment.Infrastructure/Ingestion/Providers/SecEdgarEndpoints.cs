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
