using System.Text;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Observations;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Ingestion.Providers;
using AI.Investment.Infrastructure.Normalization;
using Xunit;

namespace AI.Investment.Integration.Tests.Normalization;

/// <summary>
/// Reading an exported price history into closing-price observations, and refusing everything else.
/// </summary>
/// <remarks>
/// <para>
/// The provenance assertions are the ones that matter most. Every point-in-time judgement the
/// platform makes reads <c>AsOfUtc</c> and <c>PublishedAtUtc</c>, and a normaliser that put the
/// retrieval time in either would produce a series that backtests beautifully and means nothing.
/// </para>
/// <para>
/// The refusals are the second half. This normaliser quarantines the whole payload on a single bad
/// row, which is the opposite of the company-profile normaliser's choice and deliberately so: a
/// missing attribute costs one fact, and a missing row is an invisible hole in a time series.
/// </para>
/// </remarks>
public sealed class DailyClosePriceNormalizerTests
{
    private static readonly DateTime Retrieved = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private static IngestionSubject Apple() => IngestionSubject.Create("Security", "AAPL");

    private const string TwoSessions = """
        session_close_utc,close,published_at_utc
        2026-01-02T21:00:00Z,185.64,2026-01-02T21:15:00Z
        2026-01-05T21:00:00Z,187.15,2026-01-05T21:15:00Z
        """;

    // ---- what it reads -------------------------------------------------------------------------

    [Fact]
    public async Task Each_row_becomes_one_canonical_closing_price()
    {
        var result = await Normalize(TwoSessions);

        Assert.False(result.IsQuarantined);
        Assert.Equal(2, result.Observations.Count);

        Assert.All(result.Observations, observation =>
        {
            Assert.Equal("security.close", observation.Attribute);
            Assert.Equal(ObservationValueKind.Number, observation.Value.Kind);
            Assert.Equal("Security", observation.Subject.Kind);
            Assert.Equal("AAPL", observation.Subject.Identifier);
            Assert.Contains(DailyClosePriceNormalizer.SourceCaveat, observation.Caveats);
        });

        Assert.Equal(185.64m, result.Observations[0].Value.AsNumber());
        Assert.Equal(187.15m, result.Observations[1].Value.AsNumber());
    }

    /// <summary>
    /// The three provenance timestamps mean three different things, and each comes from where it
    /// should.
    /// </summary>
    [Fact]
    public async Task Provenance_separates_the_session_the_publication_and_the_read()
    {
        var provenance = (await Normalize(TwoSessions)).Observations[0].Provenance;

        Assert.Equal(new DateTime(2026, 1, 2, 21, 0, 0, DateTimeKind.Utc), provenance.AsOfUtc);
        Assert.Equal(new DateTime(2026, 1, 2, 21, 15, 0, DateTimeKind.Utc), provenance.PublishedAtUtc);
        Assert.Equal(Retrieved, provenance.RetrievedAtUtc);
        Assert.Equal(PriceHistoryFileProvider.Id, provenance.SourceId);
        Assert.Equal("AAPL", provenance.SourceRecordId);
    }

    /// <summary>
    /// A correction is a second observation of the same session, published later. Both are recorded;
    /// resolving between them is the reader's job and is what makes a restatement visible.
    /// </summary>
    [Fact]
    public async Task A_restated_session_is_recorded_rather_than_replacing_the_original()
    {
        var result = await Normalize("""
            session_close_utc,close,published_at_utc
            2026-01-02T21:00:00Z,185.64,2026-01-02T21:15:00Z
            2026-01-02T21:00:00Z,185.70,2026-01-03T09:00:00Z
            """);

        Assert.Equal(2, result.Observations.Count);
        Assert.Equal(
            result.Observations[0].Provenance.AsOfUtc,
            result.Observations[1].Provenance.AsOfUtc);
        Assert.True(
            result.Observations[1].Provenance.PublishedAtUtc >
            result.Observations[0].Provenance.PublishedAtUtc);
    }

    [Fact]
    public async Task Blank_lines_and_a_trailing_newline_are_not_rows()
    {
        var result = await Normalize(TwoSessions + "\n\n");

        Assert.Equal(2, result.Observations.Count);
    }

