using System.Globalization;
using System.Text.Json;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Ingestion.Providers;

namespace AI.Investment.Infrastructure.Normalization;

/// <summary>
/// Reads the filing history EDGAR's submissions document carries into one observation set per filing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The normaliser <see cref="SecEdgarSubmissionsNormalizer"/> declined to be.</strong> That
/// class reads the company's current state from the same document and says so in its own remarks:
/// filings deserve their own normaliser rather than a corner of one about company profiles. Until
/// this existed, a <c>RegulatoryFilings</c> request archived its bytes correctly and then quarantined
/// every one of them under <c>normalization.no-normalizer@1</c>, producing no observations at all.
/// </para>
/// <para>
/// <strong>The document is the one the connector already fetches.</strong>
/// <c>SecEdgarEndpoints.ForCategory(RegulatoryFilings, cik)</c> resolves to
/// <c>submissions/CIK{cik}.json</c>, whose <c>filings.recent</c> object holds parallel arrays -
/// <c>accessionNumber</c>, <c>form</c>, <c>filingDate</c>, <c>reportDate</c>,
/// <c>acceptanceDateTime</c>, <c>primaryDocument</c>, <c>primaryDocDescription</c> - one entry per
/// filing at the same index. Nothing here invents a shape: a column that is absent produces no
/// attribute, and a row that cannot be placed in time produces no observation.
/// </para>
/// <para>
/// <strong>Timing is the reason this class is careful rather than short.</strong>
/// <c>Claim.Validate</c> refuses a fact whose publication precedes the period it describes, and one
/// retrieved before it was published. So the ordering
/// <c>AsOfUtc &lt;= PublishedAtUtc &lt;= RetrievedAtUtc</c> is not a convention here, it is a domain
/// rule that throws - and it throws <see cref="DomainRuleViolationException"/>, which the sibling
/// normaliser's guard does not catch. A filing whose stated period is <em>later</em> than the instant
/// the SEC accepted it - a Form 25 with a future effective date is the case this investigation
/// found - would violate it. The rule below is therefore explicit: <strong>the acceptance instant is
/// always the publication, a stated period later than acceptance is never used as
/// <c>AsOfUtc</c></strong>, and that later date survives as its own
/// <see cref="ObservationValue.Timestamp"/> attribute instead of being lost.
/// </para>
/// <para>
/// <strong>What this class deliberately does not decide.</strong> Whether an empty result means
/// "nothing was filed" or "the history does not reach the question" is not a normalisation
/// judgement - it is a coverage judgement, and it belongs to the completeness rule that already
/// exists for it. A document stating no filings yields no observations here and no verdict. Nor does
/// this class filter by form, by member or by window: it records what the document states, and every
/// selection - including the one member-scoped secondary form list the installed authorisation
/// carries - is applied downstream against the declaration that approved it.
/// </para>
/// </remarks>
public sealed class SecEdgarFilingsNormalizer : INormalizer
{
    /// <summary>The payload is not JSON this build can parse.</summary>
    public const string UnreadableRule = "normalization.unreadable-payload@1";

    /// <summary>The document is JSON, but not an EDGAR submissions document.</summary>
    public const string NotASubmissionsDocumentRule = "normalization.unexpected-document@1";

    /// <summary>The accession number, EDGAR's identity for one filing.</summary>
    public const string AccessionAttribute = "filing.accession-number";

    /// <summary>The form type, exactly as stated. Never mapped, never classified.</summary>
    public const string FormAttribute = "filing.form";

    /// <summary>The date EDGAR records the filing under.</summary>
    public const string FilingDateAttribute = "filing.date";

    /// <summary>The instant EDGAR accepted it, which is when it became public.</summary>
    public const string AcceptedAtAttribute = "filing.accepted-at";

    /// <summary>The period the filing states it is about, when it states one.</summary>
    public const string ReportDateAttribute = "filing.report-date";

    /// <summary>The primary document's filename within the filing.</summary>
    public const string PrimaryDocumentAttribute = "filing.primary-document";

    /// <summary>EDGAR's own description of the primary document, when it gives one.</summary>
    public const string DescriptionAttribute = "filing.description";

    /// <summary>
    /// States what the publication timestamp means, on every filing observation.
    /// </summary>
    public const string PublicationCaveat =
        "PublishedAtUtc is the instant EDGAR accepted the filing, which is when it became public. " +
        "AsOfUtc is the period the filing states when EDGAR states one at or before acceptance, and " +
        "the acceptance instant otherwise.";

    /// <summary>
    /// Says that the acceptance instant was absent and the filing date stands in for it.
    /// </summary>
    public const string FilingDateFloorCaveat =
        "The document states no acceptance instant for this filing; the stated filing date at " +
        "00:00:00Z is used as the publication instant and is therefore a floor, not the moment it " +
        "became public.";

