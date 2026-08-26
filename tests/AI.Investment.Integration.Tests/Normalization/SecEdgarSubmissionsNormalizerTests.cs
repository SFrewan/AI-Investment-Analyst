using System.Text;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Ingestion.Providers;
using AI.Investment.Infrastructure.Normalization;
using Xunit;

namespace AI.Investment.Integration.Tests.Normalization;

/// <summary>
/// Reading EDGAR's submissions document, and refusing to read anything else.
/// </summary>
/// <remarks>
/// <para>
/// In the integration project for the same reason as the connector tests: it reaches Infrastructure
/// types, not a database. Nothing here touches the network - the fixture below is a trimmed copy of
/// the document's shape, not a live response.
/// </para>
/// <para>
/// The assertions that matter are the negative ones. A missing field must produce no observation,
/// a document that is not a submissions document must be quarantined rather than half-read, and
/// every observation must carry the caveat that its publication date is a floor. A normaliser that
/// guessed would produce facts indistinguishable from real ones.
/// </para>
/// </remarks>
public sealed class SecEdgarSubmissionsNormalizerTests
{
    private static readonly DateTime Retrieved = new(2026, 8, 22, 14, 30, 0, DateTimeKind.Utc);

    private static readonly IngestionSubject Apple = IngestionSubject.Create("Company", "0000320193");

    /// <summary>A trimmed submissions document, shaped like the real one.</summary>
    private const string AppleSubmissions = """
        {
          "cik": "320193",
          "entityType": "operating",
          "sic": "3571",
          "sicDescription": "Electronic Computers",
          "name": "Apple Inc.",
          "tickers": ["AAPL"],
          "exchanges": ["Nasdaq"],
          "ein": "942404110",
          "stateOfIncorporation": "CA",
          "fiscalYearEnd": "0927",
          "filings": {
            "recent": {
              "accessionNumber": ["0000320193-25-000073"],
              "form": ["10-Q"]
            }
          }
        }
        """;

    private static SecEdgarSubmissionsNormalizer Normalizer() => new();

    private static NormalizationInput Input(string json, IngestionSubject? subject = null)
    {
        var payload = Encoding.UTF8.GetBytes(json);

        return new NormalizationInput(
            SecEdgarProvider.Id,
            DataCategory.CompanyProfile,
            subject ?? Apple,
            ContentHash.Compute(payload),
            payload,
            Retrieved);
    }

    private static async Task<NormalizationResult> Normalize(string json) =>
        await Normalizer().NormalizeAsync(Input(json));

    private static string? ValueOf(NormalizationResult result, string attribute) =>
        result.Observations.FirstOrDefault(o => o.Attribute == attribute)?.Value.Canonical;

    // ---------- what it claims to read ----------

    [Fact]
    public void It_reads_company_profiles_from_EDGAR() =>
        Assert.True(Normalizer().CanNormalize(SecEdgarProvider.Id, DataCategory.CompanyProfile));

    [Fact]
    public void It_does_not_claim_categories_it_cannot_read() =>

        // The submissions document also carries filing history, but filings deserve their own
        // normaliser rather than a corner of this one. Claiming the category here would mean the
        // pipeline stops looking for the normaliser that will actually read them.
        Assert.False(Normalizer().CanNormalize(SecEdgarProvider.Id, DataCategory.RegulatoryFilings));

    [Fact]
    public void It_does_not_claim_other_sources() =>
        Assert.False(Normalizer().CanNormalize(
            SourceId.Create("some-other-source"),
            DataCategory.CompanyProfile));

    // ---------- reading a well-formed document ----------

    [Fact]
    public async Task A_well_formed_document_is_not_quarantined() =>
        Assert.False((await Normalize(AppleSubmissions)).IsQuarantined);

