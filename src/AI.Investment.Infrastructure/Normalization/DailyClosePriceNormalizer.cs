using System.Globalization;
using System.Text;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Ingestion.Providers;

namespace AI.Investment.Infrastructure.Normalization;

/// <summary>
/// Reads an exported daily price history into <c>security.close</c> observations.
/// </summary>
/// <remarks>
/// <para>
/// The wire format is three columns and nothing else:
/// <c>session_close_utc,close,published_at_utc</c>. Two instants and a number, both instants UTC
/// and explicitly so. A date without a time would force this class to invent a session close, and a
/// time without an offset would force it to invent a market's timezone - and both inventions would
/// land in <c>AsOfUtc</c>, which is one of the two timestamps every point-in-time judgement in the
/// platform is made from.
/// </para>
/// <para>
/// <strong>The publication column is why this format exists.</strong> Phase 7 admits evidence on
/// <c>PublishedAtUtc</c> and on nothing else, so a price series that did not say when each close
/// became public would be unusable for measurement no matter how accurate its prices were. A vendor
/// export that carries no publication time is not a smaller version of this file; it is a different
/// file, and the operator has to say what the publication times were before the platform can
/// measure anything against it.
/// </para>
/// <para>
/// <strong>A bad row quarantines the payload rather than being skipped.</strong> This is the
/// opposite of the company-profile normaliser's choice, and deliberately: there, a field that could
/// not be read costs one attribute out of nine and the other eight are still true. Here, a row that
/// could not be read is a hole in a time series, and a series with an invisible hole produces
/// confident, wrong returns. A refusal an operator can see beats a gap nobody can.
/// </para>
/// <para>
/// Nothing here interpolates, forward-fills, adjusts or rounds. The observation carries the number
/// in the file.
/// </para>
/// </remarks>
public sealed class DailyClosePriceNormalizer : INormalizer
{
    /// <summary>The canonical attribute a closing price is recorded under.</summary>
    /// <remarks>
    /// The same string Phase 7 reads from configuration. It is stated here as a constant so the
    /// producing side and the measuring side can be shown to agree in a test rather than by
    /// inspection.
    /// </remarks>
    public const string CloseAttribute = "security.close";

    /// <summary>The header the file must begin with, exactly these columns in this order.</summary>
    public const string Header = "session_close_utc,close,published_at_utc";

    /// <summary>The payload is not text this build can read.</summary>
    public const string UnreadablePayloadRule = "market-data.unreadable-payload@1";

    /// <summary>The file does not begin with the declared columns.</summary>
    public const string UnexpectedColumnsRule = "market-data.unexpected-columns@1";

    /// <summary>The file has a header and no prices under it.</summary>
    public const string EmptySeriesRule = "market-data.empty-series@1";

    /// <summary>A row could not be read as an instant, a price and a publication time.</summary>
    public const string UnreadableRowRule = "market-data.unreadable-row@1";

    /// <summary>A row claims a price was published before its session ended, or after it was read.</summary>
    public const string ImpossibleOrderingRule = "market-data.impossible-ordering@1";

    public const string SourceCaveat =
        "Session close, price and publication time are all as stated by the operator-supplied price " +
        "history. The publication time is what every point-in-time judgement is made from, and it is " +
        "only as good as the export it came from.";

    private static readonly string[] ExpectedColumns = ["session_close_utc", "close", "published_at_utc"];

    private static readonly char[] RowSeparators = ['\n'];

    private static readonly string[] Caveats = [SourceCaveat];

    public bool CanNormalize(SourceId sourceId, DataCategory category)
    {
        ArgumentNullException.ThrowIfNull(sourceId);

        // Tied to the one source that produces this format, exactly as the EDGAR normaliser is.
        // A second market-data connector will speak its vendor's wire format, not this one, and
        // claiming every MarketPrices payload would mean reading that vendor's file as if it were
        // this one.
        return sourceId == PriceHistoryFileProvider.Id && category == DataCategory.MarketPrices;
    }

    public Task<NormalizationResult> NormalizeAsync(
        NormalizationInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        string text;

        try
        {
            text = Decode(input.Payload.Span);
        }
        catch (DecoderFallbackException)
        {
            return Task.FromResult(NormalizationResult.Quarantine(
                UnreadablePayloadRule,
                "The payload is not UTF-8 text. A price history the platform cannot decode is not a " +
                "price history it may guess at."));
        }

        return Task.FromResult(Read(text, input));
    }

