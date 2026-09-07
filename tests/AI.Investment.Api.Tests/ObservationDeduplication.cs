using System.Globalization;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Infrastructure.Normalization;

namespace AI.Investment.Api.Tests;

/// <summary>
/// What makes two stored observations the same observation, in one place for both namespaces.
/// </summary>
/// <remarks>
/// <para>
/// The store's own identity is subject, attribute, period end, publication instant and canonical
/// value. Gate 12 has always enforced it - but only over the financial-statement namespace, which
/// is how <strong>5,249 duplicate closing-price rows sat in the store while the gate read PASS</strong>.
/// The rule was never wrong; it was pointed at one half of the data.
/// </para>
/// <para>
/// Stated here as a pure function so the same key is used by the diagnosis, by the repair and by
/// the gate. Three implementations of one identity is how the halves drift apart, and a drift in
/// this particular rule is invisible: it does not throw, it just quietly stops counting things.
/// </para>
/// </remarks>
internal static class ObservationDeduplication
{
    /// <summary>The financial-statement namespace Gate 12 has always covered.</summary>
    /// <remarks>
    /// Referenced, never restated. Written out by hand this was <c>financial.</c> against a real
    /// prefix of <c>financials.</c>, so the query matched nothing and the gate reported a clean
    /// PASS over an empty set - which is the same shape of failure as the one this class was
    /// written to fix, arrived at by a different route. A de-duplication rule that matches nothing
    /// always passes, and that is the one bug it must not have.
    /// </remarks>
    public const string FinancialPrefix = FinancialFigures.Prefix;

    /// <summary>The price namespace it did not.</summary>
    public const string PriceAttribute = EodhdDailyPriceNormalizer.CloseAttribute;

    /// <summary>The corporate-action namespace, on the same footing.</summary>
    public const string SplitAttribute = EodhdSplitsNormalizer.SplitAttribute;

    /// <summary>
    /// The identity of one observation: everything the store uses to tell two rows apart.
    /// </summary>
    /// <remarks>
    /// Culture-invariant round-trip format for both instants. A key that formatted differently on
    /// a different machine would make the gate pass or fail by locale, which is worse than not
    /// having it.
    /// </remarks>
    public static string Key(
        string subjectKind,
        string? subjectIdentifier,
        string attribute,
        DateTime asOfUtc,
        DateTime publishedAtUtc,
        string canonicalValue) =>
        string.Join(
            '|',
            subjectKind,
            subjectIdentifier ?? string.Empty,
            attribute,
            asOfUtc.ToString("O", CultureInfo.InvariantCulture),
            publishedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            canonicalValue);

    /// <summary>Whether this attribute is one Gate 12 must judge.</summary>
    /// <remarks>
    /// Deliberately an allow-list of namespaces rather than "everything", because a namespace the
    /// gate has never seen may legitimately hold repeated rows and silently failing a gate on it
    /// would teach an operator to ignore the gate.
    /// </remarks>
    public static bool IsGoverned(string attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        return attribute.StartsWith(FinancialPrefix, StringComparison.Ordinal)
            || string.Equals(attribute, PriceAttribute, StringComparison.Ordinal)
            || string.Equals(attribute, SplitAttribute, StringComparison.Ordinal);
    }

    /// <summary>Which namespace an attribute belongs to, for reporting.</summary>
    public static string Namespace(string attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        if (attribute.StartsWith(FinancialPrefix, StringComparison.Ordinal))
        {
            return "financial";
        }

        return string.Equals(attribute, PriceAttribute, StringComparison.Ordinal)
            || string.Equals(attribute, SplitAttribute, StringComparison.Ordinal)
                ? "market-data"
                : "other";
    }

    /// <summary>
    /// How many rows exceed the number of distinct identities. Zero is the only passing answer.
    /// </summary>
    public static int Excess(int rows, int distinctIdentities)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        ArgumentOutOfRangeException.ThrowIfNegative(distinctIdentities);

        return Math.Max(0, rows - distinctIdentities);
    }

    /// <summary>Zero tolerance, in both namespaces, stated once.</summary>
    public static bool Passes(int rows, int distinctIdentities) =>
        Excess(rows, distinctIdentities) == 0;
}
