using AI.Investment.Application.Ingestion;

namespace AI.Investment.Infrastructure.Ingestion.Providers;

/// <summary>
/// A filing document arrived with a successful status and no bytes. That is not a document.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists at all, when an empty 200 is ordinary elsewhere.</strong> For most
/// feeds an empty body is a legitimate answer meaning "nothing in this window", and the gateway is
/// right to archive it and record a success: the absence is the datum. A filing document is the
/// opposite kind of thing. It is one named document inside one accession that the issuer has
/// already filed, so it either exists and has content or the request was wrong. Zero bytes is never
/// its content.
/// </para>
/// <para>
/// <strong>What it prevents.</strong> An archived zero-byte payload is indistinguishable in shape
/// from a document that was read and found to say nothing. A later stage looking for an
/// issuer-stated ticker in these documents could read that emptiness as "this filing states no
/// symbol" - which is exactly the inference F2 and F3 forbid, because it converts a delivery
/// failure into evidence of absence. Failing here means the ledger records a failed acquisition,
/// which is what actually happened, and the archive holds nothing that could be misread.
/// </para>
/// <para>
/// <strong>Scope is deliberately narrow.</strong> The check is applied only to
/// <see cref="AI.Investment.Domain.Sources.DataCategory.RegulatoryFilingDocuments"/>, inside this
/// connector. The gateway's general success rule is untouched, so no other category and no other
/// provider - EODHD included - changes behaviour. Widening it would be a different decision about
/// feeds whose empty responses are meaningful, and it is not this one.
/// </para>
/// <para>
/// It is raised before the payload reaches the archive, so nothing is stored: the failure is
/// recorded on the run ledger and the archive is left exactly as it was.
/// </para>
/// </remarks>
public sealed class EmptyFilingDocumentException : InvalidOperationException, ITransportDiagnostic
{
    /// <summary>
    /// The closed-set token written to the append-only ledger through
    /// <c>IngestionGateway.Describe</c>. A constant, so it carries nothing a provider wrote.
    /// </summary>
    public const string Diagnostic = "filing-document:empty-payload";

    public EmptyFilingDocumentException(string message)
        : base(message)
    {
    }

    public EmptyFilingDocumentException(string message, Exception? inner)
        : base(message, inner)
    {
    }

    public EmptyFilingDocumentException()
        : base("A filing document was returned with no bytes.")
    {
    }

    /// <inheritdoc />
    public string TransportDiagnostic => Diagnostic;
}
