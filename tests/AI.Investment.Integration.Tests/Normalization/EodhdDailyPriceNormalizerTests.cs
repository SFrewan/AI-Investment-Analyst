using System.Text;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Ingestion.Providers;
using AI.Investment.Infrastructure.Normalization;
using AI.Investment.Integration.Tests.Ingestion;
using Microsoft.Extensions.Options;
using Xunit;

namespace AI.Investment.Integration.Tests.Normalization;

/// <summary>
/// Reading EODHD's end-of-day document, and refusing to read it.
/// </summary>
/// <remarks>
/// The interesting cases are the timestamps. EODHD sends a trading date and no times, and the two
/// instants the ledger needs come from the exchange session the operator stated. Everything below
/// is about that substitution being visible, conservative, and refused when nobody stated one.
/// </remarks>
public sealed class EodhdDailyPriceNormalizerTests
{
    private static readonly DateTime Retrieved = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private const string TwoRows =
        """[{"date":"2026-08-26","open":1.0,"high":3.0,"low":0.9,"close":2.5,"adjusted_close":2.4,"volume":10},""" +
        """{"date":"2026-08-27","open":2.5,"high":3.1,"low":2.4,"close":2.75,"adjusted_close":2.6,"volume":11}]""";

    [Fact]
    public void The_normaliser_claims_only_eodhd_market_prices()
    {
        var normalizer = Normalizer();

        Assert.True(normalizer.CanNormalize(EodhdProvider.Id, DataCategory.MarketPrices));
        Assert.False(normalizer.CanNormalize(EodhdProvider.Id, DataCategory.CompanyProfile));
        Assert.False(normalizer.CanNormalize(SecEdgarProvider.Id, DataCategory.MarketPrices));
        Assert.False(normalizer.CanNormalize(PriceHistoryFileProvider.Id, DataCategory.MarketPrices));
    }

    [Fact]
    public async Task Valid_rows_become_closing_price_observations()
    {
        var result = await Normalize(TwoRows);

        Assert.False(result.IsQuarantined);
        Assert.Equal(2, result.Observations.Count);

        foreach (var observation in result.Observations)
        {
            Assert.Equal(DailyClosePriceNormalizer.CloseAttribute, observation.Attribute);
        }

        Assert.Equal(2.5m, result.Observations[0].Value.AsNumber());
        Assert.Equal(2.75m, result.Observations[1].Value.AsNumber());
    }

    /// <summary>
    /// The raw close, not <c>adjusted_close</c>. The adjusted figure is rewritten by every later
    /// split and dividend, so the same row would mean different things on different days.
    /// </summary>
    [Fact]
    public async Task The_unadjusted_close_is_the_one_recorded()
    {
        var result = await Normalize(TwoRows);

        Assert.Equal(2.5m, result.Observations[0].Value.AsNumber());
        Assert.NotEqual(2.4m, result.Observations[0].Value.AsNumber());
    }