    private static NormalizationResult Read(string text, NormalizationInput input)
    {
        var rows = text.Split(RowSeparators, StringSplitOptions.None);
        var index = 0;

        while (index < rows.Length && string.IsNullOrWhiteSpace(rows[index]))
        {
            index++;
        }

        if (index >= rows.Length || !IsHeader(rows[index]))
        {
            return NormalizationResult.Quarantine(
                UnexpectedColumnsRule,
                $"The file does not begin with '{Header}'. Reading a differently shaped export as " +
                "if it had these columns is how a publication time becomes a session date.");
        }

        var provenanceSource = input.SourceId;
        var observations = new List<Observation>();

        for (var line = index + 1; line < rows.Length; line++)
        {
            var row = rows[line].TrimEnd('\r').Trim();

            if (row.Length == 0)
            {
                continue;
            }

            var parsed = ParseRow(row, out var failure);

            if (parsed is null)
            {
                // The line number and the reason; never the row's text. A quarantine reason is
                // long-lived and unredactable, and licensed prices are exactly what should not be
                // copied into one.
                return NormalizationResult.Quarantine(
                    failure!.RuleId,
                    $"Line {line + 1}: {failure.Reason}");
            }

            if (parsed.PublishedAtUtc > input.RetrievedAtUtc)
            {
                return NormalizationResult.Quarantine(
                    ImpossibleOrderingRule,
                    $"Line {line + 1}: the close claims to have been published after the file was " +
                    "read. A fact cannot be retrieved before it was published, and the ordering " +
                    "rules would refuse it one layer down.");
            }

            try
            {
                observations.Add(Observation.RecordFact(
                    input.Subject,
                    CloseAttribute,
                    ObservationValue.Number(parsed.Close),
                    Provenance.Create(
                        provenanceSource,
                        parsed.SessionCloseUtc,
                        parsed.PublishedAtUtc,
                        input.RetrievedAtUtc,
                        sourceRecordId: input.Subject.Identifier),
                    Caveats));
            }
            catch (DomainValidationException exception)
            {
                return NormalizationResult.Quarantine(
                    UnreadableRowRule,
                    $"Line {line + 1}: the domain refused the observation ({exception.GetType().Name}).");
            }
            catch (DomainRuleViolationException exception)
            {
                return NormalizationResult.Quarantine(
                    ImpossibleOrderingRule,
                    $"Line {line + 1}: the domain refused the observation ({exception.GetType().Name}).");
            }
        }

        if (observations.Count == 0)
        {
            return NormalizationResult.Quarantine(
                EmptySeriesRule,
                "The file has the declared columns and no prices under them. An empty series and a " +
                "series that has not been exported yet are different problems, and recording no " +
                "observations would make them look the same.");
        }

        return NormalizationResult.Normalized(observations);
    }

    private static bool IsHeader(string row)
    {
        var columns = row.TrimEnd('\r').Split(',');

        if (columns.Length != ExpectedColumns.Length)
        {
            return false;
        }

        for (var i = 0; i < columns.Length; i++)
        {
            if (!string.Equals(columns[i].Trim(), ExpectedColumns[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static PriceRow? ParseRow(string row, out RowFailure? failure)
    {
        failure = null;

        var fields = row.Split(',');

        if (fields.Length != ExpectedColumns.Length)
        {
            failure = new RowFailure(
                UnreadableRowRule,
                $"expected {ExpectedColumns.Length} fields and found {fields.Length}.");

            return null;
        }

        if (!TryReadInstant(fields[0], out var sessionClose))
        {
            failure = new RowFailure(
                UnreadableRowRule,
                "the session close is not an ISO-8601 instant with an explicit UTC offset.");

            return null;
        }

        if (!decimal.TryParse(
                fields[1].Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var close))
        {
            failure = new RowFailure(UnreadableRowRule, "the close is not a number.");

            return null;
        }

        if (close <= 0m)
        {
            failure = new RowFailure(
                UnreadableRowRule,
                "the close is not positive. A zero or negative closing price is a broken export, " +
                "not a market event.");

            return null;
        }

        if (!TryReadInstant(fields[2], out var published))
        {
            failure = new RowFailure(
                UnreadableRowRule,
                "the publication time is not an ISO-8601 instant with an explicit UTC offset.");

            return null;
        }

        if (published < sessionClose)
        {
            failure = new RowFailure(
                ImpossibleOrderingRule,
                "the close claims to have been published before the session it describes had ended.");

            return null;
        }

        return new PriceRow(sessionClose, close, published);
    }

    /// <summary>
    /// An ISO-8601 instant that says, in the text itself, that it is UTC.
    /// </summary>
    /// <remarks>
    /// The trailing <c>Z</c> is required rather than inferred. A bare date or a time with no offset
    /// would have to be given a timezone here, and the timezone this class would pick is precisely
    /// the one it does not know - so it refuses instead. That refusal is the difference between a
    /// session close and a guess about a market's trading hours.
    /// </remarks>
    private static bool TryReadInstant(string text, out DateTime value)
    {
        value = default;

        var trimmed = text.Trim();

        if (trimmed.Length == 0 ||
            trimmed[^1] != 'Z' ||
            !trimmed.Contains('T', StringComparison.Ordinal))
        {
            return false;
        }

        if (!DateTime.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed) ||
            parsed.Kind != DateTimeKind.Utc)
        {
            return false;
        }

        value = parsed;

        return true;
    }

    /// <summary>Decodes strictly: an undecodable byte is an error rather than a question mark.</summary>
    private static string Decode(ReadOnlySpan<byte> payload)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var text = encoding.GetString(payload);

        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    private sealed record PriceRow(DateTime SessionCloseUtc, decimal Close, DateTime PublishedAtUtc);

    private sealed record RowFailure(string RuleId, string Reason);
}
