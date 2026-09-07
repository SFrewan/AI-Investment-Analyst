using System.Net;
using System.Net.Sockets;
using AI.Investment.Application.Ingestion;
using Xunit;

namespace AI.Investment.Application.UnitTests;

/// <summary>
/// What a failed ingestion is allowed to write into a ledger that can never be edited.
/// </summary>
/// <remarks>
/// <para>
/// Two obligations pull against each other here and both are load-bearing. The row has to say
/// enough that a run of failures can be diagnosed without re-running it - fifty-seven failures
/// once recorded the same eight words and settled nothing. And it must be impossible for a
/// credential to reach it, because an append-only row cannot be redacted after the fact.
/// </para>
/// <para>
/// The design that satisfies both is to read only type names - a closed set, chosen by whoever
/// wrote the exception class, never by a server. These facts test that the chain survives and
/// that a message never does, including when the message is doing its best to look like
/// something worth keeping.
/// </para>
/// </remarks>
public sealed class IngestionFailureDescriptionTests
{
    /// <summary>A message shaped exactly like the thing that must never be stored.</summary>
    private const string Secret =
        "GET https://eodhd.com/api/eod/AAPL.US?api_token=6512a3f9deadbeefcafe1234&fmt=json failed";

    [Fact]
    public void A_bare_exception_is_named_and_nothing_else()
    {
        var described = IngestionGateway.Describe(new InvalidOperationException(Secret));

        Assert.Equal("InvalidOperationException during ingestion.", described);
    }

    /// <summary>
    /// The distinction the run needed: the vendor refused us, or we never reached the vendor.
    /// </summary>
    /// <remarks>
    /// A status the server chose arrives on its own. A connection that never opened arrives
    /// wrapped around a socket failure. Those need entirely different fixes, and until this
    /// change both recorded the same eight words.
    /// </remarks>
    [Fact]
    public void A_refusal_and_an_unreachable_vendor_no_longer_read_the_same()
    {
        var refusedByVendor = IngestionGateway.Describe(
            new HttpRequestException(Secret, null, HttpStatusCode.TooManyRequests));

        var neverReached = IngestionGateway.Describe(
            new HttpRequestException(Secret, new SocketException((int)SocketError.HostNotFound)));

        Assert.Equal("HttpRequestException during ingestion.", refusedByVendor);
        Assert.Contains("SocketException", neverReached, StringComparison.Ordinal);
        Assert.NotEqual(refusedByVendor, neverReached);
    }

    /// <summary>The chain, because the useful fact is usually not the outermost one.</summary>
    [Fact]
    public void The_inner_chain_is_walked_in_order()
    {
        var described = IngestionGateway.Describe(
            new HttpRequestException(
                Secret,
                new IOException(
                    Secret,
                    new SocketException((int)SocketError.ConnectionReset))));

        Assert.Contains("HttpRequestException", described, StringComparison.Ordinal);
        Assert.Contains("IOException", described, StringComparison.Ordinal);
        Assert.Contains("SocketException", described, StringComparison.Ordinal);

        Assert.True(
            described.IndexOf("IOException", StringComparison.Ordinal)
                < described.IndexOf("SocketException", StringComparison.Ordinal),
            "the chain is not recorded outermost first");
    }

    /// <summary>
    /// The guarantee. No message, no URL, no credential, however deep it is buried.
    /// </summary>
    [Fact]
    public void No_message_reaches_the_ledger_at_any_depth()
    {
        var described = IngestionGateway.Describe(
            new HttpRequestException(
                Secret,
                new IOException(
                    Secret,
                    new SocketException((int)SocketError.TimedOut))));

        Assert.DoesNotContain("api_token", described, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("6512a3f9", described, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("eodhd.com", described, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https", described, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AAPL", described, StringComparison.Ordinal);
        Assert.DoesNotContain("?", described, StringComparison.Ordinal);
        Assert.DoesNotContain("=", described, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unbounded chain is an unbounded string in a row that cannot be shortened later.
    /// </summary>
    [Fact]
    public void A_pathological_chain_is_bounded_rather_than_stored_whole()
    {
        Exception nested = new SocketException((int)SocketError.NetworkDown);

        for (var i = 0; i < 40; i++)
        {
            nested = new InvalidOperationException(Secret, nested);
        }

        var described = IngestionGateway.Describe(nested);

        Assert.EndsWith("during ingestion.", described, StringComparison.Ordinal);
        Assert.Contains("<- ...", described, StringComparison.Ordinal);

        // Comfortably inside what an ingestion run will store, with room to spare.
        Assert.True(
            described.Length < 400,
            $"the description is {described.Length} characters, which is not a bounded string");
    }

    /// <summary>
    /// Every failure still reads as one, and still ends the same way, so nothing downstream
    /// that recognised the old text stops recognising a failure.
    /// </summary>
    [Fact]
    public void Every_description_still_ends_the_way_the_ledger_expects()
    {
        Exception[] failures =
        [
            new InvalidOperationException(Secret),
            new HttpRequestException(Secret),
            new HttpRequestException(Secret, new SocketException((int)SocketError.HostUnreachable)),
            new TaskCanceledException(Secret),
            new IOException(Secret),
        ];

        Assert.All(failures, failure =>
        {
            var described = IngestionGateway.Describe(failure);

            Assert.EndsWith(" during ingestion.", described, StringComparison.Ordinal);
            Assert.StartsWith(failure.GetType().Name, described, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, described, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// The classification a connector attached, carried across the layer boundary as a string.
    /// </summary>
    /// <remarks>
    /// This layer must not name a networking type - <c>DataPlaneRuleTests</c> refuses it - so a
    /// connector computes the token in Infrastructure and this reads it through an interface that
    /// knows nothing about HTTP. The fake below is that interface and nothing else.
    /// </remarks>
    [Fact]
    public void A_connectors_transport_classification_is_recorded_when_it_offers_one()
    {
        var described = IngestionGateway.Describe(
            new Classified("socket:HostNotFound", new IOException(Secret)));

        Assert.Contains("[socket:HostNotFound]", described, StringComparison.Ordinal);
        Assert.Contains("IOException", described, StringComparison.Ordinal);
        Assert.DoesNotContain("api_token", described, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(" during ingestion.", described, StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_failure_is_refused_rather_than_described()
    {
        Assert.Throws<ArgumentNullException>(() => IngestionGateway.Describe(null!));
    }

    /// <summary>A failure that carries a classification, and no networking type in sight.</summary>
    private sealed class Classified : Exception, ITransportDiagnostic
    {
        public Classified(string diagnostic, Exception inner)
            : base("a message that must not be stored", inner) =>
            TransportDiagnostic = diagnostic;

        public string TransportDiagnostic { get; }
    }
}
