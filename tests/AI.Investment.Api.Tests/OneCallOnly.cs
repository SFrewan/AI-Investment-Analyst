namespace AI.Investment.Api.Tests;

/// <summary>
/// A message handler that permits one request and refuses every one after it.
/// </summary>
/// <remarks>
/// <para>
/// The verifications that reach EODHD are authorised one call at a time. Making that a property
/// of the client rather than of the code above it means a redirect followed automatically, a retry
/// inside <c>HttpClient</c>, or a future edit that adds "just one more check" fails loudly here
/// instead of spending a call nobody agreed to. A budget that is a comment is not a budget.
/// </para>
/// <para>
/// Shared rather than copied into each verification. It is the one control those tests exist under,
/// and a second implementation of a control is a second place for it to drift - which is exactly
/// the reasoning the connectors already apply to sharing their redaction routine.
/// </para>
/// </remarks>
internal sealed class OneCallOnly : DelegatingHandler
{
    private int _sent;

    /// <summary>How many requests were attempted. One is the only passing answer.</summary>
    public int Sent => _sent;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _sent) > 1)
        {
            throw new InvalidOperationException(
                "This verification is authorised for exactly one request and has already sent it. " +
                "Whatever asked for a second one is not part of what was agreed.");
        }

        return base.SendAsync(request, cancellationToken);
    }
}
