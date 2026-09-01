using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Domain.Ingestion;

/// <summary>
/// What to fetch, from where, about what, and for which period.
/// </summary>
/// <remarks>
/// <para>
/// Says nothing about how. The source identifier resolves to a registry entry, and the registry
/// holds trust rather than transport - so this type stays free of URLs, credentials and paging
/// tokens exactly as <see cref="DataSource"/> does. A connector turns a request into HTTP; nothing
/// above the connector needs to know that HTTP was involved.
/// </para>
/// <para>
/// <see cref="Window"/> is optional because not every request has one. "The latest quote" has no
/// period; "filings published in Q1" does.
/// </para>
/// </remarks>
public sealed record IngestionRequest
{
    private IngestionRequest(
        SourceId sourceId,
        DataCategory category,
        Region region,
        IngestionSubject subject,
        DateRange? window,
        CorrelationId correlationId,
        DateTime requestedAtUtc)
    {
        SourceId = sourceId;
        Category = category;
        Region = region;
        Subject = subject;
        Window = window;
        CorrelationId = correlationId;
        RequestedAtUtc = requestedAtUtc;
    }

    /// <summary>Required by the persistence provider. Not for application use.</summary>
    /// <remarks>
    /// <para>
    /// EF materialises this type through this constructor and then sets each property. It cannot
    /// use the constructor above, because two of that constructor's parameters -
    /// <see cref="Subject"/> and <see cref="Window"/> - are owned types, and an owned reference is
    /// a <em>navigation</em>. EF binds constructor parameters to mapped scalar properties only; it
    /// cannot set a navigation through a constructor at all. Without a parameterless constructor
    /// there is no candidate it can bind, and model building fails with "No suitable constructor
    /// was found for entity type 'IngestionRequest'".
    /// </para>
    /// <para>
    /// This is the same pattern every aggregate in this model already uses - <c>Observation</c>,
    /// <c>DataSource</c>, <c>IngestionRun</c>, <c>QuarantinedPayload</c> - and it changes nothing
    /// about how application code constructs a request: <see cref="Create"/> remains the only way
    /// in, and every validation rule it applies is untouched.
    /// </para>
    /// <para>
    /// The non-nullable properties are assigned <c>null!</c> here for the same reason they are on
    /// those aggregates: the provider overwrites every one of them immediately after construction,
    /// and the alternative - making them nullable - would weaken the type for every legitimate
    /// caller to accommodate a materialisation step that never observes these values.
    /// </para>
    /// </remarks>
    private IngestionRequest()
    {
        SourceId = null!;
        Region = null!;
        Subject = null!;
        CorrelationId = null!;
    }

    public SourceId SourceId { get; }

    public DataCategory Category { get; }

    public Region Region { get; }

    public IngestionSubject Subject { get; }

    /// <summary>The period the request is about, when it has one.</summary>
    public DateRange? Window { get; }

    public CorrelationId CorrelationId { get; }

    public DateTime RequestedAtUtc { get; }

    public static IngestionRequest Create(
        SourceId sourceId,
        DataCategory category,
        Region region,
        IngestionSubject subject,
        CorrelationId correlationId,
        DateTime requestedAtUtc,
        DateRange? window = null)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(correlationId);
        DateRange.EnsureUtc(requestedAtUtc, nameof(requestedAtUtc));

        if (!Enum.IsDefined(category) || category == DataCategory.Unknown)
        {
            throw new DomainValidationException(
                nameof(category),
                $"'{category}' is not a data category that can be requested.");
        }

        return new IngestionRequest(
            sourceId,
            category,
            region,

            // COPIES, never the caller's instances - the rule Observation.RecordFact already
            // states for its subject, applied here for the same reason. Both of these are owned
            // entities, and a caller that builds one window and issues two requests inside one
            // scope hands the same object to two owners; the provider reads that as re-parenting
            // and refuses the save. That is exactly what stopped the Block 2B backfill, after the
            // provider call had been made and paid for. Copying a value costs nothing and removes
            // a trap that only fires once money has already been spent.
            IngestionSubject.Create(subject.Kind, subject.Identifier),
            window is null ? null : DateRange.Create(window.StartUtc, window.EndUtc),
            correlationId,
            requestedAtUtc);
    }

    /// <summary>
    /// A stable identifier for "this exact request", independent of when it was made.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Used as the idempotency key when the run is proposed through the Action/Policy seam, so a
    /// retry after a timeout cannot fetch and archive the same window twice. Deliberately excludes
    /// <see cref="RequestedAtUtc"/> and <see cref="CorrelationId"/>: including either would make
    /// every retry a different request, which is precisely the bug an idempotency key exists to
    /// prevent.
    /// </para>
    /// <para>
    /// Culture-invariant throughout. A fingerprint that differed between machines because of a
    /// locale would be worse than no fingerprint at all, because it would fail only sometimes.
    /// </para>
    /// </remarks>
    public string Fingerprint()
    {
        // Bound to a local so the null check and the use are unambiguously the same value, and
        // so the interpolation cannot be read as a nullable member access.
        var range = Window;

        var window = range is null
            ? "-"
            : string.Create(CultureInfo.InvariantCulture, $"{range.StartUtc:O}..{range.EndUtc:O}");

        var canonical = string.Join(
            '\n',
            SourceId.Value,
            Category.ToString(),
            Region.Code,
            Subject.ToString(),
            window);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    public override string ToString() =>
        $"{Category} for {Subject} from {SourceId} ({Region})";
}
