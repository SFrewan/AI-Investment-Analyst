using System.Globalization;
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
/// Reading EDGAR's filing history, and refusing to read anything into it that is not there.
/// </summary>
/// <remarks>
/// <para>
/// In the integration project beside the other normaliser tests, for the same reason: it reaches
/// Infrastructure types, not a database. Nothing here touches the network - every fixture is a
/// trimmed copy of the document's real shape.
/// </para>
/// <para>
/// The assertions that matter are the temporal ones. A filing whose stated period falls after the
/// SEC accepted it is the case that would either violate the domain's ordering rule or be silently
/// lost, and the whole reason this normaliser is longer than its siblings.
/// </para>
/// </remarks>
public sealed class SecEdgarFilingsNormalizerTests
{
    private static readonly DateTime Retrieved = new(2026, 8, 22, 14, 30, 0, DateTimeKind.Utc);

    private static readonly IngestionSubject Qumu = IngestionSubject.Create("Company", "0000892482");

    /// <summary>Three filings, shaped exactly as EDGAR states them.</summary>
    private const string Submissions = """
        {
          "cik": "892482",
          "name": "Qumu Corporation",
          "filings": {
            "recent": {
              "accessionNumber": [
                "0000892482-23-000003",
                "0000892482-22-000045",
                "0000892482-22-000031"
              ],
              "filingDate": ["2023-02-14", "2022-12-27", "2022-12-12"],
              "reportDate":  ["2022-12-31", "2022-12-23", ""],
              "acceptanceDateTime": [
                "2023-02-14T16:31:20.000Z",
                "2022-12-27T17:02:11.000Z",
                "2022-12-12T06:01:36.000Z"
              ],
              "form": ["10-K", "25-NSE", "8-K"],
              "primaryDocument": ["qumu-20221231.htm", "form25.html", "qumu-8k.htm"],
              "primaryDocDescription": ["10-K", "NOTICE OF DELISTING", "8-K"]
            },
            "files": []
          }
        }
        """;

    private static SecEdgarFilingsNormalizer Normalizer() => new();

    private static NormalizationInput Input(
        string json,
        IngestionSubject? subject = null,
        DateTime? retrieved = null)
    {
        var payload = Encoding.UTF8.GetBytes(json);

        return new NormalizationInput(
            SecEdgarProvider.Id,
            DataCategory.RegulatoryFilings,
            subject ?? Qumu,
            ContentHash.Compute(payload),
            payload,
            retrieved ?? Retrieved);
    }

    private static async Task<NormalizationResult> Normalize(
        string json,
        DateTime? retrieved = null) =>
        await Normalizer().NormalizeAsync(Input(json, retrieved: retrieved));

    private static List<Observation> Filing(NormalizationResult result, string accession) =>
        result.Observations
            .Where(o => string.Equals(o.Provenance.SourceRecordId, accession, StringComparison.Ordinal))
            .ToList();

    private static string? ValueOf(IEnumerable<Observation> observations, string attribute) =>
        observations.FirstOrDefault(o => o.Attribute == attribute)?.Value.Canonical;