    [Fact]
    public async Task Windows_line_endings_read_the_same_as_unix_ones()
    {
        var result = await Normalize(TwoSessions.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Equal(2, result.Observations.Count);
        Assert.Equal(185.64m, result.Observations[0].Value.AsNumber());
    }

    // ---- what it refuses -----------------------------------------------------------------------

    [Fact]
    public async Task A_document_that_is_not_this_export_is_quarantined()
    {
        var result = await Normalize("date,price\n2026-01-02,185.64");

        Assert.True(result.IsQuarantined);
        Assert.Equal(DailyClosePriceNormalizer.UnexpectedColumnsRule, result.RuleId);
        Assert.Empty(result.Observations);
    }

    [Fact]
    public async Task A_header_with_no_rows_under_it_is_quarantined()
    {
        var result = await Normalize(DailyClosePriceNormalizer.Header);

        Assert.True(result.IsQuarantined);
        Assert.Equal(DailyClosePriceNormalizer.EmptySeriesRule, result.RuleId);
    }

    [Theory]
    [InlineData("2026-01-02T21:00:00Z,185.64")]
    [InlineData("2026-01-02,185.64,2026-01-02T21:15:00Z")]
    [InlineData("2026-01-02T21:00:00,185.64,2026-01-02T21:15:00Z")]
    [InlineData("2026-01-02T21:00:00Z,not-a-number,2026-01-02T21:15:00Z")]
    [InlineData("2026-01-02T21:00:00Z,0,2026-01-02T21:15:00Z")]
    [InlineData("2026-01-02T21:00:00Z,-5,2026-01-02T21:15:00Z")]
    [InlineData("2026-01-02T21:00:00Z,185.64,tuesday")]
    public async Task A_row_that_cannot_be_read_quarantines_the_payload(string row)
    {
        var result = await Normalize(DailyClosePriceNormalizer.Header + "\n" + row);

        Assert.True(result.IsQuarantined);
        Assert.Equal(DailyClosePriceNormalizer.UnreadableRowRule, result.RuleId);
        Assert.Empty(result.Observations);
    }

    /// <summary>
    /// One bad row costs the whole payload, and the good rows beside it produce nothing. A hole in
    /// a price series is worse than a refusal an operator can see.
    /// </summary>
    [Fact]
    public async Task One_unreadable_row_costs_the_whole_series()
    {
        var result = await Normalize("""
            session_close_utc,close,published_at_utc
            2026-01-02T21:00:00Z,185.64,2026-01-02T21:15:00Z
            2026-01-05T21:00:00Z,,2026-01-05T21:15:00Z
            2026-01-06T21:00:00Z,190.00,2026-01-06T21:15:00Z
            """);

        Assert.True(result.IsQuarantined);
        Assert.Empty(result.Observations);
        Assert.Contains("Line 3", result.Reason!, StringComparison.Ordinal);
    }

    /// <summary>A price cannot have been published before the session it describes ended.</summary>
    [Fact]
    public async Task A_close_published_before_its_own_session_is_quarantined()
    {
        var result = await Normalize("""
            session_close_utc,close,published_at_utc
            2026-01-02T21:00:00Z,185.64,2026-01-02T09:00:00Z
            """);

        Assert.True(result.IsQuarantined);
        Assert.Equal(DailyClosePriceNormalizer.ImpossibleOrderingRule, result.RuleId);
    }

    /// <summary>And it cannot have been read before it was published.</summary>
    [Fact]
    public async Task A_close_published_after_the_file_was_read_is_quarantined()
    {
        var result = await Normalize("""
            session_close_utc,close,published_at_utc
            2027-01-02T21:00:00Z,185.64,2027-01-02T21:15:00Z
            """);

        Assert.True(result.IsQuarantined);
        Assert.Equal(DailyClosePriceNormalizer.ImpossibleOrderingRule, result.RuleId);
    }

    [Fact]
    public async Task A_payload_that_is_not_text_is_quarantined()
    {
        var payload = new byte[] { 0xFF, 0xFE, 0xFD, 0xFC };

        var result = await new DailyClosePriceNormalizer().NormalizeAsync(
            new NormalizationInput(
                PriceHistoryFileProvider.Id,
                DataCategory.MarketPrices,
                Apple(),
                ContentHash.Compute(payload),
                payload,
                Retrieved));

        Assert.True(result.IsQuarantined);
        Assert.Equal(DailyClosePriceNormalizer.UnreadablePayloadRule, result.RuleId);
    }

    /// <summary>
    /// The quarantine reason names the line and the problem, and never the row itself. A quarantine
    /// record is long-lived and unredactable, and licensed prices are what should not be in one.
    /// </summary>
    [Fact]
    public async Task A_quarantine_reason_never_repeats_the_data()
    {
        var result = await Normalize("""
            session_close_utc,close,published_at_utc
            2026-01-02T21:00:00Z,999.99,tuesday
            """);

        Assert.DoesNotContain("999.99", result.Reason!, StringComparison.Ordinal);
        Assert.Contains("Line 2", result.Reason!, StringComparison.Ordinal);
    }

    // ---- what it answers for -------------------------------------------------------------------

    [Fact]
    public void It_reads_market_prices_from_the_price_history_connector_and_nothing_else()
    {
        var normalizer = new DailyClosePriceNormalizer();

        Assert.True(normalizer.CanNormalize(PriceHistoryFileProvider.Id, DataCategory.MarketPrices));
        Assert.False(normalizer.CanNormalize(PriceHistoryFileProvider.Id, DataCategory.CompanyProfile));
        Assert.False(normalizer.CanNormalize(SecEdgarProvider.Id, DataCategory.MarketPrices));
    }

    /// <summary>
    /// The attribute written here is the attribute the validation run reads. They are the same
    /// string, and a mismatch would produce an empty report rather than an error.
    /// </summary>
    [Fact]
    public void The_attribute_written_is_the_one_the_validation_run_reads() =>
        Assert.Equal("security.close", DailyClosePriceNormalizer.CloseAttribute);

    // ---- helpers -------------------------------------------------------------------------------

    private static async Task<NormalizationResult> Normalize(string csv)
    {
        var payload = Encoding.UTF8.GetBytes(csv);

        return await new DailyClosePriceNormalizer().NormalizeAsync(
            new NormalizationInput(
                PriceHistoryFileProvider.Id,
                DataCategory.MarketPrices,
                Apple(),
                ContentHash.Compute(payload),
                payload,
                Retrieved));
    }
}
