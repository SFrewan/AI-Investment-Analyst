using System.Globalization;
using System.Text.Json;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Ingestion.Providers;
using Microsoft.Extensions.Options;

namespace AI.Investment.Infrastructure.Normalization;

/// <summary>
/// Reads an archived EODHD splits document into <c>security.split-ratio</c> observations.
/// </summary>
/// <remarks>
/// <para>
/// A row is <c>{"date":"2024-06-10","split":"10.000000/1.000000"}</c>: an effective date and a
/// ratio written as new shares over old. It becomes one observation, stamped at the same instant
/// the price normaliser stamps that trading date, which is what makes the two series comparable.
/// </para>
/// <para>
/// <strong>The stamp matters more than it looks.</strong>
/// <see cref="Domain.Opportunities.Equity.SplitAdjustment"/> restates every close printed
/// <em>strictly before</em> a split's instant. Stamping the split at its effective session close
/// therefore leaves that session's own close alone - it is already quoted in the new shares - and
/// restates everything earlier. An off-by-one here would invent a fall on exactly one day, which
/// is the kind of defect that survives review because every individual number looks plausible.
/// </para>
/// <para>
/// <strong>Publication is stamped at the effective date, not at announcement.</strong> The vendor
/// gives the effective date and nothing about when the split was announced, and inventing an
/// earlier publication instant would let a replay use a fact before the platform could have had
/// it. Stamping it late is the safe direction and costs nothing real: before the effective date
/// there is no step in the price series to explain, so a backtest asked about an earlier instant
/// does not need the split and correctly cannot see it.
/// </para>
/// <para>
/// <strong>An empty array is a fact, not a failure.</strong> Most instruments have never split.
/// This returns zero observations for that document rather than quarantining it, because "nothing
/// happened" and "the payload was unreadable" must not arrive at the ledger as the same thing.
/// </para>
/// </remarks>
public sealed class EodhdSplitsNormalizer : INormalizer
{
    /// <summary>The canonical attribute a split ratio is recorded under.</summary>
    /// <remarks>
    /// The same string <c>DiscoverySettings.SplitAttribute</c> defaults to and the price reader
    /// asks for. Stated once here; a test asserts the two agree, because a mismatch produces a
    /// series that is silently refused rather than an error anybody would see.
    /// </remarks>
    public const string SplitAttribute = "security.split-ratio";

    /// <summary>The document is not a JSON array of split rows.</summary>
    public const string UnexpectedShapeRule = "eodhd-splits.unexpected-shape@1";

    /// <summary>A row is present but cannot be read as a dated ratio.</summary>
    public const string UnreadableRowRule = "eodhd-splits.unreadable-row@1";

    /// <summary>No session was stated for the symbol's exchange.</summary>
    public const string UnstatedSessionRule = "market-data.unstated-session@1";

    /// <summary>The symbol on the request is not one this connector fetches.</summary>
    public const string UnreadableSymbolRule = "eodhd-splits.unreadable-symbol@1";

    private const string DateField = "date";
    private const string SplitField = "split";

    private readonly EodhdOptions _options;

    public EodhdSplitsNormalizer(IOptions<EodhdOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
    }

