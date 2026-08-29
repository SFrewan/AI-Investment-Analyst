using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AI.Investment.Infrastructure.Ingestion.Providers;

/// <summary>
/// Connector for a daily price history the operator holds a licence for and exports to disk.
/// </summary>
/// <remarks>
/// <para>
/// The platform needs closing prices before it can measure anything: Phase 7's guard admits a
/// prediction only when the evidence behind it resolves, and every performance figure it computes
/// reads <c>security.close</c>. There is no free, licensed, redistributable source of daily closes
/// this repository could ship a connector for, and inventing prices is the one thing a system that
/// measures its own decisions must never do. So the transport here is a directory: the operator
/// puts the series they already pay for where the connector can read it, and states the terms in
/// configuration.
/// </para>
/// <para>
/// <strong>It is a connector like any other, and that is the point.</strong> It implements
/// <see cref="IDataProvider"/>, it declares its capabilities rather than discovering them, it
/// returns the exact bytes it read, and it parses nothing - normalisation happens afterwards
/// against the archived copy. A vendor API can replace it by registering a different
/// <see cref="IDataProvider"/> for the same source, or take its place beside it under a source of
/// its own; nothing above this class enumerates connectors.
/// </para>
/// <para>
/// <strong>It never fabricates and never substitutes.</strong> A missing file throws, exactly as a
/// failed HTTP request would, because an empty response and a failed request mean opposite things
/// to a ledger. There is no generated series, no interpolation and no default price anywhere in
/// this file.
/// </para>
/// </remarks>
public sealed class PriceHistoryFileProvider : IDataProvider
{
    /// <summary>The registry key. Matches <see cref="PriceHistorySource"/>.</summary>
    public static readonly SourceId Id = SourceId.Create("operator-price-history");

    /// <summary>The subject kind this connector understands.</summary>
    public const string SecurityKind = "Security";

    /// <summary>The media type every payload is recorded under.</summary>
    public const string MediaType = "text/csv";

    /// <summary>The longest instrument symbol that can name a file.</summary>
    public const int MaxIdentifierLength = 20;

    private readonly MarketDataOptions _options;
    private readonly IClock _clock;

    public PriceHistoryFileProvider(IOptions<MarketDataOptions> options, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        Capabilities = BuildCapabilities();
    }

    public SourceId SourceId => Id;

    public ProviderCapabilities Capabilities { get; }

    public async Task<ProviderResponse> FetchAsync(
        IngestionRequest request,
        string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var identifier = SafeIdentifier(request.Subject);

        if (identifier is null)
        {
            throw new InvalidOperationException(
                $"A price history is keyed by a bare instrument symbol, and '{request.Subject}' is " +
                "not one. A sweep has no file, and an identifier carrying a path separator is not a " +
                "symbol - it is an attempt to read somewhere else.");
        }

        var path = Path.Combine(_options.HistoryDirectory, identifier + ".csv");

        if (!File.Exists(path))
        {
            // Thrown rather than answered with an empty payload. The gateway records a failed run;
            // an empty response would be archived and normalised into a series with no prices in
            // it, which reads downstream as "this instrument did not trade".
            throw new FileNotFoundException(
                $"No price history is available for '{identifier}'. The connector reads what the " +
                "operator exported and does not produce prices of its own.",
                path);
        }

        var payload = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

        return ProviderResponse.Create(
            payload,
            MediaType,
            _clock.UtcNow,
            sourceRecordId: identifier);

        // No continuation token: one file is one complete series. Paging a local file would be a
        // request shape invented here rather than one the source offers.
    }

    /// <summary>
    /// The subject's identifier when it can safely name a file, and null when it cannot.
    /// </summary>
    /// <remarks>
    /// Letters, digits, dot and hyphen only, which is what an instrument symbol is. Everything else
    /// - separators, <c>..</c>, colons, spaces, an empty string - is refused rather than sanitised.
    /// Sanitising turns a bad identifier into a valid path to something else; refusing turns it
    /// into a recorded failure.
    /// </remarks>
    internal static string? SafeIdentifier(IngestionSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        if (!string.Equals(subject.Kind, SecurityKind, StringComparison.Ordinal))
        {
            return null;
        }

        var identifier = subject.Identifier;

        if (string.IsNullOrWhiteSpace(identifier) || identifier.Length > MaxIdentifierLength)
        {
            return null;
        }

        foreach (var c in identifier)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '.' && c != '-')
            {
                return null;
            }
        }

        return identifier[0] == '.' || identifier[^1] == '.' ? null : identifier;
    }

    /// <summary>
    /// What a directory of exported price histories can answer.
    /// </summary>
    /// <remarks>
    /// <c>supportsWindow: false</c> is a statement of fact: a file holds the whole series and has
    /// no period parameter. Narrowing to a window is the reader's job, and pretending the connector
    /// could do it would let a request for one quarter silently return everything.
    /// <para>
    /// <see cref="Region.Global"/> because a file says nothing about where its instrument trades.
    /// The registry entry records the authority; the connector records only that it can read the
    /// file whatever market it came from.
    /// </para>
    /// <para>
    /// No quota. A local read has no published rate limit to comply with, and declaring an invented
    /// one would make the rate limiter enforce a number nobody stated.
    /// </para>
    /// </remarks>
    private static ProviderCapabilities BuildCapabilities() =>
        ProviderCapabilities.Create(
            [DataCategory.MarketPrices],
            [Region.Global],
            [SecurityKind],
            supportsWindow: false,
            maxWindowDuration: null,
            quota: null);
}
