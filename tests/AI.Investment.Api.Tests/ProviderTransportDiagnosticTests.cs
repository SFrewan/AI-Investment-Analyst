using System.Net;
using System.Net.Sockets;
using AI.Investment.Infrastructure.Ingestion;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// What a connector is allowed to say about a transport failure, and what it may never say.
/// </summary>
/// <remarks>
/// The classification is written verbatim into an append-only ledger. It is built only from
/// enumerations - a socket error, a framework transport error, a status code - because those are
/// closed sets nobody outside the framework chooses. A provider's message is not, and these facts
/// exist to keep it out.
/// </remarks>
public sealed class ProviderTransportDiagnosticTests
{
    /// <summary>A message shaped exactly like the thing that must never be stored.</summary>
    private const string Secret =
        "GET https://eodhd.com/api/eod/AAPL.US?api_token=6512a3f9deadbeefcafe1234&fmt=json failed";

    [Fact]
    public void The_innermost_socket_error_is_what_gets_named()
    {
        Assert.Equal(
            "socket:HostNotFound",
            ProviderTransportException.Classify(
                new HttpRequestException(Secret, new SocketException((int)SocketError.HostNotFound))));

        Assert.Equal(
            "socket:ConnectionReset",
            ProviderTransportException.Classify(
                new HttpRequestException(
                    Secret,
                    new IOException(Secret, new SocketException((int)SocketError.ConnectionReset)))));
    }

    /// <summary>
    /// The distinction the failed run could not draw: never reached them, or they refused us.
    /// </summary>
    [Fact]
    public void A_refusal_and_an_unreachable_host_classify_differently()
    {
        var refused = ProviderTransportException.Classify(
            new HttpRequestException(Secret, null, HttpStatusCode.TooManyRequests));

        var unreachable = ProviderTransportException.Classify(
            new HttpRequestException(Secret, new SocketException((int)SocketError.HostUnreachable)));

        Assert.Equal("status:429", refused);
        Assert.Equal("socket:HostUnreachable", unreachable);
        Assert.NotEqual(refused, unreachable);
    }

    [Fact]
    public void A_framework_transport_error_is_used_when_there_is_no_socket_error()
    {
        Assert.Equal(
            "http:NameResolutionError",
            ProviderTransportException.Classify(
                new HttpRequestException(HttpRequestError.NameResolutionError, Secret)));

        Assert.Equal(
            "http:SecureConnectionError",
            ProviderTransportException.Classify(
                new HttpRequestException(HttpRequestError.SecureConnectionError, Secret)));
    }

    /// <summary>An unrecognisable failure says so rather than inventing a cause.</summary>
    [Fact]
    public void An_unclassifiable_failure_is_named_unclassified()
    {
        Assert.Equal(
            ProviderTransportException.Unclassified,
            ProviderTransportException.Classify(new InvalidOperationException(Secret)));

        Assert.Equal(ProviderTransportException.Unclassified, ProviderTransportException.Classify(null));
    }

    /// <summary>
    /// The guarantee. Every classification this can produce is a closed-set token.
    /// </summary>
    [Fact]
    public void No_classification_can_carry_a_url_a_token_or_a_message()
    {
        Exception[] failures =
        [
            new HttpRequestException(Secret),
            new HttpRequestException(Secret, null, HttpStatusCode.Unauthorized),
            new HttpRequestException(Secret, new SocketException((int)SocketError.TimedOut)),
            new HttpRequestException(HttpRequestError.ConnectionError, Secret),
            new IOException(Secret, new SocketException((int)SocketError.NetworkDown)),
            new InvalidOperationException(Secret),
        ];

        Assert.All(failures, failure =>
        {
            var diagnostic = ProviderTransportException.Classify(failure);

            Assert.DoesNotContain("api_token", diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("6512a3f9", diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("eodhd", diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("http://", diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("https", diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("AAPL", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("=", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain(" ", diagnostic, StringComparison.Ordinal);

            // Short by construction: a prefix, a colon and an enumeration member.
            Assert.True(diagnostic.Length < 40, $"'{diagnostic}' is not a closed-set token");
        });
    }

    /// <summary>
    /// The bug this was written for: the connector used to drop the inner exception.
    /// </summary>
    [Fact]
    public void The_exception_preserves_the_chain_and_the_status_it_was_given()
    {
        var inner = new HttpRequestException(Secret, new SocketException((int)SocketError.HostNotFound));

        var wrapped = new ProviderTransportException(
            "The EODHD request for 'X.US' failed before a response was received.",
            inner,
            ProviderTransportException.Classify(inner));

        Assert.Same(inner, wrapped.InnerException);
        Assert.Equal("socket:HostNotFound", wrapped.TransportDiagnostic);
        Assert.IsAssignableFrom<HttpRequestException>(wrapped);

        // And a status, when the failure carried one, survives the wrapping.
        var refused = new HttpRequestException(Secret, null, HttpStatusCode.TooManyRequests);

        var wrappedRefusal = new ProviderTransportException(
            "refused", refused, ProviderTransportException.Classify(refused));

        Assert.Equal(HttpStatusCode.TooManyRequests, wrappedRefusal.StatusCode);
    }
}