    /// <summary>
    /// The whole point of the exchange session: two real instants, in the right order, neither of
    /// them the retrieval time.
    /// </summary>
    [Fact]
    public async Task The_stated_session_supplies_both_provenance_instants()
    {
        var result = await Normalize(TwoRows);

        var provenance = result.Observations[0].Provenance;

        // 2026-08-26, session close 20:00Z, publication delay 4h.
        Assert.Equal(new DateTime(2026, 8, 26, 20, 0, 0, DateTimeKind.Utc), provenance.AsOfUtc);
        Assert.Equal(new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc), provenance.PublishedAtUtc);
        Assert.Equal(Retrieved, provenance.RetrievedAtUtc);
        Assert.NotEqual(provenance.RetrievedAtUtc, provenance.PublishedAtUtc);
        Assert.Equal(EodhdProvider.Id, provenance.SourceId);
        Assert.Equal("AAPL.US", provenance.SourceRecordId);
    }

    /// <summary>A reader of the ledger meets the assumption rather than inferring it.</summary>
    [Fact]
    public async Task Every_observation_carries_the_assumption_as_a_caveat()
    {
        var result = await Normalize(TwoRows);

        foreach (var observation in result.Observations)
        {
            var caveat = Assert.Single(observation.Caveats);

            Assert.Contains("stated facts about exchange", caveat, StringComparison.Ordinal);
            Assert.Contains("raw close", caveat, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// An exchange nobody configured is refused. Guessing a market's trading hours would put a
    /// fabricated instant in the one field a backtest must not be wrong about.
    /// </summary>
    [Fact]
    public async Task An_exchange_with_no_stated_session_quarantines_the_payload()
    {
        var result = await Normalize(TwoRows, symbol: "BP.LSE");

        Assert.True(result.IsQuarantined);
        Assert.Equal(EodhdDailyPriceNormalizer.UnstatedSessionRule, result.RuleId);
    }

    [Fact]
    public async Task A_subject_that_is_not_a_symbol_quarantines_the_payload()
    {
        var result = await Normalize(TwoRows, symbol: "AAPL");

        Assert.True(result.IsQuarantined);
        Assert.Equal(EodhdDailyPriceNormalizer.UnreadableSymbolRule, result.RuleId);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"code":401,"message":"Unauthorized"}""")]
    [InlineData("""{"date":"2026-08-26","close":2.5}""")]
    [InlineData("\"a string\"")]
    public async Task A_document_that_is_not_an_array_of_rows_is_quarantined(string payload)
    {
        var result = await Normalize(payload);

        Assert.True(result.IsQuarantined);
        Assert.Equal(EodhdDailyPriceNormalizer.UnexpectedShapeRule, result.RuleId);
    }

    [Fact]
    public async Task An_empty_array_is_quarantined_rather_than_recorded_as_no_prices()
    {
        var result = await Normalize("[]");

        Assert.True(result.IsQuarantined);
        Assert.Equal(DailyClosePriceNormalizer.EmptySeriesRule, result.RuleId);
    }

    /// <summary>
    /// One bad row refuses the payload. A row that cannot be read is a hole in a time series, and
    /// a series with an invisible hole produces confident, wrong returns.
    /// </summary>
    [Theory]
    [InlineData("""[{"date":"2026-08-26","close":2.5},{"close":2.75}]""")]
    [InlineData("""[{"date":"26/08/2026","close":2.5}]""")]
    [InlineData("""[{"date":"2026-08-26","close":"not a number"}]""")]
    [InlineData("""[{"date":"2026-08-26","close":0}]""")]
    [InlineData("""[{"date":"2026-08-26","close":-1.5}]""")]
    [InlineData("""[{"date":"2026-08-26"}]""")]
    [InlineData("""[{"date":"2026-08-26","close":2.5},"a string"]""")]
    public async Task A_row_that_cannot_be_read_quarantines_the_payload(string payload)
    {
        var result = await Normalize(payload);

        Assert.True(result.IsQuarantined);
        Assert.Equal(DailyClosePriceNormalizer.UnreadableRowRule, result.RuleId);
    }

    /// <summary>A quoted number is a number. A non-numeric string is not.</summary>
    [Fact]
    public async Task A_quoted_close_is_read_as_a_number()
    {
        var result = await Normalize("""[{"date":"2026-08-26","close":"2.50"}]""");

        Assert.False(result.IsQuarantined);
        Assert.Equal(2.50m, Assert.Single(result.Observations).Value.AsNumber());
    }

    /// <summary>
    /// A row whose stated publication has not happened yet is refused. Recording it would claim
    /// the platform read a price before this installation says it became public.
    /// </summary>
    [Fact]
    public async Task A_close_published_after_it_was_fetched_is_quarantined()
    {
        var result = await Normalize("""[{"date":"2026-08-28","close":2.5}]""");

        Assert.True(result.IsQuarantined);
        Assert.Equal(DailyClosePriceNormalizer.ImpossibleOrderingRule, result.RuleId);
    }

    [Fact]
    public async Task A_payload_that_is_not_utf8_is_quarantined()
    {
        var result = await Normalize(new byte[] { 0xFF, 0xFE, 0x00, 0x01 });

        Assert.True(result.IsQuarantined);
        Assert.Equal(DailyClosePriceNormalizer.UnreadablePayloadRule, result.RuleId);
    }

    /// <summary>
    /// A quarantine reason is long-lived and unredactable. A vendor error body can carry the token
    /// that failed, so none of the body is copied into one.
    /// </summary>
    [Fact]
    public async Task A_quarantine_reason_never_quotes_the_payload()
    {
        const string secret = "a-token-that-must-not-be-copied";

        var result = await Normalize($$"""{"error":"bad token {{secret}}"}""");

        Assert.True(result.IsQuarantined);
        Assert.DoesNotContain(secret, result.Reason, StringComparison.Ordinal);
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static EodhdDailyPriceNormalizer Normalizer() =>
        // The normaliser never touches the credential; what it needs from these options is the
        // stated exchange session, and that is what this fixture is about.
        new(Options.Create(EodhdTestOptions.Build()));

    private static Task<NormalizationResult> Normalize(string payload, string symbol = "AAPL.US") =>
        Normalize(Encoding.UTF8.GetBytes(payload), symbol);

    private static Task<NormalizationResult> Normalize(byte[] payload, string symbol = "AAPL.US") =>
        Normalizer().NormalizeAsync(new NormalizationInput(
            EodhdProvider.Id,
            DataCategory.MarketPrices,
            IngestionSubject.Create("Security", symbol),
            ContentHash.Compute(payload),
            payload,
            Retrieved));
}
