using System.Text;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The correlation identifier one attempt at one batched request carries.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists as its own type.</strong> The batch runner used to build the correlation
/// from the ticker alone - <c>batch-NXST-MarketPrices</c> - which made it a pure function of the
/// symbol. The Action/Policy seam keys idempotency on <c>{fingerprint}:{correlationId}</c>, so a
/// symbol-only correlation meant every future attempt at that symbol was, to the seam, the same
/// act. When <c>NXST.US</c> failed on a DNS error the seam had already claimed its key, and the
/// re-attempt was refused as a duplicate: a transport failure became permanent, and the symbol
/// could never be acquired again.
/// </para>
/// <para>
/// <strong>What changed, and what deliberately did not.</strong> The correlation now carries the
/// attempt, so each separately approved attempt is a distinct act. The seam's duplicate rule is
/// untouched and is exactly as strict as before: two proposals with the same correlation still
/// collide, so a repeated or redelivered attempt within one approval is still suppressed and still
/// reaches the provider once. The <em>fingerprint</em> is not touched either - it is computed from
/// source, category, region, subject and window, and excludes the correlation by design - so ledger
/// suppression, restart safety and every existing <c>HasCompletedAsync</c> decision are unchanged.
/// </para>
/// <para>
/// A correlation identifier admits ASCII letters, digits, hyphen and underscore and nothing else,
/// which is why the ticker is reduced rather than interpolated: a malformed identifier would throw
/// while the request was being built, outside the runner's try, and end a batch before it could
/// record what it had already spent.
/// </para>
/// </remarks>
internal static class AcquisitionCorrelation
{
    /// <summary>What every batched correlation begins with.</summary>
    public const string Prefix = "batch";

    /// <summary>The stand-in for a ticker that reduces to nothing.</summary>
    public const string Unnamed = "unnamed";

    /// <summary>
    /// Builds the correlation for one attempt at one symbol in one batch.
    /// </summary>
    /// <param name="batchIndex">Which batch of the partition this is.</param>
    /// <param name="attempt">Which approved attempt at that batch this is, from one.</param>
    /// <param name="ticker">The member's ticker, before the exchange suffix.</param>
    /// <param name="category">The data category being requested.</param>
    public static CorrelationId For(int batchIndex, int attempt, string ticker, DataCategory category)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(batchIndex);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        return CorrelationId.Create(Text(batchIndex, attempt, ticker, category));
    }

    /// <summary>The identifier as text, so a test can read it without a domain type in the way.</summary>
    public static string Text(int batchIndex, int attempt, string ticker, DataCategory category) =>
        Universe.Inv($"{Prefix}-{batchIndex}-a{attempt}-{Safe(ticker)}-{category}");

    /// <summary>Reduces a ticker to the characters a correlation identifier admits.</summary>
    public static string Safe(string? ticker)
    {
        if (string.IsNullOrEmpty(ticker))
        {
            return Unnamed;
        }

        var safe = new StringBuilder(ticker.Length);

        foreach (var c in ticker)
        {
            safe.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        }

        return safe.ToString();
    }
}
