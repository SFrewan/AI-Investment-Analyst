using System.Text.RegularExpressions;

namespace AI.Investment.Infrastructure.Ingestion.Providers;

/// <summary>
/// One filing document, named the way the held filing pointers already name it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A filing document is identified by three things the platform already holds</strong> - the
/// filer's CIK, EDGAR's accession number for the filing, and the primary document's own filename.
/// Every one of the 2,913 filing pointers carries all three, keyed by accession, so a target is
/// reassembled from evidence rather than composed by a caller.
/// </para>
/// <para>
/// <strong>A URL is not accepted as identity.</strong> The subject names the filing; the path is
/// derived from it. Taking a free-form URL would let a caller reach anything the connector's
/// credentials can reach, and would make the archived bytes' provenance a string somebody typed.
/// </para>
/// </remarks>
/// <param name="Cik">The filer, ten digits, zero-padded.</param>
/// <param name="AccessionNumber">EDGAR's identity for the filing, dashed form.</param>
/// <param name="PrimaryDocument">The document's own filename, exactly as the index states it.</param>
public sealed partial record FilingDocumentSubject(
    string Cik,
    string AccessionNumber,
    string PrimaryDocument)
{
    /// <summary>
    /// The subject kind a filing-document request is about: one document, not a company.
    /// </summary>
    /// <remarks>
    /// Distinct from <c>Company</c> deliberately. A company subject names a filer and resolves to
    /// an index; this names one document inside one filing. Sharing the kind would let a capability
    /// check pass a company identifier into the document endpoint, and the archived bytes would be
    /// a submissions index filed as though it were the document it lists.
    /// </remarks>
    public const string SubjectKind = "FilingDocument";

    /// <summary>The separator between the three parts of a subject identifier.</summary>
    public const char Separator = '|';

    /// <summary>The longest a document filename may be.</summary>
    public const int MaxDocumentLength = 128;

    /// <summary>The accession number with its dashes removed, as EDGAR's archive paths spell it.</summary>
    public string AccessionPathSegment => AccessionNumber.Replace("-", string.Empty, StringComparison.Ordinal);

    /// <summary>The CIK with leading zeros removed, as EDGAR's archive paths spell it.</summary>
    public string CikPathSegment => Cik.TrimStart('0') is { Length: > 0 } trimmed ? trimmed : "0";

    /// <summary>
    /// Parses a subject identifier, or returns a refusal naming what was wrong with it.
    /// </summary>
    /// <remarks>
    /// Total, and refusing rather than throwing, because this is the boundary between an operator's
    /// input and a URL path. Every rejection below is a way an identifier could reach an endpoint
    /// nobody authorised, and each is named so a refusal says which.
    /// </remarks>
    public static FilingDocumentSubjectResult Parse(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return FilingDocumentSubjectResult.Refused(FilingDocumentRefusal.MissingSubject);
        }

        var parts = identifier.Trim().Split(Separator);

        if (parts.Length != 3)
        {
            return FilingDocumentSubjectResult.Refused(FilingDocumentRefusal.MalformedSubject);
        }

        var cik = SecEdgarEndpoints.NormaliseCik(parts[0]);

        if (cik is null)
        {
            return FilingDocumentSubjectResult.Refused(FilingDocumentRefusal.MissingOrInvalidCik);
        }

        var accession = parts[1].Trim();

        if (accession.Length == 0)
        {
            return FilingDocumentSubjectResult.Refused(FilingDocumentRefusal.MissingAccessionNumber);
        }

        if (!AccessionPattern().IsMatch(accession))
        {
            return FilingDocumentSubjectResult.Refused(FilingDocumentRefusal.MalformedAccessionNumber);
        }

        var document = parts[2].Trim();

        if (document.Length == 0)
        {
            return FilingDocumentSubjectResult.Refused(FilingDocumentRefusal.MissingPrimaryDocument);
        }

        if (document.Length > MaxDocumentLength)
        {
            return FilingDocumentSubjectResult.Refused(FilingDocumentRefusal.MalformedPrimaryDocument);
        }

        // A viewer-prefixed document names a rendering, not the filing. Refused rather than
        // rewritten - see FilingDocumentRefusal.ViewerRenderedDocument.
        if (document.Contains('/', StringComparison.Ordinal) || document.Contains('\\', StringComparison.Ordinal))
        {
            return FilingDocumentSubjectResult.Refused(FilingDocumentRefusal.ViewerRenderedDocument);
        }

        if (!DocumentPattern().IsMatch(document))
        {
            return FilingDocumentSubjectResult.Refused(FilingDocumentRefusal.MalformedPrimaryDocument);
        }

        return FilingDocumentSubjectResult.Accepted(new FilingDocumentSubject(cik, accession, document));
    }

    /// <summary>The identifier form this subject round-trips through.</summary>
    public string ToIdentifier() => $"{Cik}{Separator}{AccessionNumber}{Separator}{PrimaryDocument}";

    /// <summary>EDGAR's dashed accession form: ten digits, two, then six.</summary>
    [GeneratedRegex(@"^[0-9]{10}-[0-9]{2}-[0-9]{6}$")]
    private static partial Regex AccessionPattern();

    /// <summary>
    /// A document filename: ASCII letters, digits, and the three separators EDGAR filenames use.
    /// </summary>
    /// <remarks>
    /// No dot-segments, no slashes, no percent signs, no whitespace - the same boundary
    /// <c>ParseFrame</c> draws, for the same reason. A dot is permitted because every filename has
    /// an extension, but <c>..</c> is refused explicitly below it.
    /// </remarks>
    [GeneratedRegex(@"^(?!.*\.\.)[A-Za-z0-9]([A-Za-z0-9._-]*[A-Za-z0-9])?\.[A-Za-z0-9]{1,8}$")]
    private static partial Regex DocumentPattern();
}