    /// <summary>
    /// Says that a stated period later than acceptance was kept as an attribute and not as a period.
    /// </summary>
    public const string StatedPeriodAfterAcceptanceCaveat =
        "The period this filing states falls after the instant EDGAR accepted it. It is recorded as " +
        "the filing.report-date attribute and is NOT used as AsOfUtc, because a fact cannot describe " +
        "a period that had not begun when it became public.";

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public bool CanNormalize(SourceId sourceId, DataCategory category)
    {
        ArgumentNullException.ThrowIfNull(sourceId);

        return sourceId == SecEdgarProvider.Id && category == DataCategory.RegulatoryFilings;
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
            // The exception type only. The payload stays in the archive; a quarantine reason is
            // long-lived, unredactable, and no place for an excerpt of a response.
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
                    NotASubmissionsDocumentRule,
                    $"Expected a JSON object; the document root is {root.ValueKind}."));
            }

            // The same marker the company-profile normaliser uses. Without it there is nothing to
            // confirm this is EDGAR's document rather than an error page, and reading a filing
            // history out of one would attach filings to a company nobody filed them for.
            if (!TryText(root, "name", out _))
            {
                return Task.FromResult(NormalizationResult.Quarantine(
                    NotASubmissionsDocumentRule,
                    "No 'name' field: this is not an EDGAR submissions document."));
            }

            if (!TryObject(root, "filings", out var filings) ||
                !TryObject(filings, "recent", out var recent))
            {
                return Task.FromResult(NormalizationResult.Quarantine(
                    NotASubmissionsDocumentRule,
                    "No 'filings.recent' object: this document carries no filing history to read."));
            }

            return Task.FromResult(NormalizationResult.Normalized(Read(recent, input)));
        }
    }

    /// <summary>
    /// Reads every row <c>filings.recent</c> states, in the order it states them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rows are keyed by <c>accessionNumber</c>, because that is EDGAR's identity for a filing and
    /// the only column that can address one afterwards. A document whose <c>recent</c> object carries
    /// no accession numbers states no filings this normaliser can identify, and produces an empty
    /// result rather than a quarantine - an empty result is a fact about the document, and what it
    /// means about the question asked is the coverage rule's decision, not this one's.
    /// </para>
    /// <para>
    /// A duplicate accession number is emitted twice, exactly as the document states it. Collapsing
    /// duplicates here would be this class deciding which of two rows the SEC meant; deduplication
    /// is a separate concern with its own rules.
    /// </para>
    /// </remarks>
    private static List<Observation> Read(JsonElement recent, NormalizationInput input)
    {
        var observations = new List<Observation>();

        if (!TryArray(recent, "accessionNumber", out var accessions))
        {
            return observations;
        }

        var forms = Column(recent, "form");
        var filingDates = Column(recent, "filingDate");
        var reportDates = Column(recent, "reportDate");
        var acceptances = Column(recent, "acceptanceDateTime");
        var primaryDocuments = Column(recent, "primaryDocument");
        var descriptions = Column(recent, "primaryDocDescription");

        var rows = accessions.GetArrayLength();

        for (var row = 0; row < rows; row++)
        {
            var accession = TextAt(accessions, row);

            if (accession is null)
            {
                // No identity, so nothing that follows could be addressed afterwards.
                continue;
            }

            var filingDate = DateAt(filingDates, row);
            var acceptance = InstantAt(acceptances, row);
            var reportDate = DateAt(reportDates, row);

            // Acceptance is publication. When the document does not state it, the stated filing date
            // stands in and the observation says so - a floor, disclosed, rather than a guess.
            var published = acceptance ?? filingDate;

            if (published is not { } publishedAt)
            {
                // Neither an acceptance instant nor a filing date. Nothing can be placed in history
                // from this row, and placing it anyway would be inventing when it became public.
                continue;
            }

            if (publishedAt > input.RetrievedAtUtc)
            {
                // A filing accepted after these bytes were fetched cannot be represented: the domain
                // refuses retrieval before publication, and clamping would hide an impossible row.
                continue;
            }

            // THE RULE. A stated period later than acceptance is never the period a fact describes.
            var statedPeriodIsFuture = reportDate is { } stated && stated > publishedAt;

            var asOf = reportDate is { } period && period <= publishedAt ? period : publishedAt;

            var caveats = new List<string>(3) { PublicationCaveat };

            if (acceptance is null)
            {
                caveats.Add(FilingDateFloorCaveat);
            }

            if (statedPeriodIsFuture)
            {
                caveats.Add(StatedPeriodAfterAcceptanceCaveat);
            }

            var provenance = Provenance.Create(
                input.SourceId,
                asOf,
                publishedAt,
                input.RetrievedAtUtc,
                sourceRecordId: accession);

            AddText(observations, input, AccessionAttribute, accession, provenance, caveats);

            AddText(observations, input, FormAttribute, TextAt(forms, row), provenance, caveats);
            AddText(observations, input, PrimaryDocumentAttribute, TextAt(primaryDocuments, row), provenance, caveats);
            AddText(observations, input, DescriptionAttribute, TextAt(descriptions, row), provenance, caveats);

            AddInstant(observations, input, FilingDateAttribute, filingDate, provenance, caveats);
            AddInstant(observations, input, AcceptedAtAttribute, acceptance, provenance, caveats);

            // Recorded whenever the document states it, and the caveat above says whether it was
            // usable as the period. This is where a future effective date survives.
            AddInstant(observations, input, ReportDateAttribute, reportDate, provenance, caveats);
        }

        return observations;
    }

    /// <summary>
    /// Adds one text observation, skipping any single value the domain refuses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The value is constructed inside the guard, and that is the whole point.</strong>
    /// <see cref="SecEdgarSubmissionsNormalizer"/> documents this trap in its own remarks: a helper
    /// that takes a ready-made <see cref="ObservationValue"/> evaluates the construction at the
    /// <em>call site</em>, before the method is entered, so the one case the guard exists for -
    /// a field longer than the domain permits - is the one case it cannot catch. This class was
    /// written with that shape and the overlong-value test failed on it immediately. Taking the raw
    /// string and building the value here is the fix, and it is why no <c>ObservationValue</c> is
    /// constructed anywhere outside one of these two methods.
    /// </para>
    /// <para>
    /// <strong>Both refusal types are caught, and the second matters more here than anywhere else.</strong>
    /// <see cref="DomainValidationException"/> is a value the domain will not hold.
    /// <see cref="DomainRuleViolationException"/> is a claim the domain will not make, which is what
    /// a temporal ordering failure raises - and filings carry three dates each, so this is exactly
    /// where one would arise. The sibling catches only the first; an ordering failure would abort a
    /// whole document there.
    /// </para>
    /// </remarks>
    private static void AddText(
        List<Observation> observations,
        NormalizationInput input,
        string attribute,
        string? value,
        Provenance provenance,
        List<string> caveats)
    {
        if (value is null)
        {
            // Absent is absent. An observation that exists only because a field was missing is
            // worse than a gap, because a gap is visible.
            return;
        }

        try
        {
            observations.Add(Observation.RecordFact(
                input.Subject,
                attribute,
                ObservationValue.Text(value),
                provenance,
                caveats));
        }
        catch (DomainValidationException)
        {
            // Skipped, never truncated and never substituted.
        }
        catch (DomainRuleViolationException)
        {
            // A claim the domain refuses to make. One unusable row must not cost the document.
        }
    }

    /// <summary>Adds one timestamp observation, under the same guard and for the same reasons.</summary>
    /// <remarks>
    /// <see cref="ObservationValue.Timestamp"/> refuses a non-UTC instant, and every value reaching
    /// it here is built as UTC - but it is constructed inside the guard anyway, because "this one
    /// cannot throw" is the assumption the text path was wrong about.
    /// </remarks>
    private static void AddInstant(
        List<Observation> observations,
        NormalizationInput input,
        string attribute,
        DateTime? value,
        Provenance provenance,
        List<string> caveats)
    {
        if (value is not { } instant)
        {
            return;
        }

        try
        {
            observations.Add(Observation.RecordFact(
                input.Subject,
                attribute,
                ObservationValue.Timestamp(instant),
                provenance,
                caveats));
        }
        catch (DomainValidationException)
        {
        }
        catch (DomainRuleViolationException)
        {
        }
    }

    // ---- reading the document's own shape --------------------------------------------------------

    private static bool TryObject(JsonElement parent, string property, out JsonElement value) =>
        parent.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.Object;

    private static bool TryArray(JsonElement parent, string property, out JsonElement value) =>
        parent.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.Array;

    private static bool TryText(JsonElement parent, string property, out string value)
    {
        value = string.Empty;

        if (!parent.TryGetProperty(property, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = element.GetString();

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text;

        return true;
    }

    /// <summary>One parallel column, or null when the document does not carry it.</summary>
    private static JsonElement? Column(JsonElement recent, string property) =>
        TryArray(recent, property, out var array) ? array : null;

    /// <summary>
    /// The string at one index of a column, or null when there is not a usable one there.
    /// </summary>
    /// <remarks>
    /// A short column is not an error. EDGAR's arrays are parallel, but a normaliser that required
    /// them to be equal in length would refuse a whole document over one trailing field, and the
    /// row's identity does not depend on any column but its own.
    /// </remarks>
    private static string? TextAt(JsonElement? column, int index)
    {
        if (column is not { } array || index >= array.GetArrayLength())
        {
            return null;
        }

        var element = array[index];

        if (element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = element.GetString();

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>A <c>yyyy-MM-dd</c> column entry as midnight UTC, or null.</summary>
    private static DateTime? DateAt(JsonElement? column, int index)
    {
        var text = TextAt(column, index);

        if (text is null)
        {
            return null;
        }

        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
            : null;
    }

    /// <summary>An acceptance timestamp as UTC, or null.</summary>
    /// <remarks>
    /// EDGAR writes these as <c>2024-11-01T06:01:36.000Z</c>. Parsed invariantly and adjusted to
    /// UTC, so an entry that omits its offset is read as UTC rather than as the machine's local
    /// time - which would move a publication instant by hours depending on where the run happened.
    /// </remarks>
    private static DateTime? InstantAt(JsonElement? column, int index)
    {
        var text = TextAt(column, index);

        if (text is null)
        {
            return null;
        }

        return DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var instant)
            ? DateTime.SpecifyKind(instant, DateTimeKind.Utc)
            : null;
    }
}
