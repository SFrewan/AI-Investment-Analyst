using System.Globalization;
using System.Text.Json;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Ingestion.Providers;

namespace AI.Investment.Infrastructure.Normalization;

/// <summary>
/// Turns EDGAR's XBRL <c>companyfacts</c> document into the financial figures the calculators read.
/// </summary>
/// <remarks>
/// <para>
/// This is the adapter that was missing. The connector already reaches
/// <c>api/xbrl/companyfacts/CIK{cik}.json</c>, the ratio, growth and sum calculators already exist,
/// and <c>ScoringSpecifications.FinancialHealthV1</c> is already specified over four of the figures
/// emitted here - but no fetched fact could become an <see cref="Observation"/>, so all of it sat
/// idle. Nothing else changes: the attribute names are the ones
/// <see cref="FinancialFigures"/> already declares, so the calculators find their terms without
/// being told about EDGAR.
/// </para>
/// <para>
/// <strong>The reason this source is worth having is its dates.</strong> Every XBRL fact carries
/// both the period it describes (<c>end</c>) and the date the filing became public (<c>filed</c>),
/// which is exactly the pair <see cref="Provenance"/> is built around. The submissions document has
/// no publication date and has to fall back on retrieval time; this one does not. A backtest
/// filtered on <c>PublishedAtUtc</c> therefore sees a company's figures on the day the market saw
/// them, not on the day the period ended - and the gap between those two dates is the single most
/// common way a fundamental backtest is quietly wrong.
/// </para>
/// <para>
/// Restatements are emitted rather than resolved. The same <c>(tag, end)</c> pair recurs in later
/// filings with a new accession number, a later <c>filed</c> date and sometimes a different value;
/// each becomes its own observation, and the point-in-time read decides which one was in force at
/// the instant being asked about. Collapsing them here would destroy the only record of what the
/// platform could have known at the time.
/// </para>
/// <para>
/// <strong>An identical repeat is not a restatement, and is emitted once.</strong> EDGAR carries
/// one entry per accession that reported a figure, so a 10-K and the 10-K/A that amends it both
/// carry the untouched lines - same period, same <c>filed</c> date, same value, different accession
/// number. Those are one fact reported once and indexed twice. See <see cref="Identity"/> for why
/// the de-duplication key is the one it is.
/// </para>
/// </remarks>
public sealed class SecEdgarCompanyFactsNormalizer : INormalizer
{
    public const string UnreadableRule = "normalization.unreadable-payload@1";
    public const string NotACompanyFactsDocumentRule = "normalization.unexpected-document@1";

    /// <summary>The taxonomy read. Company-specific extensions are deliberately not.</summary>
    public const string Taxonomy = "us-gaap";

    public const string MoneyUnit = "USD";
    public const string ShareUnit = "shares";

    /// <summary>
    /// A period this long or longer counts as a year.
    /// </summary>
    /// <remarks>
    /// Duration facts arrive for quarters, half-years, nine-month stubs and full years, all under
    /// the same tag. A net margin computed from a quarter's revenue and a year's net income would be
    /// wrong by a factor of four and would look entirely plausible, so anything that is not
    /// approximately a year is skipped rather than guessed at. The window is generous because fiscal
    /// years are 52 or 53 weeks and rarely exactly 365 days.
    /// </remarks>
    public const int AnnualMinimumDays = 340;

    public const int AnnualMaximumDays = 400;

    /// <summary>
    /// A period this long counts as a quarter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Thirteen weeks is 91 days and fiscal quarters wander either side of it, so the window is
    /// generous enough for a 4-4-5 calendar and narrow enough that a half-year (about 182 days) or
    /// a nine-month stub (about 273) still falls outside it. Those two remain skipped: they are
    /// neither a quarter nor a year, and guessing which they were meant to be is the error this
    /// whole filter exists to avoid.
    /// </para>
    /// <para>
    /// <strong>A quarter is emitted under its own attribute, never the annual one.</strong> The
    /// facts were always in the payload; only the annual ones were ever kept. Admitting them under
    /// the same name would put a quarter's revenue and a year's net income within reach of one
    /// ratio, which is wrong by a factor of four and looks entirely plausible.
    /// </para>
    /// </remarks>
    public const int QuarterMinimumDays = 80;

