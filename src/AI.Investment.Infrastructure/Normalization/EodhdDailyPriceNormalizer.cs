using System.Globalization;
using System.Text;
using System.Text.Json;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Ingestion.Providers;
using Microsoft.Extensions.Options;

namespace AI.Investment.Infrastructure.Normalization;

/// <summary>
/// Reads an archived EODHD end-of-day document into <c>security.close</c> observations.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The hard part is not the JSON; it is the two instants EODHD does not send.</strong> A
/// row is <c>{"date":"2026-08-27","open":…,"close":…}</c> - a bare trading date, with no session
/// close, no timezone and no statement of when the row became public. The platform's provenance
/// needs both, and one of them, <c>PublishedAtUtc</c>, is what every point-in-time judgement is
/// made from. So they come from the exchange session the operator stated in configuration, and the
/// assumption is written onto every observation as a caveat rather than left for a reader to infer.
/// An exchange nobody stated quarantines the payload; guessing a market's trading hours here would
/// put a fabricated instant in the one field a backtest must not be wrong about.
/// </para>
/// <para>
/// <strong>The raw close, never the adjusted one.</strong> <c>adjusted_close</c> is retroactively
/// rewritten by every later split and dividend, so the same row means different things on different
/// days - which is precisely what a bitemporal ledger exists to prevent. The number recorded here is
/// the price that printed.
/// </para>
/// <para>
/// <strong>Close only, deliberately.</strong> Open, high, low and volume are in the archived payload
/// and stay there. The existing discovery and validation paths read <c>security.close</c> and
/// nothing else; normalising four more attributes now would add four more things to be wrong about
/// for no current reader, and a later block can read them out of the archive without re-fetching a
/// byte.
/// </para>
/// <para>
/// <strong>A bad row quarantines the payload rather than being skipped</strong>, for the same
/// reason the operator-export normaliser does it: a row that cannot be read is a hole in a time
/// series, and a series with an invisible hole produces confident, wrong returns.
/// </para>
/// </remarks>
public sealed class EodhdDailyPriceNormalizer : INormalizer
{
    /// <summary>The canonical attribute a closing price is recorded under.</summary>
    /// <remarks>
    /// The same string the operator-export normaliser writes and Phase 7 reads. Stated as a
    /// reference rather than a second literal so the two producers cannot drift apart.
    /// </remarks>
    public const string CloseAttribute = DailyClosePriceNormalizer.CloseAttribute;

    /// <summary>The document is not a JSON array of daily rows.</summary>
    public const string UnexpectedShapeRule = "eodhd.unexpected-shape@1";

    /// <summary>No session was stated for the symbol's exchange.</summary>
    public const string UnstatedSessionRule = "market-data.unstated-session@1";

    /// <summary>The symbol on the request is not one this connector fetches.</summary>
    public const string UnreadableSymbolRule = "eodhd.unreadable-symbol@1";

    private const string DateField = "date";
    private const string CloseField = "close";

    private readonly EodhdOptions _options;

    public EodhdDailyPriceNormalizer(IOptions<EodhdOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
    }

    public bool CanNormalize(SourceId sourceId, DataCategory category)
    {
        ArgumentNullException.ThrowIfNull(sourceId);

        // Tied to the one source that produces this wire format. Claiming every MarketPrices
        // payload would mean reading the operator's CSV export as if it were EODHD's JSON.
        return sourceId == EodhdProvider.Id && category == DataCategory.MarketPrices;
    }

    public Task<NormalizationResult> NormalizeAsync(
        NormalizationInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var symbol = input.Subject.Identifier;

        if (string.IsNullOrWhiteSpace(symbol) || !symbol.Contains('.', StringComparison.Ordinal))
        {
            return Task.FromResult(NormalizationResult.Quarantine(
                UnreadableSymbolRule,
                "The request's subject is not an EODHD symbol, so the exchange whose session " +
                "these rows belong to cannot be determined."));
        }

        var session = _options.Session(EodhdProvider.ExchangeOf(symbol));

        if (session is null)
        {
            return Task.FromResult(NormalizationResult.Quarantine(
                UnstatedSessionRule,
                $"No session is configured for the exchange in '{symbol}'. EODHD sends a trading " +
                "date and no times, so without a stated session close and publication delay the " +
                "only way to produce the two instants provenance requires would be to invent them."));
        }

        string text;

        try
        {
            text = Decode(input.Payload.Span);
        }
        catch (DecoderFallbackException)
        {
            return Task.FromResult(NormalizationResult.Quarantine(
                DailyClosePriceNormalizer.UnreadablePayloadRule,
                "The payload is not UTF-8 text. A price document the platform cannot decode is " +
                "not one it may guess at."));
        }

        return Task.FromResult(Read(text, input, session, symbol));
    }