    [Theory]
    [InlineData("company.name", "Apple Inc.")]
    [InlineData("company.entity-type", "operating")]
    [InlineData("company.sic", "3571")]
    [InlineData("company.sic-description", "Electronic Computers")]
    [InlineData("company.state-of-incorporation", "CA")]
    [InlineData("company.fiscal-year-end", "0927")]
    [InlineData("company.ein", "942404110")]
    [InlineData("company.ticker", "AAPL")]
    [InlineData("company.exchange", "Nasdaq")]
    public async Task Each_known_field_becomes_its_attribute(string attribute, string expected) =>
        Assert.Equal(expected, ValueOf(await Normalize(AppleSubmissions), attribute));

    [Fact]
    public async Task Observations_are_attached_to_the_requested_subject()
    {
        var result = await Normalize(AppleSubmissions);

        // Taken from the request, never parsed out of the payload. A mislabelled response must not
        // be able to attach facts to a company nobody asked about.
        Assert.All(result.Observations, o => Assert.Equal(Apple, o.Subject));
    }

    [Fact]
    public async Task Every_observation_is_a_fact()
    {
        var result = await Normalize(AppleSubmissions);

        Assert.All(result.Observations, o => Assert.Equal(ClaimKind.Fact, o.Kind));
        Assert.All(result.Observations, o => Assert.Null(o.Confidence));
    }

    [Fact]
    public async Task Every_observation_carries_the_timing_caveat()
    {
        var result = await Normalize(AppleSubmissions);

        // The submissions document has no publication date of its own, so the retrieval time is a
        // floor rather than a fact. A backtest filtering on publication needs to be told that, and
        // the only place to tell it is on the observation itself.
        Assert.All(
            result.Observations,
            o => Assert.Contains(SecEdgarSubmissionsNormalizer.TimingCaveat, o.Caveats));
    }

    [Fact]
    public async Task All_three_provenance_timestamps_are_the_retrieval_time()
    {
        var result = await Normalize(AppleSubmissions);

        Assert.All(result.Observations, o =>
        {
            Assert.Equal(Retrieved, o.Provenance.AsOfUtc);
            Assert.Equal(Retrieved, o.Provenance.PublishedAtUtc);
            Assert.Equal(Retrieved, o.Provenance.RetrievedAtUtc);
        });
    }

    [Fact]
    public async Task Provenance_names_the_source_and_the_record()
    {
        var result = await Normalize(AppleSubmissions);

        Assert.All(result.Observations, o =>
        {
            Assert.Equal(SecEdgarProvider.Id, o.Provenance.SourceId);
            Assert.Equal("0000320193", o.Provenance.SourceRecordId);
        });
    }

    [Fact]
    public async Task Only_the_first_ticker_and_exchange_are_recorded()
    {
        const string multiple = """
            {
              "name": "Example Corp",
              "tickers": ["EXA", "EXB", "EXC"],
              "exchanges": ["NYSE", "Nasdaq"]
            }
            """;

        var result = await Normalize(multiple);

        // A company's primary listing is one fact. Flattening three tickers into one attribute
        // would produce a value that is true of none of them.
        Assert.Single(result.Observations, o => o.Attribute == "company.ticker");
        Assert.Equal("EXA", ValueOf(result, "company.ticker"));
        Assert.Equal("NYSE", ValueOf(result, "company.exchange"));
    }

    // ---------- nothing is invented ----------

    [Fact]
    public async Task A_missing_field_produces_no_observation()
    {
        const string sparse = """{"name": "Example Corp"}""";

        var result = await Normalize(sparse);

        Assert.Single(result.Observations);
        Assert.Null(ValueOf(result, "company.ein"));
        Assert.Null(ValueOf(result, "company.ticker"));
    }

    [Theory]
    [InlineData("""{"name": "Example Corp", "ein": ""}""")]
    [InlineData("""{"name": "Example Corp", "ein": "   "}""")]
    [InlineData("""{"name": "Example Corp", "ein": null}""")]
    [InlineData("""{"name": "Example Corp", "ein": 942404110}""")]
    public async Task A_blank_wrongly_typed_or_null_field_produces_no_observation(string json)
    {
        // Including the numeric case. EDGAR states the EIN as a string; a number where a string
        // belongs means the document is not what this normaliser understands, and coercing it
        // would be the normaliser deciding what the source meant.
        var result = await Normalize(json);

        Assert.Null(ValueOf(result, "company.ein"));
    }