    public const int QuarterMaximumDays = 100;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Which XBRL tags stand for which figure, in preference order.
    /// </summary>
    /// <remarks>
    /// The tags are alternatives, not additions: filers tag the same line differently and the first
    /// one present wins, so a company reporting both <c>Revenues</c> and the newer contract-revenue
    /// tag does not produce two conflicting revenue figures for one period. Nothing here is summed -
    /// a derived figure belongs to a calculator that can cite its terms, not to a normalizer that
    /// would present the result as something the filer reported.
    /// </remarks>
    private static readonly FigureMapping[] Mappings =
    [
        new(FinancialFigures.Revenue, MoneyUnit, true,
            ["RevenueFromContractWithCustomerExcludingAssessedTax", "Revenues", "SalesRevenueNet"],
            FinancialFigures.QuarterlyRevenue),
        new(FinancialFigures.GrossProfit, MoneyUnit, true, ["GrossProfit"],
            FinancialFigures.QuarterlyGrossProfit),
        new(FinancialFigures.OperatingIncome, MoneyUnit, true, ["OperatingIncomeLoss"],
            FinancialFigures.QuarterlyOperatingIncome),
        new(FinancialFigures.NetIncome, MoneyUnit, true, ["NetIncomeLoss", "ProfitLoss"],
            FinancialFigures.QuarterlyNetIncome),
        new(FinancialFigures.DepreciationAndAmortisation, MoneyUnit, true,
            ["DepreciationDepletionAndAmortization", "DepreciationAmortizationAndAccretionNet"],
            FinancialFigures.QuarterlyDepreciationAndAmortisation),
        new(FinancialFigures.OperatingCashFlow, MoneyUnit, true,
            [
                "NetCashProvidedByUsedInOperatingActivities",
                "NetCashProvidedByUsedInOperatingActivitiesContinuingOperations",
            ],
            FinancialFigures.QuarterlyOperatingCashFlow),
        new(FinancialFigures.CapitalExpenditure, MoneyUnit, true,
            ["PaymentsToAcquirePropertyPlantAndEquipment", "PaymentsToAcquireProductiveAssets"],
            FinancialFigures.QuarterlyCapitalExpenditure),
        new(FinancialFigures.DilutedShares, ShareUnit, true,
            ["WeightedAverageNumberOfDilutedSharesOutstanding"],
            FinancialFigures.QuarterlyDilutedShares),

        new(FinancialFigures.CashAndEquivalents, MoneyUnit, false,
            ["CashAndCashEquivalentsAtCarryingValue"]),
        new(FinancialFigures.CurrentAssets, MoneyUnit, false, ["AssetsCurrent"]),
        new(FinancialFigures.CurrentLiabilities, MoneyUnit, false, ["LiabilitiesCurrent"]),
        new(FinancialFigures.Inventory, MoneyUnit, false, ["InventoryNet"]),
        new(FinancialFigures.TotalAssets, MoneyUnit, false, ["Assets"]),
        new(FinancialFigures.TotalEquity, MoneyUnit, false,
            ["StockholdersEquity", "StockholdersEquityIncludingPortionAttributableToNoncontrollingInterest"]),
        new(FinancialFigures.TotalDebt, MoneyUnit, false,
            ["DebtLongtermAndShorttermCombinedAmount", "LongTermDebt"]),
    ];

