using System.Globalization;
using System.Net;
using System.Net.Sockets;
using AI.Investment.Application.Ingestion;

namespace AI.Investment.Infrastructure.Ingestion;

/// <summary>
/// A connector's transport failure, carrying what kind it was and nothing a provider wrote.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The classification is built from enumerations, never from a message.</strong>
/// <see cref="SocketException.SocketErrorCode"/>, <see cref="HttpRequestException.HttpRequestError"/>
/// and the status code are closed sets defined by the framework or by the HTTP specification. None
/// of them can contain a URL, a token or a response body, which is why they are safe to write into
/// an append-only ledger and why nothing else here is.
/// </para>
/// <para>
/// <strong>It preserves the inner exception, which is the bug this was written for.</strong> The
/// connector used to rethrow a bare <see cref="HttpRequestException"/> with its own message and no
/// inner, which discarded the socket exception underneath - so by the time the ledger saw it, the
/// only surviving fact was that something HTTP-shaped had gone wrong. Preserving the chain costs
/// one argument and restores the whole diagnosis.
/// </para>
/// </remarks>
public sealed class ProviderTransportException : HttpRequestException, ITransportDiagnostic
{
    /// <summary>What is recorded when nothing more specific can be established.</summary>
    public const string Unclassified = "transport";

    public ProviderTransportException(string message, Exception? inner, string diagnostic)
        : base(message, inner, (inner as HttpRequestException)?.StatusCode)
    {
        TransportDiagnostic = diagnostic;
    }

    /// <inheritdoc />
    public string TransportDiagnostic { get; }

    /// <summary>
    /// Names the kind of transport failure, from the exception's structure alone.
    /// </summary>
    /// <remarks>
    /// The innermost <see cref="SocketException"/> wins, because it is the most specific thing
    /// available: "the name did not resolve" and "the connection was reset" are different problems
    /// with different fixes, and both arrive wrapped in the same outer type. Failing that, the
    /// framework's own transport classification; failing that, a status the server chose; and only
    /// then the unclassified fallback, which says so rather than guessing.
    /// </remarks>
    public static string Classify(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"socket:{socket.SocketErrorCode}");
            }
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException http)
            {
                if (http.HttpRequestError != HttpRequestError.Unknown)
                {
                    return string.Create(
                        CultureInfo.InvariantCulture,
                        $"http:{http.HttpRequestError}");
                }

                if (http.StatusCode is { } status)
                {
                    return string.Create(CultureInfo.InvariantCulture, $"status:{(int)status}");
                }
            }
        }

        return Unclassified;
    }
}
