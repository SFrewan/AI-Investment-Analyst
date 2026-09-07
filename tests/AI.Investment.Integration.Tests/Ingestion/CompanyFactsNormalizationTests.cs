using System.Text;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Analytics;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Ingestion.Providers;
using AI.Investment.Infrastructure.Normalization;
using AI.Investment.Infrastructure.Validation;
using Xunit;

namespace AI.Investment.Integration.Tests.Ingestion;

/// <summary>
/// EDGAR's XBRL facts, from payload to PostgreSQL to a point-in-time read.
/// </summary>
/// <remarks>
/// <para>
/// The connector could already fetch <c>companyfacts</c> and the calculators could already consume
/// the figures; nothing joined them, so the whole fundamentals path was unreachable. This proves the
/// join end to end against a real database rather than in memory, because the parts that can go
/// wrong are persistence-shaped: an owned value shared between rows, a restatement that overwrites
/// rather than accumulates, and a publication date that is silently replaced by a retrieval date.
/// </para>
/// <para>
/// The fixture is deliberately awkward. It contains an annual figure, a restatement of the same
/// period filed months later, a quarterly figure under the same tag, a balance-sheet figure with no
/// period, and a fact whose filing date precedes the period it describes. Each of those is a shape
/// EDGAR actually produces, and three of the five must not become observations.
/// </para>
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class CompanyFactsNormalizationTests : IAsyncLifetime
{
    private static readonly DateTime PeriodEnd = new(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FirstFiling = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Restatement = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Retrieved = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// One year of one company, with every awkward shape EDGAR actually emits.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Revenues: an annual figure, a restatement of it, and a quarter under the same tag.</item>
    /// <item>Assets: an instantaneous balance with no period at all.</item>
    /// <item>NetIncomeLoss: filed before the period it describes, which cannot have happened.</item>
    /// </list>
    /// </remarks>
    private const string CompanyFacts = """
    {
      "cik": 320193,
      "entityName": "Test Corporation",
      "facts": {
        "us-gaap": {
          "Revenues": {
            "label": "Revenues",
            "units": {
              "USD": [
                { "start": "2025-01-01", "end": "2025-12-31", "val": 1000,
                  "accn": "0000320193-26-000001", "fy": 2025, "fp": "FY",
                  "form": "10-K", "filed": "2026-02-01" },
                { "start": "2025-01-01", "end": "2025-12-31", "val": 1100,
                  "accn": "0000320193-26-000042", "fy": 2025, "fp": "FY",
                  "form": "10-K/A", "filed": "2026-05-01" },
                { "start": "2025-10-01", "end": "2025-12-31", "val": 260,
                  "accn": "0000320193-26-000007", "fy": 2026, "fp": "Q1",
                  "form": "10-Q", "filed": "2026-02-01" }
              ]
            }
          },
          "Assets": {
            "units": {
              "USD": [
                { "end": "2025-12-31", "val": 5000, "accn": "0000320193-26-000001",
                  "fy": 2025, "fp": "FY", "form": "10-K", "filed": "2026-02-01" }
              ]
            }
          },
          "NetIncomeLoss": {
            "units": {
              "USD": [
                { "start": "2025-01-01", "end": "2025-12-31", "val": 250,
                  "accn": "0000320193-24-000009", "fy": 2025, "fp": "FY",
                  "form": "10-K", "filed": "2024-12-01" }
              ]
            }
          }
        }
      }
    }
    """;

    private readonly PostgresFixture _fixture;

    public CompanyFactsNormalizationTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A fresh subject per observation; an owned value belongs to exactly one owner.</summary>
    private static IngestionSubject Company() => IngestionSubject.Create("Company", "0000320193");

    // ---- what the normalizer reads, and what it refuses to read ---------------------------------

    /// <summary>
    /// Annual figures become observations; a quarter under the same tag does not.
    /// </summary>
    /// <remarks>
    /// This is the mistake the duration window exists to prevent. A net margin built from one
    /// quarter's revenue and a full year's net income is wrong by roughly a factor of four and looks
    /// entirely reasonable, because both numbers are real and both came from the filer.
    /// </remarks>
    [Fact]
    public async Task An_annual_figure_is_read_and_a_quarter_under_the_same_tag_is_not()
    {
        var result = await NormalizeAsync();

        Assert.False(result.IsQuarantined);

        var revenue = result.Observations
            .Where(o => o.Attribute == FinancialFigures.Revenue)
            .ToList();

        Assert.Equal(2, revenue.Count);
        Assert.All(revenue, o => Assert.Equal(PeriodEnd, o.Provenance.AsOfUtc));
        Assert.DoesNotContain(revenue, o => o.Value.AsNumber() == 260m);
    }

    /// <summary>A balance sheet figure has no period, and is read as the instant it is.</summary>
    [Fact]
    public async Task A_balance_figure_with_no_period_is_read()
    {
        var result = await NormalizeAsync();

        var assets = Assert.Single(
            result.Observations,
            o => o.Attribute == FinancialFigures.TotalAssets);

        Assert.Equal(5000m, assets.Value.AsNumber());
        Assert.Equal(PeriodEnd, assets.Provenance.AsOfUtc);
    }

    /// <summary>
    /// A fact filed before the period it describes is skipped, and does not poison the document.
    /// </summary>
    /// <remarks>
    /// The claim rules would refuse it - a value cannot be published before the period it describes -
    /// and refusing the whole payload for one bad row would discard every good figure beside it.
    /// </remarks>
    [Fact]
    public async Task A_fact_filed_before_its_own_period_is_skipped_without_losing_the_document()
    {
        var result = await NormalizeAsync();

        Assert.False(result.IsQuarantined);
        Assert.DoesNotContain(result.Observations, o => o.Attribute == FinancialFigures.NetIncome);
        Assert.NotEmpty(result.Observations);
    }

    /// <summary>
    /// The publication date is the filing date, not the retrieval date.
    /// </summary>
    /// <remarks>
    /// This is the whole reason this source is worth having. A backtest filters on
    /// <c>PublishedAtUtc</c>, and a fundamentals backtest that uses the period end instead sees a
    /// company's annual figures on 31 December when the market did not see them until February.
    /// </remarks>
    [Fact]
    public async Task The_publication_date_is_the_filing_date()
    {
        var result = await NormalizeAsync();

        var original = result.Observations
            .Single(o => o.Attribute == FinancialFigures.Revenue && o.Value.AsNumber() == 1000m);

        Assert.Equal(FirstFiling, original.Provenance.PublishedAtUtc);
        Assert.Equal(PeriodEnd, original.Provenance.AsOfUtc);
        Assert.Equal(Retrieved, original.Provenance.RetrievedAtUtc);
        Assert.True(original.Provenance.PublishedAtUtc > original.Provenance.AsOfUtc);
    }

    /// <summary>Each figure says which XBRL tag and which filing it came from.</summary>
    [Fact]
    public async Task Each_figure_cites_the_tag_and_the_filing_it_came_from()
    {
        var result = await NormalizeAsync();

        var restated = result.Observations
            .Single(o => o.Attribute == FinancialFigures.Revenue && o.Value.AsNumber() == 1100m);

        Assert.Equal("0000320193-26-000042", restated.Provenance.SourceRecordId);
        Assert.Contains(restated.Caveats, c => c.Contains("Revenues", StringComparison.Ordinal));
        Assert.Contains(restated.Caveats, c => c.Contains("10-K/A", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_submissions_document_is_quarantined_rather_than_read_as_facts()
    {
        var result = await NormalizeAsync("""{ "name": "Test Corporation", "tickers": ["TEST"] }""");

        Assert.True(result.IsQuarantined);
        Assert.Equal(SecEdgarCompanyFactsNormalizer.NotACompanyFactsDocumentRule, result.RuleId);
    }

    [Fact]
    public async Task An_unreadable_payload_is_quarantined()
    {
        var result = await NormalizeAsync("{ not json");

        Assert.True(result.IsQuarantined);
        Assert.Equal(SecEdgarCompanyFactsNormalizer.UnreadableRule, result.RuleId);
    }

    [Fact]
    public void The_normalizer_claims_only_the_financial_statements_of_its_own_source()
    {
        var normalizer = new SecEdgarCompanyFactsNormalizer();

        Assert.True(normalizer.CanNormalize(SecEdgarProvider.Id, DataCategory.FinancialStatements));
        Assert.False(normalizer.CanNormalize(SecEdgarProvider.Id, DataCategory.CompanyProfile));
        Assert.False(normalizer.CanNormalize(
            SourceId.Create("eodhd-eod"), DataCategory.FinancialStatements));
    }

    // ---- and the same thing through PostgreSQL --------------------------------------------------

    /// <summary>
    /// <strong>The restatement.</strong> Both versions are stored, and the read picks the one that
    /// was in force at the instant asked about.
    /// </summary>
    /// <remarks>
    /// A store that overwrote the original on restatement would answer every question with the
    /// corrected figure, including questions about dates before the correction existed. That is
    /// look-ahead that no strategy could detect and no test of the strategy would catch, because the
    /// number returned is real - it is simply from the future.
    /// </remarks>
    [SkippableFact]
    public async Task A_restated_figure_reads_back_as_the_version_in_force_at_the_time()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await PersistAsync();

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        var history = new EfValidationHistory(context);

        var beforeCorrection = await history.GetPriceSeriesAsync(
            Company(),
            FinancialFigures.Revenue,
            PeriodEnd.AddYears(-1),
            PeriodEnd.AddDays(1),
            KnowledgeCutoff.At(Restatement.AddDays(-1)));

        var afterCorrection = await history.GetPriceSeriesAsync(
            Company(),
            FinancialFigures.Revenue,
            PeriodEnd.AddYears(-1),
            PeriodEnd.AddDays(1),
            KnowledgeCutoff.At(Restatement.AddDays(1)));

        Assert.Equal(1000m, Assert.Single(beforeCorrection).Price);
        Assert.Equal(1100m, Assert.Single(afterCorrection).Price);
    }

    /// <summary>
    /// Nothing is visible before it was filed, which is what makes a fundamentals backtest honest.
    /// </summary>
    [SkippableFact]
    public async Task Nothing_is_visible_before_the_day_it_was_filed()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await PersistAsync();

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        var onTheDayThePeriodEnded = await new EfValidationHistory(context).GetPriceSeriesAsync(
            Company(),
            FinancialFigures.Revenue,
            PeriodEnd.AddYears(-1),
            PeriodEnd.AddDays(1),
            KnowledgeCutoff.At(PeriodEnd));

        Assert.Empty(onTheDayThePeriodEnded);
    }

    /// <summary>
    /// Every row keeps its own subject and provenance, which an owned value shared between rows does
    /// not.
    /// </summary>
    /// <remarks>
    /// This class of defect has shipped four times in this codebase. It is invisible in memory and
    /// arrives from the database as a not-null violation on a column nobody was thinking about, so
    /// it is asserted here against the real provider rather than reasoned about.
    /// </remarks>
    [SkippableFact]
    public async Task Every_stored_figure_keeps_its_own_subject_and_provenance()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var written = await PersistAsync();

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        var stored = await new EfValidationHistory(context).GetAdmissibleAsync(
            Company(),
            FinancialFigures.Revenue,
            KnowledgeCutoff.At(Retrieved));

        Assert.Equal(2, stored.Count);
        Assert.All(stored, o => Assert.Equal("0000320193", o.Subject.Identifier));
        Assert.All(stored, o => Assert.Equal(SecEdgarProvider.Id, o.Provenance.SourceId));
        Assert.Equal(2, stored.Select(o => o.Provenance.SourceRecordId).Distinct().Count());
        Assert.True(written >= stored.Count);
    }

    // ---- fixtures -------------------------------------------------------------------------------

    private static async Task<NormalizationResult> NormalizeAsync(string? payload = null)
    {
        var bytes = Encoding.UTF8.GetBytes(payload ?? CompanyFacts);

        return await new SecEdgarCompanyFactsNormalizer().NormalizeAsync(
            new NormalizationInput(
                SecEdgarProvider.Id,
                DataCategory.FinancialStatements,
                Company(),
                ContentHash.Compute(bytes),
                bytes,
                Retrieved));
    }

    /// <summary>Writes everything the normalizer produced through the seam.</summary>
    private async Task<int> PersistAsync()
    {
        var result = await NormalizeAsync();
        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Retrieved)))
        {
            await context.Observations.AddRangeAsync(result.Observations);
            await context.SaveChangesAsync();
        }

        return result.Observations.Count;
    }
}