    [Fact]
    public async Task An_empty_ticker_array_produces_no_ticker()
    {
        const string unlisted = """{"name": "Private Co", "tickers": [], "exchanges": []}""";

        var result = await Normalize(unlisted);

        Assert.Null(ValueOf(result, "company.ticker"));
        Assert.Null(ValueOf(result, "company.exchange"));
    }

    [Fact]
    public async Task A_non_string_entry_is_skipped_in_favour_of_the_first_usable_one()
    {
        const string mixed = """{"name": "Example Corp", "tickers": [null, 42, "EXA"]}""";

        var result = await Normalize(mixed);

        Assert.Equal("EXA", ValueOf(result, "company.ticker"));
    }

    [Fact]
    public async Task An_overlong_value_costs_only_its_own_observation()
    {
        var overlong = new string('x', ObservationValue.MaxTextLength + 1);
        var json = $$"""{"name": "Example Corp", "sicDescription": "{{overlong}}"}""";

        var result = await Normalize(json);

        // One unusable field must not cost the whole document. The value is skipped, never
        // truncated into a different value and never substituted.
        Assert.False(result.IsQuarantined);
        Assert.Equal("Example Corp", ValueOf(result, "company.name"));
        Assert.Null(ValueOf(result, "company.sic-description"));
    }

    [Fact]
    public async Task Unknown_fields_are_ignored_rather_than_absorbed()
    {
        const string extended = """
            {
              "name": "Example Corp",
              "somethingTheProviderAddedLastTuesday": "value",
              "filings": {"recent": {"form": ["10-K"]}}
            }
            """;

        var result = await Normalize(extended);

        // A normaliser that absorbed everything would break the day the provider added a field.
        Assert.False(result.IsQuarantined);
        Assert.Single(result.Observations);
    }

    // ---------- what it refuses ----------

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"name\": ")]
    [InlineData("")]
    public async Task An_unreadable_payload_is_quarantined(string json)
    {
        var result = await Normalize(json);

        Assert.True(result.IsQuarantined);
        Assert.Equal(SecEdgarSubmissionsNormalizer.UnreadableRule, result.RuleId);
        Assert.Empty(result.Observations);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    public async Task A_document_that_is_not_an_object_is_quarantined(string json)
    {
        var result = await Normalize(json);

        Assert.True(result.IsQuarantined);
        Assert.Equal(SecEdgarSubmissionsNormalizer.NotASubmissionsDocumentRule, result.RuleId);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"cik": "320193", "sic": "3571"}""")]
    [InlineData("""{"name": ""}""")]
    public async Task A_document_with_no_company_name_is_quarantined(string json)
    {
        // Without a name there is nothing to confirm this is a submissions document, and reading
        // the other fields anyway would turn an error page into facts about a company.
        var result = await Normalize(json);

        Assert.True(result.IsQuarantined);
        Assert.Equal(SecEdgarSubmissionsNormalizer.NotASubmissionsDocumentRule, result.RuleId);
        Assert.Empty(result.Observations);
    }

    [Fact]
    public async Task A_quarantine_reason_never_quotes_the_payload()
    {
        const string secret = """{"password": "hunter2-should-never-be-copied"}""";

        var result = await Normalize(secret);

        // A quarantine record is long-lived and unredactable. A malformed response is exactly the
        // kind of thing that might carry something sensitive, and the bytes are already in the
        // archive for anyone who needs them.
        Assert.True(result.IsQuarantined);
        Assert.DoesNotContain("hunter2", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sweep_subject_leaves_the_source_record_unset()
    {
        var input = Input(AppleSubmissions, IngestionSubject.Sweep("Company"));

        var result = await Normalizer().NormalizeAsync(input);

        // No identifier was asked for, so none is claimed. Inventing one would be a fact about a
        // record that was never requested.
        Assert.All(result.Observations, o => Assert.Null(o.Provenance.SourceRecordId));
    }

    [Fact]
    public async Task Null_input_is_refused() =>
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => Normalizer().NormalizeAsync(null!));
}
