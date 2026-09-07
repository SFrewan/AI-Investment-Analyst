namespace AI.Investment.Application.Ingestion;

/// <summary>
/// A failure that can say what kind of transport failure it was, in words safe to store.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why an interface, and why it lives here.</strong> Fifty-seven requests failed in one run
/// and every one recorded <c>HttpRequestException during ingestion.</c> - equally true of a
/// name-resolution failure, a refused connection, a TLS fault and a 429, and so useless for
/// deciding what to fix. The values that separate those cases are <c>SocketError</c> and
/// <c>HttpRequestError</c>, and reading them means naming <c>System.Net.Sockets</c> and
/// <c>System.Net.Http</c> - which <c>DataPlaneRuleTests.Application_cannot_reach_the_network</c>
/// forbids in this layer, on the sound ground that an application service which knows about HTTP is
/// one step from making a request outside the gateway.
/// </para>
/// <para>
/// So the classification is computed where those types are already at hand - in the connector, in
/// Infrastructure - and crosses the boundary as a string. The application layer reads a string and
/// learns nothing about HTTP, and the rule stands unweakened.
/// </para>
/// <para>
/// <strong>What implementations may put here.</strong> Closed-set names only: enumeration members,
/// status codes, exception type names. Never a message, a URL, a header, a response body or
/// anything derived from one. This value is written verbatim into an append-only ledger that
/// cannot be redacted afterwards, and a credential that reaches it cannot be taken back out.
/// </para>
/// </remarks>
public interface ITransportDiagnostic
{
    /// <summary>
    /// A short, closed-set description of the transport failure - never free text from a provider.
    /// </summary>
    string TransportDiagnostic { get; }
}