    private static NormalizationResult Read(
        string text,
        NormalizationInput input,
        ExchangeSessionOptions session,
        string symbol)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            // Includes the vendor's own error documents, which are not JSON arrays and are often
            // not JSON at all. Nothing from the body is copied into the reason: an authentication
            // failure page can carry the token that failed.
            return NormalizationResult.Quarantine(
                UnexpectedShapeRule,
                "The payload is not valid JSON. EODHD answers an error with a document that is " +
                "not a price series, and reading one as if it were would record prices nobody sent.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return NormalizationResult.Quarantine(
                    UnexpectedShapeRule,
                    "The payload is JSON but not an array of daily rows. The end-of-day endpoint " +
                    "returns an array; anything else is an error document or a changed contract.");
            }

            return ReadRows(document.RootElement, input, session, symbol);
        }
    }

    private static NormalizationResult ReadRows(
        JsonElement rows,
        NormalizationInput input,
        ExchangeSessionOptions session,
        string symbol)
    {
        var observations = new List<Observation>(rows.GetArrayLength());
        var caveats = Caveats(session, symbol);
        var index = 0;

        foreach (var row in rows.EnumerateArray())
        {
            index++;

            if (row.ValueKind != JsonValueKind.Object)
            {
                return NormalizationResult.Quarantine(
                    DailyClosePriceNormalizer.UnreadableRowRule,
                    $"Row {index}: expected an object with a '{DateField}' and a '{CloseField}'.");
            }

            if (!TryReadDate(row, out var tradingDate))
            {
                return NormalizationResult.Quarantine(
                    DailyClosePriceNormalizer.UnreadableRowRule,
                    $"Row {index}: the '{DateField}' field is missing or is not an ISO calendar date.");
            }

            if (!TryReadClose(row, out var close))
            {
                return NormalizationResult.Quarantine(
                    DailyClosePriceNormalizer.UnreadableRowRule,
                    $"Row {index}: the '{CloseField}' field is missing, is not a number, or is not " +
                    "positive. A zero or negative closing price is a broken feed, not a market event.");
            }

            var sessionCloseUtc = tradingDate + session.SessionCloseUtc;
            var publishedAtUtc = sessionCloseUtc + session.PublicationDelay;

            if (publishedAtUtc > input.RetrievedAtUtc)
            {
                // The stated delay has not elapsed for this row yet. Recording it would claim the
                // platform read a price before this installation says it became public - and the
                // ordering rules one layer down would refuse it anyway.
                return NormalizationResult.Quarantine(
                    DailyClosePriceNormalizer.ImpossibleOrderingRule,
                    $"Row {index}: the stated publication delay for this exchange puts this close " +
                    "in the future relative to when the document was fetched. Either the session " +
                    "is configured wrongly or the feed is ahead of its own publication schedule.");
            }

            try
            {
                observations.Add(Observation.RecordFact(
                    input.Subject,
                    CloseAttribute,
                    ObservationValue.Number(close),
                    Provenance.Create(
                        input.SourceId,
                        sessionCloseUtc,
                        publishedAtUtc,
                        input.RetrievedAtUtc,
                        sourceRecordId: symbol),
                    caveats));
            }
            catch (DomainValidationException exception)
            {
                return NormalizationResult.Quarantine(
                    DailyClosePriceNormalizer.UnreadableRowRule,
                    $"Row {index}: the domain refused the observation ({exception.GetType().Name}).");
            }
            catch (DomainRuleViolationException exception)
            {
                return NormalizationResult.Quarantine(
                    DailyClosePriceNormalizer.ImpossibleOrderingRule,
                    $"Row {index}: the domain refused the observation ({exception.GetType().Name}).");
            }
        }

        if (observations.Count == 0)
        {
            return NormalizationResult.Quarantine(
                DailyClosePriceNormalizer.EmptySeriesRule,
                "The document is an empty array. An instrument with no history and a request that " +
                "asked for a range the vendor does not cover are different problems, and recording " +
                "no observations would make them look the same.");
        }

        return NormalizationResult.Normalized(observations);
    }

    /// <summary>
    /// The assumption, written where a reader of the ledger will meet it.
    /// </summary>
    /// <remarks>
    /// Stated per observation rather than once in a document, because the observation is what
    /// survives into a measurement three months from now, and the question it has to answer -
    /// "where did this timestamp come from?" - is asked of the observation.
    /// </remarks>
    private static string[] Caveats(ExchangeSessionOptions session, string symbol) =>
    [
        string.Create(
            CultureInfo.InvariantCulture,
            $"EODHD supplied the trading date and the closing price for '{symbol}' and no times. " +
            $"The session close ({session.SessionCloseUtc:hh\\:mm} UTC) and the publication delay " +
            $"({session.PublicationDelay:g}) are this installation's stated facts about exchange " +
            $"'{session.Code}', not the vendor's. The price is the raw close, not the " +
            $"split- or dividend-adjusted one."),
    ];

    private static bool TryReadDate(JsonElement row, out DateTime value)
    {
        value = default;

        if (!row.TryGetProperty(DateField, out var field) || field.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = field.GetString();

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!DateTime.TryParseExact(
                text.Trim(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return false;
        }

        // Midnight UTC on the trading day. The session offset is added by the caller; this value
        // is never used on its own.
        value = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);

        return true;
    }

    private static bool TryReadClose(JsonElement row, out decimal value)
    {
        value = 0m;

        if (!row.TryGetProperty(CloseField, out var field))
        {
            return false;
        }

        switch (field.ValueKind)
        {
            case JsonValueKind.Number when field.TryGetDecimal(out var number):
                value = number;

                break;

            // Some vendor documents quote the numbers. Reading a quoted number is not a guess;
            // reading a non-numeric string as zero would be.
            case JsonValueKind.String when decimal.TryParse(
                field.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var quoted):
                value = quoted;

                break;

            default:
                return false;
        }

        return value > 0m;
    }

    /// <summary>Decodes strictly: an undecodable byte is an error rather than a question mark.</summary>
    private static string Decode(ReadOnlySpan<byte> payload)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var text = encoding.GetString(payload);

        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }
}
