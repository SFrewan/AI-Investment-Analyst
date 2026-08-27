using System.Text.Json;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Ingestion.Providers;

namespace AI.Investment.Infrastructure.Normalization;

/// <summary>
/// Reads EDGAR's company submissions document into company-profile observations.
/// </summary>
/// <remarks>
/// <para>
/// Reads a fixed set of top-level fields and ignores everything else, including the filing history
/// the document also carries. Ignoring is deliberate: a normaliser that tried to absorb an entire
/// response would break every time the provider added a field, and filings are a different category
/// deserving their own normaliser rather than a corner of this one.
/// </para>
/// <para>
/// <strong>Nothing is invented.</strong> A field that is absent, blank or unreadable produces no
/// observation. An observation that exists only because a field was missing is worse than a gap,
/// because a gap is visible and a fabricated value is not.
/// </para>
/// <para>
/// <strong>Provenance timing.</strong> EDGAR's submissions document describes a company's current
/// state and carries no publication date of its own, so all three timestamps are the retrieval
/// time. That is honest rather than convenient - the platform learned this at that moment and
/// cannot claim to have known it earlier - and every observation carries a caveat saying so, since
/// a backtest filtering on publication needs to know the date is a floor rather than a fact.
/// </para>
/// </remarks>
public sealed class SecEdgarSubmissionsNormalizer : INormalizer
{
    /// <summary>The payload is not JSON this build can parse.</summary>
    public const string UnreadableRule = "normalization.unreadable-payload@1";

    /// <summary>The document is JSON, but not a submissions document.</summary>
    public const string NotASubmissionsDocumentRule = "normalization.unexpected-document@1";

    public const string TimingCaveat =
        "EDGAR's submissions document carries no publication date; the retrieval time is used for " +
        "all three provenance timestamps and is therefore a floor, not the date the fact became true.";

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The fields read, and the attribute each becomes.</summary>
    private static readonly (string Json, string Attribute)[] TextFields =
    [
        ("name", "company.name"),
        ("entityType", "company.entity-type"),
        ("sic", "company.sic"),
        ("sicDescription", "company.sic-description"),
        ("stateOfIncorporation", "company.state-of-incorporation"),
        ("fiscalYearEnd", "company.fiscal-year-end"),
        ("ein", "company.ein"),
    ];

    public bool CanNormalize(SourceId sourceId, DataCategory category)
    {
        ArgumentNullException.ThrowIfNull(sourceId);

        return sourceId == SecEdgarProvider.Id && category == DataCategory.CompanyProfile;
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
            // The type and message only. The payload itself stays in the archive; a quarantine
            // reason is long-lived, unredactable, and no place for an excerpt of a response.
            return Task.FromResult(NormalizationResult.Quarantine(
                UnreadableRule,
                $"The payload is not readable JSON ({exception.GetType().Name})."));
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Task.FromResult(NormalizationResult.Quarantine(
                    NotASubmissionsDocumentRule,
                    $"Expected a JSON object; the document root is {document.RootElement.ValueKind}."));
            }

            var root = document.RootElement;

            // A submissions document always names the company. Without it there is nothing to
            // attach observations to, and guessing from the request would let a mislabelled
            // response silently become facts about the wrong company.
            if (!TryReadText(root, "name", out _))
            {
                return Task.FromResult(NormalizationResult.Quarantine(
                    NotASubmissionsDocumentRule,
                    "No 'name' field: this is not an EDGAR submissions document."));
            }

            return Task.FromResult(NormalizationResult.Normalized(Read(root, input)));
        }
    }

    private static List<Observation> Read(JsonElement root, NormalizationInput input)
    {
        var provenance = Provenance.Create(
            input.SourceId,
            input.RetrievedAtUtc,
            input.RetrievedAtUtc,
            input.RetrievedAtUtc,
            sourceRecordId: input.Subject.Identifier);

        var caveats = new[] { TimingCaveat };
        var observations = new List<Observation>();

        foreach (var (json, attribute) in TextFields)
        {
            if (TryReadText(root, json, out var value))
            {
                Add(observations, input, attribute, value, provenance, caveats);
            }
        }

        // Tickers and exchanges are parallel arrays. Only the first pair is recorded: a company's
        // primary listing is one fact, and flattening several into one attribute would produce a
        // value that is true of none of them.
        if (TryReadFirstArrayItem(root, "tickers", out var ticker))
        {
            Add(observations, input, "company.ticker", ticker, provenance, caveats);
        }

        if (TryReadFirstArrayItem(root, "exchanges", out var exchange))
        {
            Add(observations, input, "company.exchange", exchange, provenance, caveats);
        }

        return observations;
    }

    /// <summary>
    /// Adds an observation, skipping any single value the domain refuses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One unusable field must not cost the whole document. EDGAR occasionally carries a value
    /// longer or stranger than the domain permits, and losing the other eight observations over it
    /// would be a worse outcome than the missing one.
    /// </para>
    /// <para>
    /// <strong>The raw string is passed in and the value built inside the guard.</strong> This
    /// method used to take a ready-made <see cref="ObservationValue"/>, which meant
    /// <c>ObservationValue.Text(...)</c> ran while evaluating the argument - before this method was
    /// entered, and therefore outside the <c>try</c>. The length rule is enforced during
    /// construction, so the one case the guard existed for was the one case it could not catch: an
    /// overlong field threw straight out of the normaliser instead of being skipped. Arguments are
    /// evaluated at the call site; a guard only covers what happens after it.
    /// </para>
    /// <para>
    /// The domain's 4000-character limit is untouched. What changed is where the refusal is caught.
    /// </para>
    /// </remarks>
    private static void Add(
        List<Observation> observations,
        NormalizationInput input,
        string attribute,
        string value,
        Provenance provenance,
        IEnumerable<string> caveats)
    {
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
            // Skipped, not fabricated and not substituted. The field simply produces no
            // observation, which is a visible gap rather than a wrong value.
        }
    }

    private static bool TryReadText(JsonElement root, string property, out string value)
    {
        value = string.Empty;

        if (!root.TryGetProperty(property, out var element) ||
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

    private static bool TryReadFirstArrayItem(JsonElement root, string property, out string value)
    {
        value = string.Empty;

        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = item.GetString();

            if (!string.IsNullOrWhiteSpace(text))
            {
                value = text;

                return true;
            }
        }

        return false;
    }
}
