using System.Reflection;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The boundary between "which work is this" and "which attempt at it is this".
/// </summary>
/// <remarks>
/// <para>
/// <strong>No provider, no store, no network.</strong> Everything here is a string, a fingerprint
/// or a file already on disk. Nothing is dispatched, nothing is written, and no connector is
/// consulted.
/// </para>
/// <para>
/// <strong>What these protect.</strong> Two identities travel with every acquisition request. The
/// fingerprint says which work it is and is what the ledger suppresses on; the correlation says
/// which attempt it is and is what the Action/Policy seam keys idempotency on. Collapsing the
/// second into the first is what stranded <c>NXST.US</c> and <c>SIRI.US</c>: their correlations were
/// functions of the ticker alone, so when a DNS failure burned the seam's claim there was no way to
/// express "the same work, a second time". These tests hold the two apart in both directions - the
/// correlation must vary per attempt, and the fingerprint must not.
/// </para>
/// </remarks>
public sealed class AcquisitionCorrelationTests
{
    private const string PriceSource = "eodhd-eod";
    private const string ActionsSource = "eodhd-splits";

    private const string SealedFingerprint =
        "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private static readonly DateTime WindowStart = new(2021, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime RequestedAt = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    // ---- attempt identity ---------------------------------------------------------------------

    /// <summary>
    /// The identifier is a function of the batch, the attempt and the symbol - and of nothing else.
    /// </summary>
    /// <remarks>
    /// Deterministic rather than random on purpose. A GUID would be unique and untraceable: an
    /// operator reading an audit row could not say which approved run produced it, and a process
    /// that crashed before writing its artefact would look like a fresh attempt rather than the same
    /// one resumed. Built from two numbers that are already written down, it can be read backwards
    /// to the approval that authorised it.
    /// </remarks>
    [Fact]
    public void The_same_attempt_always_produces_the_same_identifier()
    {
        var once = AcquisitionCorrelation.Text(4, 2, "NXST", DataCategory.MarketPrices);
        var again = AcquisitionCorrelation.Text(4, 2, "NXST", DataCategory.MarketPrices);

        Assert.Equal(once, again);
        Assert.Equal(once, AcquisitionCorrelation.For(4, 2, "NXST", DataCategory.MarketPrices).ToString());
    }