/// <summary>Why a filing-document subject was refused. Exactly one reason, never a default.</summary>
public enum FilingDocumentRefusal
{
    /// <summary>Not a refusal. Present so the default value is obviously wrong.</summary>
    None = 0,

    /// <summary>Nothing was supplied.</summary>
    MissingSubject = 1,

    /// <summary>The identifier is not three separated parts.</summary>
    MalformedSubject = 2,

    /// <summary>No CIK, or one that cannot be ten digits.</summary>
    MissingOrInvalidCik = 3,

    /// <summary>No accession number.</summary>
    MissingAccessionNumber = 4,

    /// <summary>An accession number that is not EDGAR's dashed form.</summary>
    MalformedAccessionNumber = 5,

    /// <summary>No primary document.</summary>
    MissingPrimaryDocument = 6,

    /// <summary>A document filename carrying characters a path segment may not.</summary>
    MalformedPrimaryDocument = 7,

    /// <summary>
    /// The primary document names an XSL-rendered view of the filing rather than the filing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Refused, not rewritten.</strong> 1,385 of the 2,913 held pointers carry a path
    /// separator and every one of them begins with an <c>xsl</c> segment -
    /// <c>xslF345X03/doc4.xml</c>, <c>xslF25X02/primary_doc.xml</c>, <c>xslFormDX01/primary_doc.xml</c>.
    /// The pattern is plain in the evidence. What the evidence does <em>not</em> establish is that
    /// the final segment is the stored document: that is knowledge about how EDGAR renders forms,
    /// not something any held file states.
    /// </para>
    /// <para>
    /// Stripping the prefix on that basis would put an assumption between a request and the bytes
    /// it archives, and the archive is meant to hold exactly what was filed. Refusing costs
    /// nothing that matters: <strong>none of the 546 held 8-K pointers carries a separator</strong>,
    /// and 8-K is the entire evidence scope F3 proposed. A transform rule needs its own evidence -
    /// the filing index for an accession would supply it - and that is a later stage's work.
    /// </para>
    /// </remarks>
    ViewerRenderedDocument = 8,
}

/// <summary>A parsed subject, or the reason it was refused.</summary>
public sealed record FilingDocumentSubjectResult
{
    private FilingDocumentSubjectResult(FilingDocumentSubject? subject, FilingDocumentRefusal refusal)
    {
        Subject = subject;
        Refusal = refusal;
    }

    public FilingDocumentSubject? Subject { get; }

    public FilingDocumentRefusal Refusal { get; }

    public bool IsAccepted => Subject is not null;

    public static FilingDocumentSubjectResult Accepted(FilingDocumentSubject subject) =>
        new(subject, FilingDocumentRefusal.None);

    public static FilingDocumentSubjectResult Refused(FilingDocumentRefusal refusal) =>
        refusal == FilingDocumentRefusal.None
            ? throw new ArgumentException(
                "A refusal states why. Refusing with no reason would hide which boundary the "
                    + "identifier failed.",
                nameof(refusal))
            : new FilingDocumentSubjectResult(null, refusal);
}