    public bool CanNormalize(SourceId sourceId, DataCategory category)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        return sourceId == SecEdgarProvider.Id && category == DataCategory.FinancialStatements;
    }

    public Task<NormalizationResult> NormalizeAsync(
        NormalizationInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(input.Payload, JsonOptions);
        }
        catch (JsonException exception)
        {
            return Task.FromResult(NormalizationResult.Quarantine(
                UnreadableRule,
                $"The payload is not readable JSON ({exception.GetType().Name})."));
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return Task.FromResult(NormalizationResult.Quarantine(
                    NotACompanyFactsDocumentRule,
                    $"Expected a JSON object; the document root is {root.ValueKind}."));
            }

            if (!root.TryGetProperty("facts", out var facts) ||
                facts.ValueKind != JsonValueKind.Object)
            {
                return Task.FromResult(NormalizationResult.Quarantine(
                    NotACompanyFactsDocumentRule,
                    "No 'facts' object: this is not an EDGAR companyfacts document. The submissions " +
                    "document is a different shape and has its own normalizer."));
            }

            if (!facts.TryGetProperty(Taxonomy, out var taxonomy) ||
                taxonomy.ValueKind != JsonValueKind.Object)
            {
                return Task.FromResult(NormalizationResult.Normalized([]));
            }

            return Task.FromResult(NormalizationResult.Normalized(Read(taxonomy, input)));
        }
    }

    private static List<Observation> Read(JsonElement taxonomy, NormalizationInput input)
    {
        var observations = new List<Observation>();

        // One document, one set. Scoped to this call because the identity it holds is scoped to
        // this document: every fact in it is about the one subject the request named, so the
        // subject is constant and does not need to be in the key. A set shared across documents
        // would suppress one company's fact because another company happened to report the same
        // number for the same period on the same day - which is a coincidence, not a duplicate.
        var recorded = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mapping in Mappings)
        {
            foreach (var tag in mapping.Tags)
            {
                if (!taxonomy.TryGetProperty(tag, out var concept) ||
                    concept.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!concept.TryGetProperty("units", out var units) ||
                    units.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!units.TryGetProperty(mapping.Unit, out var series) ||
                    series.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                // First tag that actually yielded an ANNUAL figure wins; the rest are alternative
                // spellings of the same line and would otherwise produce a second, conflicting one.
                //
                // Counted on the annual attribute specifically, not on the whole list. Once quarters
                // are also emitted, a tag carrying quarterly facts but no annual one would otherwise
                // satisfy "yielded something" and stop the search - and the annual figure that the
                // next tag would have supplied would silently disappear. That is a change to what
                // the annual attribute means, which is the one thing this must not do.
                if (ReadSeries(series, mapping, tag, input, observations, recorded) > 0)
                {
                    break;
                }
            }
        }

        return observations;
    }

    /// <summary>Reads one tag's facts, and reports how many ANNUAL observations it produced.</summary>
    private static int ReadSeries(
        JsonElement series,
        FigureMapping mapping,
        string tag,
        NormalizationInput input,
        List<Observation> observations,
        HashSet<string> recorded)
    {
        var annual = 0;

        foreach (var fact in series.EnumerateArray())
        {
            if (fact.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!TryReadDecimal(fact, "val", out var value) ||
                !TryReadDate(fact, "end", out var periodEnd) ||
                !TryReadDate(fact, "filed", out var filed))
            {
                continue;
            }

            // Which attribute this fact belongs under, or null when it belongs under none. A
            // duration that is neither a quarter nor a year is still skipped.
            var attribute = mapping.Attribute;

            if (mapping.IsDuration)
            {
                if (!TryReadDate(fact, "start", out var periodStart))
                {
                    continue;
                }

                var days = (periodEnd - periodStart).TotalDays;

                if (days is >= AnnualMinimumDays and <= AnnualMaximumDays)
                {
                    // Unchanged: the annual attribute keeps exactly the facts it always had.
                }
                else if (days is >= QuarterMinimumDays and <= QuarterMaximumDays &&
                    mapping.QuarterlyAttribute is not null)
                {
                    attribute = mapping.QuarterlyAttribute;
                }
                else
                {
                    continue;
                }
            }
            else if (fact.TryGetProperty("start", out var start) &&
                start.ValueKind == JsonValueKind.String)
            {
                // A balance-sheet figure carrying a period is not the instantaneous fact this
                // attribute means, whatever it is.
                continue;
            }

            // Claims.Fact refuses a value published before the period it describes or retrieved
            // before it was published. Those orderings hold for well-formed EDGAR data; a fact that
            // breaks them is a defect in the payload, and skipping it keeps one bad row from
            // quarantining an otherwise good document.
            if (filed < periodEnd || filed > input.RetrievedAtUtc)
            {
                continue;
            }

            var accession = ReadText(fact, "accn");

            var provenance = Provenance.Create(
                input.SourceId,
                asOfUtc: periodEnd,
                publishedAtUtc: filed,
                retrievedAtUtc: input.RetrievedAtUtc,
                sourceRecordId: accession);

            var added = observations.Count;

            Add(observations, input, mapping, attribute, tag, fact, value, provenance, recorded);

            if (observations.Count > added &&
                string.Equals(attribute, mapping.Attribute, StringComparison.Ordinal))
            {
                annual++;
            }
        }

        return annual;
    }

    private static void Add(
        List<Observation> observations,
        NormalizationInput input,
        FigureMapping mapping,
        string attribute,
        string tag,
        JsonElement fact,
        decimal value,
        Provenance provenance,
        HashSet<string> recorded)
    {
        var caveats = new List<string>(3)
        {
            $"Reported under the {Taxonomy} tag '{tag}', in {mapping.Unit}.",
        };

        var form = ReadText(fact, "form");

        if (form is not null)
        {
            caveats.Add($"From a {form} filing.");
        }

        var fiscalPeriod = FiscalPeriod(fact);

        if (fiscalPeriod is not null)
        {
            caveats.Add($"Fiscal period {fiscalPeriod}.");
        }

        try
        {
            // Built inside the guard, for the same reason the submissions normalizer builds its
            // value inside one: an argument is evaluated at the call site, so a value constructed
            // in the argument list is constructed OUTSIDE the try that exists to contain it.
            var recordedValue = ObservationValue.Number(value);

            // The first entry to report a fact keeps it, and the ones that merely reindex it are
            // dropped. First, not last, and that is the substantive choice here: the accession
            // retained is the filing in which the figure actually became public, which is what
            // Provenance claims to record. Keeping the amendment's accession instead would cite a
            // document that changed nothing for a value nobody changed.
            if (!recorded.Add(Identity(attribute, provenance, recordedValue)))
            {
                return;
            }

            observations.Add(Observation.RecordFact(
                input.Subject,
                attribute,
                recordedValue,
                provenance,
                caveats));
        }
        catch (DomainValidationException)
        {
            // A single malformed fact is a data-quality defect in one row, not grounds for refusing
            // the document. The submissions normalizer takes the same view.
        }
        catch (DomainRuleViolationException)
        {
        }
    }

    /// <summary>
    /// What makes two facts in one document the same fact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This key is the observation store's own identity, deliberately and exactly.</strong>
    /// An observation is identified by subject, attribute, period end, publication instant and
    /// canonical value; the subject is constant within a document, so the other four are the whole
    /// key. Nothing weaker would remove the duplicates - and nothing STRICTER could. Adding the
    /// accession number, the form or the fiscal-period label would make the two entries distinct
    /// here and leave them indistinguishable once stored, so the duplicate row would survive in
    /// exactly the place it does harm. A de-duplication key finer than the identity of the thing it
    /// de-duplicates does not de-duplicate anything.
    /// </para>
    /// <para>
    /// It also cannot lose a restatement. A restatement changes the value, the filing date, or
    /// both, so it differs in the key and is emitted as its own observation - which is the property
    /// the point-in-time read depends on. Only a byte-identical repeat is dropped, and a
    /// byte-identical repeat carries no information the first one did not.
    /// </para>
    /// <para>
    /// Round-trip formats for the instants, not the default: "O" is unambiguous to the tick and
    /// culture-invariant, so two facts an hour apart cannot collide through a format that rounds.
    /// </para>
    /// </remarks>
    private static string Identity(
        string attribute,
        Provenance provenance,
        ObservationValue value) =>
        string.Join(
            '|',
            attribute,
            provenance.AsOfUtc.ToString("O", CultureInfo.InvariantCulture),
            provenance.PublishedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            value.Canonical);

    private static string? FiscalPeriod(JsonElement fact)
    {
        var period = ReadText(fact, "fp");

        if (!fact.TryGetProperty("fy", out var year) || year.ValueKind != JsonValueKind.Number)
        {
            return period;
        }

        return period is null
            ? year.GetInt32().ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{period} {year.GetInt32()}");
    }

    private static string? ReadText(JsonElement fact, string property)
    {
        if (!fact.TryGetProperty(property, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = element.GetString();

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool TryReadDecimal(JsonElement fact, string property, out decimal value)
    {
        value = 0m;

        return fact.TryGetProperty(property, out var element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetDecimal(out value);
    }

    private static bool TryReadDate(JsonElement fact, string property, out DateTime value)
    {
        value = default;

        var text = ReadText(fact, property);

        return text is not null &&
            DateTime.TryParseExact(
                text,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out value);
    }

    /// <param name="Attribute">The platform's name for the figure.</param>
    /// <param name="Unit">The XBRL unit key the figure must be reported in.</param>
    /// <param name="IsDuration">True for a flow measured over a period, false for a balance.</param>
    /// <param name="Tags">Alternative XBRL tags, in preference order.</param>
    /// <param name="QuarterlyAttribute">
    /// Where a quarter-length fact for this figure is recorded, or null when the figure has no
    /// quarterly meaning. Balance-sheet items are instants and leave this null.
    /// </param>
    private sealed record FigureMapping(
        string Attribute,
        string Unit,
        bool IsDuration,
        string[] Tags,
        string? QuarterlyAttribute = null);
}