    /// <summary>Two attempts at one symbol in one batch are two acts, not one.</summary>
    [Fact]
    public void A_later_attempt_at_the_same_symbol_is_a_different_identifier()
    {
        var first = AcquisitionCorrelation.Text(2, 1, "NXST", DataCategory.MarketPrices);
        var second = AcquisitionCorrelation.Text(2, 2, "NXST", DataCategory.MarketPrices);

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// Neither number alone is enough, so both are present.
    /// </summary>
    /// <remarks>
    /// The batch index alone is what the runner used to have, in effect, and made every attempt at a
    /// symbol the same act. The attempt number alone would collide across batches: <c>NXST.US</c>
    /// falls inside batch 2's range and inside the completion batch's, so both would call their
    /// first try "attempt 1".
    /// </remarks>
    [Fact]
    public void The_same_symbol_in_two_batches_never_collides()
    {
        var inBatchTwo = AcquisitionCorrelation.Text(2, 1, "NXST", DataCategory.MarketPrices);
        var inBatchFour = AcquisitionCorrelation.Text(4, 1, "NXST", DataCategory.MarketPrices);

        Assert.NotEqual(inBatchTwo, inBatchFour);
    }

    /// <summary>Two symbols are never the same attempt, however the numbers line up.</summary>
    [Fact]
    public void Two_symbols_never_collide()
    {
        var nexstar = AcquisitionCorrelation.Text(2, 1, "NXST", DataCategory.MarketPrices);
        var sirius = AcquisitionCorrelation.Text(2, 1, "SIRI", DataCategory.MarketPrices);

        Assert.NotEqual(nexstar, sirius);
    }

    /// <summary>
    /// Every distinct attempt in a realistic span is distinct, checked exhaustively.
    /// </summary>
    /// <remarks>
    /// The four individual cases above each rule out one collision. This rules out all of them at
    /// once over the space the runner actually uses, which is the claim that matters: no two
    /// (batch, attempt, symbol) triples share an identifier.
    /// </remarks>
    [Fact]
    public void No_two_distinct_attempts_share_an_identifier()
    {
        string[] tickers = ["NXST", "SIRI", "CMTL", "ZYME", "NXS", "T", "NXSTA"];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var built = 0;

        for (var batch = 1; batch <= 6; batch++)
        {
            for (var attempt = 1; attempt <= 6; attempt++)
            {
                foreach (var ticker in tickers)
                {
                    built++;

                    Assert.True(
                        seen.Add(AcquisitionCorrelation.Text(batch, attempt, ticker, DataCategory.MarketPrices)),
                        Universe.Inv($"batch {batch}, attempt {attempt}, `{ticker}` collided with an earlier attempt"));
                }
            }
        }

        Assert.True(seen.Count == built, Universe.Inv($"{built} attempts produced {seen.Count} identifiers"));
    }

    /// <summary>
    /// The identifier the stranded symbols carry can never be produced again.
    /// </summary>
    /// <remarks>
    /// <c>NXST.US</c> and <c>SIRI.US</c> hold live seam claims under <c>batch-NXST-MarketPrices</c>
    /// and <c>batch-SIRI-MarketPrices</c>, and those claims are permanent: the seam does not release
    /// a key when an effect fails. If any attempt could reconstruct one of those strings it would be
    /// refused as a duplicate exactly as before, so this asserts the shape has moved away from them
    /// rather than merely happening not to match today.
    /// </remarks>
    [Theory]
    [InlineData("NXST")]
    [InlineData("SIRI")]
    public void The_old_symbol_only_identifier_is_unreachable(string ticker)
    {
        var stranded = Universe.Inv($"batch-{ticker}-MarketPrices");

        for (var batch = 1; batch <= 8; batch++)
        {
            for (var attempt = 1; attempt <= 8; attempt++)
            {
                Assert.NotEqual(
                    stranded,
                    AcquisitionCorrelation.Text(batch, attempt, ticker, DataCategory.MarketPrices));
            }
        }
    }

    /// <summary>
    /// Anything a ticker might contain still yields an identifier the domain accepts.
    /// </summary>
    /// <remarks>
    /// A correlation identifier admits ASCII letters, digits, hyphen and underscore. A malformed one
    /// throws while the request is being built - outside the runner's try - and would end a batch
    /// before it recorded what it had already spent, which is the one outcome worse than a failed
    /// request.
    /// </remarks>
    [Theory]
    [InlineData("NXST")]
    [InlineData("BRK-B")]
    [InlineData("BRK.B")]
    [InlineData("A B")]
    [InlineData("7203")]
    [InlineData("../etc")]
    [InlineData("")]
    public void Any_ticker_yields_an_identifier_the_domain_accepts(string ticker)
    {
        // Create throws on a malformed identifier, so reaching the assertions is half the test.
        var correlation = AcquisitionCorrelation.For(3, 1, ticker, DataCategory.MarketPrices).ToString();

        Assert.NotEmpty(correlation);
        Assert.DoesNotContain(".", correlation, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", correlation, StringComparison.Ordinal);
        Assert.DoesNotContain("/", correlation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ticker_that_reduces_to_nothing_is_named_rather_than_empty() =>
        Assert.Equal(AcquisitionCorrelation.Unnamed, AcquisitionCorrelation.Safe(string.Empty));

    // ---- the fingerprint is untouched ----------------------------------------------------------

    /// <summary>
    /// Changing the attempt changes the act and not the work.
    /// </summary>
    /// <remarks>
    /// The single most important assertion here. The ledger's restart guarantee, the suppression
    /// count every batch reports, and the promise that a resumed run does not re-buy history all
    /// rest on the fingerprint being blind to the correlation. If this ever fails, a resumed batch
    /// re-fetches everything it already holds at full vendor cost.
    /// </remarks>
    [Fact]
    public void Two_attempts_at_one_request_share_a_fingerprint()
    {
        var first = PriceRequest("NXST.US", AcquisitionCorrelation.For(2, 1, "NXST", DataCategory.MarketPrices));
        var second = PriceRequest("NXST.US", AcquisitionCorrelation.For(4, 2, "NXST", DataCategory.MarketPrices));

        Assert.Equal(first.Fingerprint(), second.Fingerprint());
        Assert.NotEqual(first.CorrelationId, second.CorrelationId);

        // And therefore the seam's key, which is the fingerprint and the correlation, differs.
        Assert.NotEqual(SeamKey(first), SeamKey(second));
    }

    /// <summary>
    /// The correlation cannot smuggle scope, because scope is what the fingerprint is made of.
    /// </summary>
    /// <remarks>
    /// Source, window and subject each move the fingerprint; the correlation does not. That is the
    /// property that makes an attempt-scoped correlation safe: a new attempt can never become a
    /// request for something else, because the thing being requested is hashed separately.
    /// </remarks>
    [Fact]
    public void Source_window_and_subject_all_move_the_fingerprint()
    {
        var baseline = PriceRequest("NXST.US", AcquisitionCorrelation.For(4, 2, "NXST", DataCategory.MarketPrices));

        var otherSource = IngestionRequest.Create(
            SourceId.Create(ActionsSource),
            DataCategory.CorporateActions,
            Region.Global,
            IngestionSubject.Create("Security", "NXST.US"),
            AcquisitionCorrelation.For(4, 2, "NXST", DataCategory.CorporateActions),
            RequestedAt,
            DateRange.Create(WindowStart, WindowEnd));

        var otherWindow = IngestionRequest.Create(
            SourceId.Create(PriceSource),
            DataCategory.MarketPrices,
            Region.Global,
            IngestionSubject.Create("Security", "NXST.US"),
            AcquisitionCorrelation.For(4, 2, "NXST", DataCategory.MarketPrices),
            RequestedAt,
            DateRange.Create(WindowStart, WindowEnd.AddDays(1)));

        var otherSubject = PriceRequest("SIRI.US", AcquisitionCorrelation.For(4, 2, "SIRI", DataCategory.MarketPrices));

        Assert.NotEqual(baseline.Fingerprint(), otherSource.Fingerprint());
        Assert.NotEqual(baseline.Fingerprint(), otherWindow.Fingerprint());
        Assert.NotEqual(baseline.Fingerprint(), otherSubject.Fingerprint());
    }

    // ---- approved scope is unchanged -----------------------------------------------------------

    /// <summary>
    /// The authorisation this change did not touch, checked rather than asserted in prose.
    /// </summary>
    /// <remarks>
    /// The correction was to how an attempt is named. It must not have moved the ceiling, the
    /// authorised symbols, the sources or the window - so the declaration is loaded here, digest
    /// check and all, and read back against what was approved. Loading it consumes nothing.
    /// </remarks>
    [Fact]
    public void The_approved_scope_is_exactly_what_it_was()
    {
        var authorization = AcquisitionAuthorization.Load(
            Universe.RepositoryPath("declarations", "acquisition-eodhd-sample400.json"),
            SealedFingerprint);

        Assert.Equal(704, authorization.DispatchCeiling);
        Assert.Equal(710, authorization.PlannedRequests);
        Assert.Equal(DateOnly.FromDateTime(WindowStart), authorization.WindowFrom);
        Assert.Equal(DateOnly.FromDateTime(WindowEnd), authorization.WindowTo);
        Assert.Contains(PriceSource, authorization.Sources);
        Assert.Contains(ActionsSource, authorization.Sources);

        Assert.True(
            authorization.Symbols.Count == 355,
            Universe.Inv($"{authorization.Symbols.Count} authorised symbols, not 355"));

        // Reading an authorisation spends none of it.
        Assert.Equal(0, authorization.Consumed);
        Assert.Equal(704, authorization.Remaining);
    }

    // ---- the accounting property, stated as a test ---------------------------------------------

    /// <summary>
    /// Authorisation is spent at the point of intent, and nothing gives it back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The completion attempt consumed two units on two requests the seam refused before either
    /// reached the vendor. That is the accounting working as designed rather than a defect: the
    /// runner charges before it dispatches, so real vendor spend can never be under-counted, and the
    /// price of that guarantee is over-counting when something downstream refuses.
    /// </para>
    /// <para>
    /// The conservative half is the half worth protecting, so it is asserted structurally: there is
    /// no setter and no method that reduces the count. An authorisation that could be credited is
    /// one that could be reset, and a reset ceiling is not a ceiling. This test exists to fail if
    /// anybody ever adds one.
    /// </para>
    /// </remarks>
    [Fact]
    public void Authorisation_is_charged_at_intent_and_can_never_be_credited_back()
    {
        var authorization = AcquisitionAuthorization.Load(
            Universe.RepositoryPath("declarations", "acquisition-eodhd-sample400.json"),
            SealedFingerprint);

        var symbol = authorization.Symbols.Order(StringComparer.Ordinal).First();

        Assert.True(authorization.TryConsume(
            PriceSource,
            symbol,
            DateOnly.FromDateTime(WindowStart),
            DateOnly.FromDateTime(WindowEnd)).Allowed);

        Assert.Equal(1, authorization.Consumed);

        // Whatever happens after the charge - a seam refusal, a transport failure, a run that
        // records nothing - the unit stays spent. Suppression is the only thing that is free, and
        // it is only free because it happens before the charge.
        authorization.RecordSuppressed();

        Assert.Equal(1, authorization.Consumed);
        Assert.Equal(703, authorization.Remaining);

        var settable = typeof(AcquisitionAuthorization)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name)
            .ToList();

        var methods = typeof(AcquisitionAuthorization)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToList();

        Assert.Empty(settable);
        Assert.DoesNotContain(methods, m =>
            m.Contains("Release", StringComparison.Ordinal) ||
            m.Contains("Refund", StringComparison.Ordinal) ||
            m.Contains("Reset", StringComparison.Ordinal) ||
            m.Contains("Credit", StringComparison.Ordinal) ||
            m.Contains("Return", StringComparison.Ordinal));
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>One authorised price request, differing only where a test varies it.</summary>
    private static IngestionRequest PriceRequest(string symbol, CorrelationId correlation) =>
        IngestionRequest.Create(
            SourceId.Create(PriceSource),
            DataCategory.MarketPrices,
            Region.Global,
            IngestionSubject.Create("Security", symbol),
            correlation,
            RequestedAt,
            DateRange.Create(WindowStart, WindowEnd));

    /// <summary>
    /// The Action/Policy seam's idempotency key, in the shape the ingestion gateway builds it.
    /// </summary>
    /// <remarks>
    /// Mirrored rather than called: the gateway's own method is private and this stage must not
    /// change it. The shape is pinned end-to-end by
    /// <c>IngestionGatewayTests.A_run_is_proposed_through_the_seam_under_the_ingestion_capability</c>,
    /// which asserts the real proposal's key against the same expression - so if the gateway's
    /// format ever moves, that test fails rather than this one silently agreeing with itself.
    /// </remarks>
    private static string SeamKey(IngestionRequest request) =>
        Universe.Inv($"{request.Fingerprint()}:{request.CorrelationId}");
}