    private static string Instant(DateTime value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    // ---------- 1. what it claims to read ----------

    [Fact]
    public void It_reads_regulatory_filings_from_EDGAR() =>
        Assert.True(Normalizer().CanNormalize(SecEdgarProvider.Id, DataCategory.RegulatoryFilings));

    /// <summary>
    /// It is the only SEC normaliser that claims the category, so the pipeline's first match is this.
    /// </summary>
    /// <remarks>
    /// <c>NormalizationPipeline.FindNormalizer</c> returns the first registered normaliser whose
    /// <c>CanNormalize</c> is true. Before this class existed it returned null and every filings
    /// payload was quarantined under <c>normalization.no-normalizer@1</c>. This asserts both halves
    /// of the fix: this one claims the category, and no sibling competes for it.
    /// </remarks>
    [Fact]
    public void No_other_EDGAR_normaliser_claims_the_category()
    {
        Assert.False(new SecEdgarSubmissionsNormalizer()
            .CanNormalize(SecEdgarProvider.Id, DataCategory.RegulatoryFilings));

        Assert.False(new SecEdgarFramesNormalizer()
            .CanNormalize(SecEdgarProvider.Id, DataCategory.RegulatoryFilings));

        Assert.False(new SecEdgarCompanyFactsNormalizer()
            .CanNormalize(SecEdgarProvider.Id, DataCategory.RegulatoryFilings));
    }

    [Fact]
    public void It_claims_no_other_category_and_no_other_source()
    {
        Assert.False(Normalizer().CanNormalize(SecEdgarProvider.Id, DataCategory.CompanyProfile));
        Assert.False(Normalizer().CanNormalize(SecEdgarProvider.Id, DataCategory.FinancialStatements));

        Assert.False(Normalizer().CanNormalize(
            SourceId.Create("some-other-source"),
            DataCategory.RegulatoryFilings));
    }

    // ---------- 2. a representative document produces filing observations ----------

    [Fact]
    public async Task A_representative_document_produces_one_observation_set_per_filing()
    {
        var result = await Normalize(Submissions);

        Assert.False(result.IsQuarantined);

        var accessions = result.Observations
            .Select(o => o.Provenance.SourceRecordId ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            "0000892482-22-000031,0000892482-22-000045,0000892482-23-000003",
            string.Join(",", accessions));
    }

    [Fact]
    public async Task Every_field_the_document_states_becomes_its_attribute()
    {
        var filing = Filing(await Normalize(Submissions), "0000892482-23-000003");

        Assert.Equal("0000892482-23-000003", ValueOf(filing, SecEdgarFilingsNormalizer.AccessionAttribute));
        Assert.Equal("10-K", ValueOf(filing, SecEdgarFilingsNormalizer.FormAttribute));
        Assert.Equal("qumu-20221231.htm", ValueOf(filing, SecEdgarFilingsNormalizer.PrimaryDocumentAttribute));
        Assert.Equal("10-K", ValueOf(filing, SecEdgarFilingsNormalizer.DescriptionAttribute));

        Assert.Equal(
            Instant(new DateTime(2023, 2, 14, 0, 0, 0, DateTimeKind.Utc)),
            ValueOf(filing, SecEdgarFilingsNormalizer.FilingDateAttribute));

        Assert.Equal(
            Instant(new DateTime(2023, 2, 14, 16, 31, 20, DateTimeKind.Utc)),
            ValueOf(filing, SecEdgarFilingsNormalizer.AcceptedAtAttribute));

        Assert.Equal(
            Instant(new DateTime(2022, 12, 31, 0, 0, 0, DateTimeKind.Utc)),
            ValueOf(filing, SecEdgarFilingsNormalizer.ReportDateAttribute));
    }

    [Fact]
    public async Task Every_observation_is_a_fact_about_the_requested_subject()
    {
        var result = await Normalize(Submissions);

        // The subject comes from the request, never parsed out of the payload: a mislabelled
        // response must not attach filings to a company nobody asked about.
        Assert.All(result.Observations, o => Assert.Equal(Qumu, o.Subject));
        Assert.All(result.Observations, o => Assert.Equal(ClaimKind.Fact, o.Kind));
        Assert.All(result.Observations, o => Assert.Null(o.Confidence));
        Assert.All(result.Observations, o => Assert.Equal(SecEdgarProvider.Id, o.Provenance.SourceId));
    }

    /// <summary>Older and unfamiliar forms pass through as stated, uninterpreted.</summary>
    [Fact]
    public async Task An_unfamiliar_form_is_recorded_verbatim_and_not_classified()
    {
        const string archaic = """
            {
              "name": "Example Corp",
              "filings": {"recent": {
                "accessionNumber": ["0000000000-99-000001"],
                "acceptanceDateTime": ["1999-04-25T16:31:20.000Z"],
                "form": ["S-1MEF"]
              }}
            }
            """;

        var filing = Filing(await Normalize(archaic), "0000000000-99-000001");

        // Recorded, not mapped to a family, not rejected as unsupported. A normaliser that decided
        // which forms mattered would be making the selection the declaration makes.
        Assert.Equal("S-1MEF", ValueOf(filing, SecEdgarFilingsNormalizer.FormAttribute));
    }

    // ---------- 3. empty filings.recent ----------

    [Theory]
    [InlineData("""{"name": "Example Corp", "filings": {"recent": {}}}""")]
    [InlineData("""{"name": "Example Corp", "filings": {"recent": {"accessionNumber": []}}}""")]
    [InlineData("""{"name": "Example Corp", "filings": {"recent": {"form": ["10-K"]}}}""")]
    public async Task An_empty_filing_history_produces_no_observations_and_no_quarantine(string json)
    {
        var result = await Normalize(json);

        // Deterministic and silent about meaning. Whether an empty result is "nothing was filed" or
        // "the history does not reach the question" is the coverage rule's decision, not this one's,
        // and quarantining here would turn a real absence into a data-quality problem.
        Assert.False(result.IsQuarantined);
        Assert.Empty(result.Observations);
    }

    [Fact]
    public async Task A_document_with_no_filings_object_is_quarantined()
    {
        var result = await Normalize("""{"name": "Example Corp"}""");

        Assert.True(result.IsQuarantined);
        Assert.Equal(SecEdgarFilingsNormalizer.NotASubmissionsDocumentRule, result.RuleId);
        Assert.Empty(result.Observations);
    }

    // ---------- 4. provenance ordering ----------

    [Fact]
    public async Task Acceptance_is_publication_and_the_ordering_holds_for_every_observation()
    {
        var result = await Normalize(Submissions);

        Assert.NotEmpty(result.Observations);

        Assert.All(result.Observations, o =>
        {
            Assert.True(
                o.Provenance.AsOfUtc <= o.Provenance.PublishedAtUtc,
                $"{o.Attribute}: AsOfUtc {o.Provenance.AsOfUtc:O} is after PublishedAtUtc {o.Provenance.PublishedAtUtc:O}");

            Assert.True(
                o.Provenance.PublishedAtUtc <= o.Provenance.RetrievedAtUtc,
                $"{o.Attribute}: PublishedAtUtc {o.Provenance.PublishedAtUtc:O} is after RetrievedAtUtc {o.Provenance.RetrievedAtUtc:O}");

            Assert.Equal(Retrieved, o.Provenance.RetrievedAtUtc);
            Assert.Contains(SecEdgarFilingsNormalizer.PublicationCaveat, o.Caveats);
        });

        var tenK = Filing(result, "0000892482-23-000003");

        // The 10-K states a period before acceptance, so the period is the period.
        Assert.All(tenK, o =>
        {
            Assert.Equal(new DateTime(2022, 12, 31, 0, 0, 0, DateTimeKind.Utc), o.Provenance.AsOfUtc);
            Assert.Equal(new DateTime(2023, 2, 14, 16, 31, 20, DateTimeKind.Utc), o.Provenance.PublishedAtUtc);
        });
    }

    [Fact]
    public async Task When_acceptance_is_absent_the_filing_date_stands_in_and_says_so()
    {
        const string noAcceptance = """
            {
              "name": "Example Corp",
              "filings": {"recent": {
                "accessionNumber": ["0000000000-20-000001"],
                "filingDate": ["2020-06-15"],
                "form": ["8-K"]
              }}
            }
            """;

        var filing = Filing(await Normalize(noAcceptance), "0000000000-20-000001");

        Assert.NotEmpty(filing);

        Assert.All(filing, o =>
        {
            Assert.Equal(new DateTime(2020, 6, 15, 0, 0, 0, DateTimeKind.Utc), o.Provenance.PublishedAtUtc);
            Assert.Contains(SecEdgarFilingsNormalizer.FilingDateFloorCaveat, o.Caveats);
        });

        // Nothing was invented: there is no acceptance attribute, because there was no acceptance.
        Assert.Null(ValueOf(filing, SecEdgarFilingsNormalizer.AcceptedAtAttribute));
    }

    [Fact]
    public async Task A_filing_accepted_after_retrieval_produces_nothing_rather_than_a_clamped_time()
    {
        const string impossible = """
            {
              "name": "Example Corp",
              "filings": {"recent": {
                "accessionNumber": ["0000000000-27-000001"],
                "acceptanceDateTime": ["2027-01-04T10:00:00.000Z"],
                "form": ["8-K"]
              }}
            }
            """;

        var result = await Normalize(impossible);

        // Retrieval precedes publication, which the domain refuses outright. Clamping the instant
        // back to the retrieval time would make an impossible row indistinguishable from a real one.
        Assert.False(result.IsQuarantined);
        Assert.Empty(result.Observations);
    }

    [Fact]
    public async Task A_row_with_no_date_at_all_produces_nothing()
    {
        const string undated = """
            {
              "name": "Example Corp",
              "filings": {"recent": {
                "accessionNumber": ["0000000000-21-000001"],
                "form": ["8-K"]
              }}
            }
            """;

        var result = await Normalize(undated);

        Assert.False(result.IsQuarantined);
        Assert.Empty(result.Observations);
    }

    // ---------- 5. a stated period after acceptance ----------

    /// <summary>
    /// The Form 25 case: a stated period later than acceptance is an attribute, never the period.
    /// </summary>
    /// <remarks>
    /// This is the rule the whole class exists to get right. Using the later date as
    /// <c>AsOfUtc</c> would raise <c>Claim.PublicationFollowsPeriod</c> and lose the filing;
    /// dropping it would lose the date. It is kept as its own timestamp attribute, and the
    /// observation says why.
    /// </remarks>
    [Fact]
    public async Task A_stated_period_after_acceptance_is_an_attribute_and_never_the_period()
    {
        const string future = """
            {
              "name": "Example Corp",
              "filings": {"recent": {
                "accessionNumber": ["0000892482-22-000045"],
                "filingDate": ["2022-12-27"],
                "reportDate": ["2023-01-09"],
                "acceptanceDateTime": ["2022-12-27T17:02:11.000Z"],
                "form": ["25-NSE"]
              }}
            }
            """;

        var accepted = new DateTime(2022, 12, 27, 17, 2, 11, DateTimeKind.Utc);
        var stated = new DateTime(2023, 1, 9, 0, 0, 0, DateTimeKind.Utc);

        var filing = Filing(await Normalize(future), "0000892482-22-000045");

        Assert.NotEmpty(filing);

        // The period every observation describes is the acceptance instant, never the later date.
        Assert.All(filing, o =>
        {
            Assert.Equal(accepted, o.Provenance.AsOfUtc);
            Assert.Equal(accepted, o.Provenance.PublishedAtUtc);
            Assert.NotEqual(stated, o.Provenance.AsOfUtc);
            Assert.Contains(SecEdgarFilingsNormalizer.StatedPeriodAfterAcceptanceCaveat, o.Caveats);
        });

        // And the date itself survives, as a timestamp attribute rather than as a period.
        var reportDate = filing.Single(o => o.Attribute == SecEdgarFilingsNormalizer.ReportDateAttribute);

        Assert.Equal(ObservationValueKind.Timestamp, reportDate.Value.Kind);
        Assert.Equal(stated, reportDate.Value.AsTimestamp());
    }

    // ---------- 6. no member scope lives here ----------

    /// <summary>
    /// The normaliser applies no form selection, so a member-scoped form list cannot leak into it.
    /// </summary>
    /// <remarks>
    /// The installed authorisation grants one member - LGIQ - a secondary evidence scope of
    /// <c>424B*</c>, <c>S-1</c> and <c>S-3</c>, and grants it to no other member. That scope is a
    /// property of the declaration, applied when filings are interpreted. If this class filtered by
    /// form it would either apply that scope to everyone or hard-code one member's exception into
    /// Infrastructure; it does neither, and reads every form the document states for every company.
    /// </remarks>
    [Fact]
    public async Task Every_form_is_read_for_every_company_and_no_member_is_named()
    {
        const string offerings = """
            {
              "name": "Some Other Company",
              "filings": {"recent": {
                "accessionNumber": ["a-1", "a-2", "a-3", "a-4"],
                "acceptanceDateTime": [
                  "2023-03-01T10:00:00.000Z", "2023-03-02T10:00:00.000Z",
                  "2023-03-03T10:00:00.000Z", "2023-03-04T10:00:00.000Z"
                ],
                "form": ["424B5", "S-1", "S-3", "10-Q"]
              }}
            }
            """;

        var result = await Normalize(offerings);

        var forms = result.Observations
            .Where(o => o.Attribute == SecEdgarFilingsNormalizer.FormAttribute)
            .Select(o => o.Value.Canonical)
            .Order(StringComparer.Ordinal)
            .ToList();

        // The company here is not LGIQ, and it gets the offering forms anyway - because selecting
        // them is not this class's job.
        Assert.Equal("10-Q,424B5,S-1,S-3", string.Join(",", forms));

        // Nothing member-specific is encoded here at all.
        Assert.DoesNotContain(result.Observations, o => o.Attribute.Contains("lgiq", StringComparison.OrdinalIgnoreCase));
    }

    // ---------- 7. nothing is fabricated ----------

    [Fact]
    public async Task A_missing_column_produces_no_attribute()
    {
        const string sparse = """
            {
              "name": "Example Corp",
              "filings": {"recent": {
                "accessionNumber": ["0000000000-24-000001"],
                "acceptanceDateTime": ["2024-05-05T12:00:00.000Z"]
              }}
            }
            """;

        var filing = Filing(await Normalize(sparse), "0000000000-24-000001");

        Assert.Null(ValueOf(filing, SecEdgarFilingsNormalizer.FormAttribute));
        Assert.Null(ValueOf(filing, SecEdgarFilingsNormalizer.PrimaryDocumentAttribute));
        Assert.Null(ValueOf(filing, SecEdgarFilingsNormalizer.DescriptionAttribute));
        Assert.Null(ValueOf(filing, SecEdgarFilingsNormalizer.ReportDateAttribute));
        Assert.Null(ValueOf(filing, SecEdgarFilingsNormalizer.FilingDateAttribute));

        // What it does state is there, and nothing else is.
        Assert.Equal("0000000000-24-000001", ValueOf(filing, SecEdgarFilingsNormalizer.AccessionAttribute));
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("null")]
    [InlineData("20230214")]
    public async Task A_blank_null_or_wrongly_typed_entry_produces_no_attribute(string entry)
    {
        // Built by substitution rather than interpolation: the fixture's own braces close nested
        // JSON objects, and a raw interpolated literal would read them as interpolation holes.
        const string template = """
            {
              "name": "Example Corp",
              "filings": {"recent": {
                "accessionNumber": ["0000000000-24-000002"],
                "acceptanceDateTime": ["2024-05-05T12:00:00.000Z"],
                "filingDate": [ENTRY]
              }}
            }
            """;

        var json = template.Replace("ENTRY", entry, StringComparison.Ordinal);

        var filing = Filing(await Normalize(json), "0000000000-24-000002");

        // Coercing a number into a date would be the normaliser deciding what the source meant.
        Assert.Null(ValueOf(filing, SecEdgarFilingsNormalizer.FilingDateAttribute));
    }

    [Fact]
    public async Task A_row_without_an_accession_number_is_skipped_and_the_others_are_not()
    {
        const string gappy = """
            {
              "name": "Example Corp",
              "filings": {"recent": {
                "accessionNumber": ["", "0000000000-24-000004"],
                "acceptanceDateTime": [
                  "2024-05-05T12:00:00.000Z", "2024-05-06T12:00:00.000Z"
                ],
                "form": ["8-K", "10-Q"]
              }}
            }
            """;

        var result = await Normalize(gappy);

        var forms = result.Observations
            .Where(o => o.Attribute == SecEdgarFilingsNormalizer.FormAttribute)
            .Select(o => o.Value.Canonical)
            .ToList();

        // An unaddressable row is skipped; one bad row does not cost the document.
        Assert.Equal("10-Q", string.Join(",", forms));
    }

    [Fact]
    public async Task A_duplicate_accession_number_is_recorded_as_stated_rather_than_collapsed()
    {
        const string duplicated = """
            {
              "name": "Example Corp",
              "filings": {"recent": {
                "accessionNumber": ["0000000000-24-000005", "0000000000-24-000005"],
                "acceptanceDateTime": [
                  "2024-05-05T12:00:00.000Z", "2024-05-05T12:00:00.000Z"
                ],
                "form": ["8-K", "8-K"]
              }}
            }
            """;

        var result = await Normalize(duplicated);

        var forms = result.Observations
            .Count(o => o.Attribute == SecEdgarFilingsNormalizer.FormAttribute);

        // Both, as the document states them. Choosing which of two rows the SEC meant is not a
        // normalisation decision, and deduplication has its own rules elsewhere.
        Assert.Equal(2, forms);
    }

    [Fact]
    public async Task An_overlong_value_costs_only_its_own_observation()
    {
        var overlong = new string('x', ObservationValue.MaxTextLength + 1);

        const string template = """
            {
              "name": "Example Corp",
              "filings": {"recent": {
                "accessionNumber": ["0000000000-24-000006"],
                "acceptanceDateTime": ["2024-05-05T12:00:00.000Z"],
                "form": ["8-K"],
                "primaryDocDescription": ["OVERLONG"]
              }}
            }
            """;

        var json = template.Replace("OVERLONG", overlong, StringComparison.Ordinal);

        var filing = Filing(await Normalize(json), "0000000000-24-000006");

        Assert.Equal("8-K", ValueOf(filing, SecEdgarFilingsNormalizer.FormAttribute));
        Assert.Null(ValueOf(filing, SecEdgarFilingsNormalizer.DescriptionAttribute));
    }

    [Fact]
    public async Task Unknown_columns_are_ignored_rather_than_absorbed()
    {
        const string extended = """
            {
              "name": "Example Corp",
              "filings": {"recent": {
                "accessionNumber": ["0000000000-24-000007"],
                "acceptanceDateTime": ["2024-05-05T12:00:00.000Z"],
                "form": ["8-K"],
                "somethingTheProviderAddedLastTuesday": ["value"]
              }}
            }
            """;

        var filing = Filing(await Normalize(extended), "0000000000-24-000007");

        // Three attributes: the accession, the acceptance instant and the form - the columns this
        // normaliser reads, and no fourth. A normaliser that absorbed everything would break the
        // day EDGAR added a column.
        Assert.Equal(
            string.Join(",", new[]
            {
                SecEdgarFilingsNormalizer.AcceptedAtAttribute,
                SecEdgarFilingsNormalizer.AccessionAttribute,
                SecEdgarFilingsNormalizer.FormAttribute,
            }.Order(StringComparer.Ordinal)),
            string.Join(",", filing.Select(o => o.Attribute).Order(StringComparer.Ordinal)));
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
        Assert.Equal(SecEdgarFilingsNormalizer.UnreadableRule, result.RuleId);
        Assert.Empty(result.Observations);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("""{"filings": {"recent": {"accessionNumber": ["a-1"]}}}""")]
    public async Task A_document_that_is_not_a_submissions_document_is_quarantined(string json)
    {
        var result = await Normalize(json);

        Assert.True(result.IsQuarantined);
        Assert.Equal(SecEdgarFilingsNormalizer.NotASubmissionsDocumentRule, result.RuleId);
        Assert.Empty(result.Observations);
    }

    [Fact]
    public async Task A_quarantine_reason_never_quotes_the_payload()
    {
        const string secret = """{"password": "hunter2-should-never-be-copied"}""";

        var result = await Normalize(secret);

        Assert.True(result.IsQuarantined);
        Assert.DoesNotContain("hunter2", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Null_input_is_refused() =>
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => Normalizer().NormalizeAsync(null!));
}