    public bool CanNormalize(SourceId sourceId, DataCategory category)
    {
        ArgumentNullException.ThrowIfNull(sourceId);

        return sourceId == EodhdSplitsProvider.Id && category == DataCategory.CorporateActions;
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
                "these splits take effect in cannot be determined."));
        }

        var session = _options.Session(EodhdProvider.ExchangeOf(symbol));

        if (session is null)
        {
            return Task.FromResult(NormalizationResult.Quarantine(
                UnstatedSessionRule,
                $"No session is configured for the exchange in '{symbol}'. EODHD sends an " +
                "effective date and no times, so without a stated session close the split cannot " +
                "be placed against the closing prices it restates."));
        }

        return Task.FromResult(Read(input, session, symbol));
    }

    private static NormalizationResult Read(
        NormalizationInput input,
        ExchangeSessionOptions session,
        string symbol)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(input.Payload);
        }
        catch (JsonException error)
        {
            return NormalizationResult.Quarantine(
                UnexpectedShapeRule,
                $"The archived splits payload for '{symbol}' is not JSON: {error.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return NormalizationResult.Quarantine(
                    UnexpectedShapeRule,
                    $"The archived splits payload for '{symbol}' is a " +
                    $"{document.RootElement.ValueKind} rather than an array of rows.");
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
        var observations = new List<Observation>();
        var caveats = Caveats(session, symbol);
        var index = 0;

        foreach (var row in rows.EnumerateArray())
        {
            index++;

            if (row.ValueKind != JsonValueKind.Object)
            {
                return NormalizationResult.Quarantine(
                    UnreadableRowRule,
                    $"Row {index}: expected an object with a '{DateField}' and a '{SplitField}'.");
            }

            if (!TryReadDate(row, out var effectiveDate))
            {
                return NormalizationResult.Quarantine(
                    UnreadableRowRule,
                    $"Row {index}: the '{DateField}' field is missing or is not a trading date.");
            }

            if (!TryReadRatio(row, out var ratio))
            {
                return NormalizationResult.Quarantine(
                    UnreadableRowRule,
                    $"Row {index}: the '{SplitField}' field is missing, is not written as new " +
                    "shares over old, or states a ratio that cannot restate a price. A ratio the " +
                    "platform cannot read is not a ratio of one - a series it would have restated " +
                    "must be refused instead, and quarantining the document is how that happens.");
            }

            // The same instant the price normaliser stamps this trading date with. See the class
            // remarks: strictly-before is what leaves the effective session's own close alone.
            var effectiveAtUtc = effectiveDate + session.SessionCloseUtc;

            observations.Add(Observation.RecordFact(
                input.Subject,
                SplitAttribute,
                ObservationValue.Number(ratio),
                Provenance.Create(
                    input.SourceId,
                    effectiveAtUtc,
                    effectiveAtUtc,
                    input.RetrievedAtUtc,
                    sourceRecordId: symbol),
                caveats));
        }

        // Zero rows is the ordinary answer for an instrument that has never split.
        return NormalizationResult.Normalized(observations);
    }

    private static bool TryReadDate(JsonElement row, out DateTime effectiveDate)
    {
        effectiveDate = default;

        if (!row.TryGetProperty(DateField, out var field) || field.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (!DateTime.TryParseExact(
                field.GetString(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return false;
        }

        effectiveDate = DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc);

        return true;
    }

    /// <summary>
    /// The ratio as new shares per old share, or false when the row cannot say.
    /// </summary>
    /// <remarks>
    /// EODHD writes it as <c>"4.000000/1.000000"</c>. Both halves must be positive numbers: a zero
    /// or negative on either side cannot restate a price, and a reverse split is expressed as the
    /// numerator being the smaller of the two rather than as a negative.
    /// </remarks>
    private static bool TryReadRatio(JsonElement row, out decimal ratio)
    {
        ratio = 0m;

        if (!row.TryGetProperty(SplitField, out var field) || field.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = field.GetString();

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var separator = text.IndexOf('/', StringComparison.Ordinal);

        if (separator <= 0 || separator == text.Length - 1)
        {
            return false;
        }

        if (!decimal.TryParse(
                text[..separator],
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var newShares))
        {
            return false;
        }

        if (!decimal.TryParse(
                text[(separator + 1)..],
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var oldShares))
        {
            return false;
        }

        if (newShares <= 0m || oldShares <= 0m)
        {
            return false;
        }

        ratio = newShares / oldShares;

        return ratio > 0m;
    }

    private static string[] Caveats(ExchangeSessionOptions session, string symbol) =>
    [
        $"The effective instant is the stated session close ({session.SessionCloseUtc:hh\\:mm} UTC) " +
        $"for exchange '{session.Code}', which is this installation's fact rather than the " +
        "vendor's. EODHD states an effective date and no time.",

        "Publication is stamped at the effective session rather than at announcement, because the " +
        "vendor does not say when the split was announced. A replay therefore sees a split from " +
        "the session it took effect, never earlier.",

        $"The ratio is new shares per old share for '{symbol}', taken from the vendor's " +
        "'new/old' text. It is used to restate raw closes; the platform does not store the " +
        "vendor's adjusted close.",
    ];
}
