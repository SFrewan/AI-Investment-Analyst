using System.Globalization;
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
    /// A row that cannot be read refuses the payload. A row that cannot be read is a hole in a time
    /// series, and a series with an invisible hole produces confident, wrong returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Two cases were taken out of this theory when row-level refusal was introduced, and
    /// that is recorded here rather than done quietly.</strong> A single-row payload whose only
    /// close is <c>0</c> or <c>-1.5</c> used to land here under
    /// <c>market-data.unreadable-row@1</c>. It now lands under
    /// <c>market-data.empty-series@1</c> instead, because the row is dropped and a document from
    /// which nothing could be read is an empty series - which is the more precise answer and the one
    /// <see cref="Every_row_refused_still_quarantines_the_payload_as_an_empty_series"/> now pins.
    /// </para>
    /// <para>
    /// Nothing was relaxed by the move: both payloads are still quarantined, and no observation is
    /// produced from either. What changed is which rule names the refusal, and it changed because a
    /// non-positive close is now refused as a row rather than as a document.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("""[{"date":"2026-08-26","close":2.5},{"close":2.75}]""")]
    [InlineData("""[{"date":"26/08/2026","close":2.5}]""")]
    [InlineData("""[{"date":"2026-08-26","close":"not a number"}]""")]
    [InlineData("""[{"date":"2026-08-26"}]""")]
    [InlineData("""[{"date":"2026-08-26","close":2.5},"a string"]""")]
    public async Task A_row_that_cannot_be_read_quarantines_the_payload(string payload)
    {
        var result = await Normalize(payload);

        Assert.True(result.IsQuarantined);
        Assert.Equal(DailyClosePriceNormalizer.UnreadableRowRule, result.RuleId);
    }

    // ================= row-level refusal of a non-positive close =================

    /// <summary>
    /// A trailing zero close is dropped and the sound rows before it are read.
    /// </summary>
    /// <remarks>
    /// The shape that mattered: ten of the eleven archived payloads that this behaviour was written
    /// for carry their bad rows at the end, where a vendor kept emitting rows after an instrument
    /// stopped trading. Refusing the document for them discarded hundreds of sound sessions.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1.5)]
    public async Task A_trailing_non_positive_close_is_dropped_and_the_sound_rows_survive(double bad)
    {
        var result = await Normalize(Rows(("2026-08-25", 2.5), ("2026-08-26", 2.75), ("2026-08-27", bad)));

        Assert.False(result.IsQuarantined);
        Assert.Null(result.RuleId);
        Assert.Equal(2, result.Observations.Count);
        Assert.True(result.HadRejectedRows);
        Assert.Equal(1, result.RejectedRows);
        Assert.Equal(DailyClosePriceNormalizer.UnreadableRowRule, result.RejectedRowRuleId);
    }

    /// <summary>
    /// An interior zero close is dropped, and the hole it leaves is a hole - not a filled value.
    /// </summary>
    /// <remarks>
    /// The coverage rule measures gaps from the sessions an observation is held for, so a dropped
    /// interior row simply is not there. Nothing is interpolated, and no zero is recorded: this
    /// test asserts both the count and the absence of the middle session.
    /// </remarks>
    [Fact]
    public async Task An_interior_non_positive_close_leaves_a_hole_rather_than_a_value()
    {
        var result = await Normalize(Rows(("2026-08-25", 2.5), ("2026-08-26", 0), ("2026-08-27", 2.75)));

        Assert.False(result.IsQuarantined);
        Assert.Equal(2, result.Observations.Count);
        Assert.Equal(1, result.RejectedRows);

        var days = result.Observations.Select(o => o.Provenance.AsOfUtc.Date).ToList();

        Assert.Contains(new DateTime(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc), days);
        Assert.Contains(new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc), days);
        Assert.DoesNotContain(new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc), days);
    }

    [Fact]
    public async Task Several_scattered_non_positive_closes_are_each_dropped()
    {
        var result = await Normalize(Rows(
            ("2026-08-21", 0), ("2026-08-24", 2.5), ("2026-08-25", -0.01),
            ("2026-08-26", 2.75), ("2026-08-27", 0)));

        Assert.False(result.IsQuarantined);
        Assert.Equal(2, result.Observations.Count);
        Assert.Equal(3, result.RejectedRows);
    }

    /// <summary>
    /// A payload whose every row is refused is still quarantined, as an empty series.
    /// </summary>
    /// <remarks>
    /// The boundary that must not move. Row-level refusal exists so a sound document is not thrown
    /// away for one bad row - not so a document with nothing usable in it can be reported as a
    /// successful read of nothing. An instrument with no history and a document the platform could
    /// not use are different problems, and this is what keeps them apart.
    /// </remarks>
    [Theory]
    [InlineData("""[{"date":"2026-08-26","close":0}]""")]
    [InlineData("""[{"date":"2026-08-26","close":-1.5}]""")]
    [InlineData("""[{"date":"2026-08-25","close":0},{"date":"2026-08-26","close":0}]""")]
    public async Task Every_row_refused_still_quarantines_the_payload_as_an_empty_series(string payload)
    {
        var result = await Normalize(payload);

        Assert.True(result.IsQuarantined);
        Assert.Equal(DailyClosePriceNormalizer.EmptySeriesRule, result.RuleId);
        Assert.Empty(result.Observations);
    }

    /// <summary>A quoted zero is a zero. Reading it as a price would be the whole defect.</summary>
    [Fact]
    public async Task A_quoted_non_positive_close_is_refused_as_a_row()
    {
        var result = await Normalize(
            """[{"date":"2026-08-26","close":"2.50"},{"date":"2026-08-27","close":"0"}]""");

        Assert.False(result.IsQuarantined);
        Assert.Equal(1, result.RejectedRows);
        Assert.Equal(2.50m, Assert.Single(result.Observations).Value.AsNumber());
    }

    /// <summary>
    /// No admitted observation ever carries a non-positive close.
    /// </summary>
    /// <remarks>
    /// The precondition the rest of the platform depends on. <c>SplitAdjustment</c>'s continuity
    /// walk skips any pair containing a non-positive close, so a zero admitted here would stop it
    /// checking on both sides of that row - a blind spot in the one rule the coverage gate counts
    /// refusals from. <c>PriceRecoveryRule.IsWellFormed</c> refuses such a point outright. Neither
    /// may be changed, and neither has to be, because a zero never becomes an observation.
    /// </remarks>
    [Fact]
    public async Task No_admitted_observation_carries_a_non_positive_close()
    {
        var result = await Normalize(Rows(
            ("2026-08-21", 1.0), ("2026-08-24", 0), ("2026-08-25", -3),
            ("2026-08-26", 2.75), ("2026-08-27", 0)));

        Assert.NotEmpty(result.Observations);
        Assert.All(result.Observations, o => Assert.True(o.Value.AsNumber() > 0m));
    }

    /// <summary>
    /// A dropped row changes nothing about the rows that survive it.
    /// </summary>
    [Fact]
    public async Task A_dropped_row_does_not_disturb_the_provenance_of_its_neighbours()
    {
        var clean = await Normalize(Rows(("2026-08-26", 2.5), ("2026-08-27", 2.75)));
        var mixed = await Normalize(Rows(("2026-08-26", 2.5), ("2026-08-27", 2.75), ("2026-08-21", 0)));

        Assert.Equal(clean.Observations.Count, mixed.Observations.Count);

        for (var i = 0; i < clean.Observations.Count; i++)
        {
            Assert.Equal(clean.Observations[i].Value.AsNumber(), mixed.Observations[i].Value.AsNumber());
            Assert.Equal(clean.Observations[i].Provenance.AsOfUtc, mixed.Observations[i].Provenance.AsOfUtc);
            Assert.Equal(
                clean.Observations[i].Provenance.PublishedAtUtc,
                mixed.Observations[i].Provenance.PublishedAtUtc);
            Assert.Equal(
                clean.Observations[i].Provenance.RetrievedAtUtc,
                mixed.Observations[i].Provenance.RetrievedAtUtc);
        }
    }

    /// <summary>
    /// The same bytes read twice produce the same observations, and the same refusals.
    /// </summary>
    /// <remarks>
    /// Row-level refusal must not introduce a clock or any other input the archived bytes do not
    /// carry. Every instant on an observation comes from the row's own date plus the archive's
    /// retrieval time, and the decision to drop a row is a function of that row's close alone.
    /// </remarks>
    [Fact]
    public async Task Reading_the_same_bytes_twice_is_deterministic()
    {
        const string payload = """[{"date":"2026-08-25","close":2.5},{"date":"2026-08-26","close":0},{"date":"2026-08-27","close":2.75}]""";

        var first = await Normalize(payload);
        var second = await Normalize(payload);

        Assert.Equal(first.RejectedRows, second.RejectedRows);
        Assert.Equal(first.Observations.Count, second.Observations.Count);
        Assert.Equal(first.RejectedRowReason, second.RejectedRowReason);
        Assert.Equal(
            ContentHash.Compute(Encoding.UTF8.GetBytes(payload)),
            ContentHash.Compute(Encoding.UTF8.GetBytes(payload)));

        for (var i = 0; i < first.Observations.Count; i++)
        {
            Assert.Equal(first.Observations[i].Value.Canonical, second.Observations[i].Value.Canonical);
            Assert.Equal(first.Observations[i].Provenance.AsOfUtc, second.Observations[i].Provenance.AsOfUtc);
            Assert.Equal(
                first.Observations[i].Provenance.PublishedAtUtc,
                second.Observations[i].Provenance.PublishedAtUtc);
        }
    }

    /// <summary>The reason names the rows, so an operator need not re-read the payload.</summary>
    [Fact]
    public async Task The_rejection_reason_names_the_rows_it_dropped()
    {
        var result = await Normalize(Rows(("2026-08-25", 2.5), ("2026-08-26", 0), ("2026-08-27", 2.75)));

        Assert.NotNull(result.RejectedRowReason);
        Assert.Contains("row(s) 2", result.RejectedRowReason, StringComparison.Ordinal);
        Assert.Contains("zero or less", result.RejectedRowReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every other refusal still takes the whole payload, even when sound rows sit beside it.
    /// </summary>
    /// <remarks>
    /// Before row-level refusal existed this could not be observed: the first bad row ended the
    /// read whatever it was. Now that one case continues past a bad row, the others have to be
    /// shown still not to.
    /// </remarks>
    [Theory]
    [InlineData("""[{"date":"2026-08-26","close":2.5},{"date":"2026-08-27"}]""", "market-data.unreadable-row@1")]
    [InlineData("""[{"date":"2026-08-26","close":2.5},{"date":"2026-08-27","close":"nope"}]""", "market-data.unreadable-row@1")]
    [InlineData("""[{"date":"2026-08-26","close":2.5},{"date":"27/08/2026","close":2.75}]""", "market-data.unreadable-row@1")]
    [InlineData("""[{"date":"2026-08-26","close":2.5},"a string"]""", "market-data.unreadable-row@1")]
    [InlineData("""[{"date":"2026-08-26","close":2.5},{"date":"2026-08-28","close":2.75}]""", "market-data.impossible-ordering@1")]
    public async Task A_sound_row_does_not_rescue_a_payload_refused_for_any_other_reason(
        string payload,
        string expectedRule)
    {
        var result = await Normalize(payload);

        Assert.True(result.IsQuarantined);
        Assert.Equal(expectedRule, result.RuleId);
        Assert.Empty(result.Observations);
        Assert.Equal(0, result.RejectedRows);
    }

    /// <summary>A payload with no bad rows is a plain read, carrying no rejection at all.</summary>
    [Fact]
    public async Task A_payload_with_no_bad_rows_reports_no_rejected_rows()
    {
        var result = await Normalize(TwoRows);

        Assert.False(result.IsQuarantined);
        Assert.False(result.HadRejectedRows);
        Assert.Equal(0, result.RejectedRows);
        Assert.Null(result.RejectedRowRuleId);
        Assert.Null(result.RejectedRowReason);
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

    /// <summary>An end-of-day document with the given dates and closes, in the order given.</summary>
    private static string Rows(params (string Date, double Close)[] rows) =>
        "[" + string.Join(
            ",",
            rows.Select(r => string.Create(
                CultureInfo.InvariantCulture,
                $$"""{"date":"{{r.Date}}","close":{{r.Close}}}"""))) + "]";

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
