using System.Text;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Ingestion.Providers;
using Microsoft.Extensions.Options;
using Xunit;

namespace AI.Investment.Integration.Tests.Ingestion;

/// <summary>
/// The price-history connector: what it reads, and everything it refuses to read.
/// </summary>
/// <remarks>
/// <para>
/// In the integration project for the same reason the EDGAR connector tests are: it reaches
/// Infrastructure internals, not a database. The transport here is a real directory, created and
/// removed per test, because the whole point of the connector is that it reads a file the operator
/// put somewhere and nothing else.
/// </para>
/// <para>
/// The refusals are the assertions that matter. A missing file must throw rather than answer with
/// an empty payload - an empty payload would be archived and normalised into a series with no
/// prices in it, which reads downstream as an instrument that did not trade - and an identifier
/// carrying a path separator must be refused rather than sanitised, because sanitising turns a bad
/// identifier into a valid path to something else.
/// </para>
/// </remarks>
public sealed class PriceHistoryFileProviderTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    private const string Csv = """
        session_close_utc,close,published_at_utc
        2026-01-02T21:00:00Z,185.64,2026-01-02T21:15:00Z
        2026-01-05T21:00:00Z,187.15,2026-01-05T21:15:00Z
        """;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "price-history-" + Guid.NewGuid().ToString("N"));

    public PriceHistoryFileProviderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // ---- what it reads -------------------------------------------------------------------------

    [Fact]
    public async Task It_returns_the_exact_bytes_the_operator_exported()
    {
        var expected = Encoding.UTF8.GetBytes(Csv);

        await File.WriteAllBytesAsync(Path.Combine(_directory, "AAPL.csv"), expected);

        var response = await Provider().FetchAsync(Request("AAPL"));

        Assert.Equal(expected, response.Payload.ToArray());
        Assert.Equal(PriceHistoryFileProvider.MediaType, response.MediaType);
        Assert.Equal(Now, response.RetrievedAtUtc);
        Assert.Equal("AAPL", response.SourceRecordId);

        // One file is one complete series. Paging a local file would be a request shape invented
        // here rather than one the source offers.
        Assert.False(response.HasMore);
        Assert.Null(response.ContinuationToken);
    }

    // ---- what it refuses -----------------------------------------------------------------------

    /// <summary>
    /// A missing series throws. It is the same distinction the connector contract draws between an
    /// empty page and a failed request, and conflating them turns a gap into a fact.
    /// </summary>
    [Fact]
    public async Task A_missing_series_throws_rather_than_answering_empty()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => Provider().FetchAsync(Request("MSFT")));
    }

    [Theory]
    [InlineData("../secrets")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData(".hidden")]
    [InlineData("with space")]
    [InlineData("")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task An_identifier_that_is_not_an_instrument_symbol_is_refused(string identifier)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Provider().FetchAsync(Request(identifier)));
    }

    /// <summary>A sweep names no instrument, so there is no file and no guess to make.</summary>
    [Fact]
    public async Task A_sweep_is_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Provider().FetchAsync(Request(IngestionSubject.Sweep("Security"))));
    }

    [Fact]
    public async Task A_subject_of_another_kind_is_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Provider().FetchAsync(Request(IngestionSubject.Create("Company", "0000320193"))));
    }

    // ---- what it declares ----------------------------------------------------------------------

    /// <summary>
    /// Declared, never discovered. The gateway checks these before a request is made, so a
    /// capability claimed here and not honoured would be a refusal that never happened.
    /// </summary>
    [Fact]
    public void It_declares_what_a_directory_of_exports_can_answer()
    {
        var capabilities = Provider().Capabilities;

        Assert.True(capabilities.Supports(DataCategory.MarketPrices));
        Assert.False(capabilities.Supports(DataCategory.RegulatoryFilings));
        Assert.True(capabilities.Understands("Security"));
        Assert.False(capabilities.Understands("Company"));
        Assert.True(capabilities.Covers(Region.UnitedStates));

        // A file holds the whole series and has no period parameter. Saying otherwise would let a
        // request for one quarter silently return everything.
        Assert.False(capabilities.SupportsWindow);
        Assert.Null(capabilities.MaxWindowDuration);

        // A local read has no published rate limit to comply with, and an invented one would make
        // the limiter enforce a number nobody stated.
        Assert.Null(capabilities.Quota);
    }

    [Fact]
    public void The_connector_and_its_registry_entry_name_the_same_source()
    {
        Assert.Equal(PriceHistoryFileProvider.Id, Source().SourceId);
        Assert.Equal("operator-price-history", PriceHistoryFileProvider.Id.Value);
    }

    /// <summary>
    /// The registry entry records the operator's terms rather than terms this repository invented,
    /// and it is registered inactive like every other definition.
    /// </summary>
    [Fact]
    public void The_registry_entry_records_the_operators_terms_and_stays_inactive()
    {
        var definition = Source().Definition(Now);

        Assert.False(definition.IsActive);
        Assert.Equal(SourceType.DataVendor, definition.Type);
        Assert.Equal(SourceAuthority.Secondary, definition.Authority);
        Assert.Contains("vendor's own export", definition.Licensing.Notes!, StringComparison.Ordinal);
        Assert.False(definition.Licensing.RedistributionAllowed);
    }

    /// <summary>
    /// A definition built with no terms stated says so, rather than asserting permissions nobody
    /// granted. Unreachable through registration, because the options refuse to validate.
    /// </summary>
    [Fact]
    public void A_definition_with_no_stated_terms_says_so()
    {
        var source = new PriceHistorySource(Options.Create(new MarketDataOptions
        {
            Enabled = true,
            HistoryDirectory = _directory,
        }));

        Assert.Equal(PriceHistorySource.UnstatedTerms, source.Definition(Now).Licensing.Notes);
        Assert.Contains("forbidden", PriceHistorySource.UnstatedTerms, StringComparison.Ordinal);
    }

    // ---- the options ---------------------------------------------------------------------------

    [Fact]
    public void An_enabled_connector_without_a_directory_or_terms_is_invalid()
    {
        var results = new MarketDataOptions { Enabled = true }
            .Validate(new System.ComponentModel.DataAnnotations.ValidationContext(new object()))
            .ToList();

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void A_disabled_connector_needs_nothing()
    {
        Assert.Empty(new MarketDataOptions()
            .Validate(new System.ComponentModel.DataAnnotations.ValidationContext(new object())));
    }

    // ---- helpers -------------------------------------------------------------------------------

    private PriceHistoryFileProvider Provider() =>
        new(Options.Create(Configured()), new PriceHistoryClock(Now));

    private PriceHistorySource Source() => new(Options.Create(Configured()));

    private MarketDataOptions Configured() => new()
    {
        Enabled = true,
        HistoryDirectory = _directory,
        LicensingNotes = "Read from the vendor's own export under this installation's licence.",
        RedistributionAllowed = false,
    };

    private static IngestionRequest Request(string identifier) =>
        Request(IngestionSubject.Create("Security", identifier));

    private static IngestionRequest Request(IngestionSubject subject) =>
        IngestionRequest.Create(
            PriceHistoryFileProvider.Id,
            DataCategory.MarketPrices,
            Region.UnitedStates,
            subject,
            CorrelationId.Create("price-history-test"),
            Now);
}

/// <summary>A clock that does not move.</summary>
internal sealed class PriceHistoryClock : IClock
{
    public PriceHistoryClock(DateTime nowUtc) => UtcNow = nowUtc;

    public DateTime UtcNow { get; }
}
