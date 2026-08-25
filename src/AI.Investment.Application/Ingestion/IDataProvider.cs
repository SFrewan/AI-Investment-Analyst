using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Ingestion;

/// <summary>
/// A connector: the transport half of a registered source.
/// </summary>
/// <remarks>
/// <para>
/// Implementations live in Infrastructure and are the only place in the system that knows a
/// provider's URLs, authentication scheme or wire format. That isolation is the point.
/// <strong>Credentials are deliberately absent from this interface</strong>: a connector reads its
/// own options from configuration inside Infrastructure, so no credential ever crosses into
/// Application or Domain, appears in a method signature, or can be captured by a caller that had
/// no business holding one.
/// </para>
/// <para>
/// The contract is intentionally thin - declare what you can do, fetch bytes. Every decision
/// about whether a fetch is <em>allowed</em> (<see cref="SourceAdmission"/>) or
/// <em>possible</em> (<see cref="ProviderCapabilityCheck"/>) is made before this is called, in
/// pure code that can be tested without a network. A connector that made those judgements itself
/// would put licensing rules in code written to parse JSON.
/// </para>
/// <para>
/// An implementation must not work around a provider's restrictions - no undocumented endpoints,
/// no ignoring a declared rate limit, no circumventing a paywall. Where a provider requires
/// identification (a contact e-mail in a user agent, for example) the connector supplies it from
/// configuration; that is a licensing obligation, not an optional courtesy.
/// </para>
/// </remarks>
public interface IDataProvider
{
    /// <summary>The registry entry this connector serves. Ties transport to trust.</summary>
    SourceId SourceId { get; }

    /// <summary>What this connector can fetch. Declared, never discovered by trying.</summary>
    ProviderCapabilities Capabilities { get; }

    /// <summary>
    /// Fetches one page. Callers repeat with <paramref name="continuationToken"/> from the
    /// previous response until it is null.
    /// </summary>
    /// <remarks>
    /// Implementations should throw on transport or protocol failure rather than returning an
    /// empty response. An empty page and a failed request mean opposite things to a ledger, and
    /// conflating them is how a failure becomes a silent gap.
    /// </remarks>
    Task<ProviderResponse> FetchAsync(
        IngestionRequest request,
        string? continuationToken = null,
        CancellationToken cancellationToken = default);
}
