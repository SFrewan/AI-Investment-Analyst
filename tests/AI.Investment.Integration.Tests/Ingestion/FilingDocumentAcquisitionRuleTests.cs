using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Ingestion.Providers;
using Xunit;

namespace AI.Investment.Integration.Tests.Ingestion;

/// <summary>
/// The filing-document capability: deterministic targeting, and no path to the network yet.
/// </summary>
/// <remarks>
/// <para>
/// Every fact here runs without a network and without a database. That is the point of the design:
/// the part of a connector most likely to be wrong is the part that builds a path, and it is pure.
/// </para>
/// <para>
/// <strong>The dispatch facts matter more than the parsing ones.</strong> F4 builds a capability and
/// must not enable an acquisition, so several of these assert an absence - that the source does not
/// declare the category, that the provider does not claim the capability, and that no CIK-keyed
/// endpoint serves it. Each is a gate the existing ingestion gateway already consults.
/// </para>
/// </remarks>
public sealed class FilingDocumentAcquisitionRuleTests
{
    private const string Cik = "0000892482";
    private const string Accession = "0000897101-04-002197";
    private const string Document = "rimage044983_8k.htm";

    private static string Identifier(string cik = Cik, string accession = Accession, string document = Document) =>
        $"{cik}|{accession}|{document}";

    // ---- 1. deterministic target identity ---------------------------------------------------

    [Fact]
    public void A_valid_filing_target_parses_and_derives_one_deterministic_path()
    {
        var result = FilingDocumentSubject.Parse(Identifier());

        Assert.True(result.IsAccepted);

        var subject = result.Subject!;

        Assert.Equal(Cik, subject.Cik);
        Assert.Equal(Accession, subject.AccessionNumber);
        Assert.Equal(Document, subject.PrimaryDocument);

        // Leading zeros off the CIK, dashes out of the accession - both derived, never asked for.
        Assert.Equal("892482", subject.CikPathSegment);
        Assert.Equal("000089710104002197", subject.AccessionPathSegment);

        var path = SecEdgarEndpoints.FilingDocument(subject);

        Assert.Equal("Archives/edgar/data/892482/000089710104002197/rimage044983_8k.htm", path);

        // Same subject, same path, every time.
        Assert.Equal(path, SecEdgarEndpoints.FilingDocument(FilingDocumentSubject.Parse(Identifier()).Subject!));
    }

    [Fact]
    public void A_subject_round_trips_through_its_identifier_form()
    {
        var subject = FilingDocumentSubject.Parse(Identifier()).Subject!;

        Assert.Equal(Identifier(), subject.ToIdentifier());
        Assert.Equal(subject, FilingDocumentSubject.Parse(subject.ToIdentifier()).Subject);
    }

    // ---- 2-5. refusals, each with its own named reason ---------------------------------------

    [Theory]
    [InlineData(null, FilingDocumentRefusal.MissingSubject)]
    [InlineData("", FilingDocumentRefusal.MissingSubject)]
    [InlineData("   ", FilingDocumentRefusal.MissingSubject)]
    [InlineData("0000892482", FilingDocumentRefusal.MalformedSubject)]
    [InlineData("0000892482|0000897101-04-002197", FilingDocumentRefusal.MalformedSubject)]
    public void An_unusable_identifier_is_refused_with_its_own_reason(string? identifier, FilingDocumentRefusal expected)
    {
        var result = FilingDocumentSubject.Parse(identifier);

        Assert.False(result.IsAccepted);
        Assert.Equal(expected, result.Refusal);
        Assert.Null(result.Subject);
    }

    /// <summary>2. A missing or unusable CIK.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-cik")]
    [InlineData("00008924821111")]
    public void A_missing_or_invalid_cik_is_refused(string cik)
    {
        var result = FilingDocumentSubject.Parse(Identifier(cik: cik));

        Assert.False(result.IsAccepted);
        Assert.Equal(FilingDocumentRefusal.MissingOrInvalidCik, result.Refusal);
    }

    /// <summary>3, 5. A missing or malformed accession number.</summary>
    [Theory]
    [InlineData("", FilingDocumentRefusal.MissingAccessionNumber)]
    [InlineData("   ", FilingDocumentRefusal.MissingAccessionNumber)]
    [InlineData("000089710104002197", FilingDocumentRefusal.MalformedAccessionNumber)]
    [InlineData("0000897101-4-002197", FilingDocumentRefusal.MalformedAccessionNumber)]
    [InlineData("0000897101-04-00219", FilingDocumentRefusal.MalformedAccessionNumber)]
    [InlineData("abcdefghij-04-002197", FilingDocumentRefusal.MalformedAccessionNumber)]
    public void A_missing_or_malformed_accession_is_refused(string accession, FilingDocumentRefusal expected)
    {
        var result = FilingDocumentSubject.Parse(Identifier(accession: accession));

        Assert.False(result.IsAccepted);
        Assert.Equal(expected, result.Refusal);
    }

    /// <summary>4. A missing primary document.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_primary_document_is_refused(string document)
    {
        var result = FilingDocumentSubject.Parse(Identifier(document: document));

        Assert.False(result.IsAccepted);
        Assert.Equal(FilingDocumentRefusal.MissingPrimaryDocument, result.Refusal);
    }

    /// <summary>
    /// A document name that could climb out of its path, or carry a query, is refused.
    /// </summary>
    /// <remarks>
    /// The same boundary <c>ParseFrame</c> draws. A slash is refused before this as a viewer path;
    /// everything else that could change the meaning of a URL is refused here.
    /// </remarks>
    [Theory]
    [InlineData("..")]
    [InlineData("..%2Fsecret.htm")]
    [InlineData("doc.htm?x=1")]
    [InlineData("doc .htm")]
    [InlineData("doc#frag.htm")]
    [InlineData("doc")]
    [InlineData("../../etc/passwd")]
    public void A_document_name_that_could_reach_elsewhere_is_refused(string document)
    {
        var result = FilingDocumentSubject.Parse(Identifier(document: document));

        Assert.False(result.IsAccepted);
        Assert.NotEqual(FilingDocumentRefusal.None, result.Refusal);
    }

    // ---- 21. the viewer-prefix rule ----------------------------------------------------------

    /// <summary>
    /// A viewer-rendered document is refused, and refused by its own name.
    /// </summary>
    /// <remarks>
    /// These are real values from the held pointers. Refused rather than rewritten: the evidence
    /// shows every separator-bearing document begins with an <c>xsl</c> segment, but nothing held
    /// states that the final segment is the stored filing, and the archive must hold what was filed.
    /// </remarks>
    [Theory]
    [InlineData("xslF25X02/primary_doc.xml")]
    [InlineData("xslF345X03/doc4.xml")]
    [InlineData("xslFormDX01/primary_doc.xml")]
    [InlineData("xslEFFECTX01/primary_doc.xml")]
    [InlineData("xslSCHEDULE_13G_X01/primary_doc.xml")]
    [InlineData("primary_doc.xml\\nested.xml")]
    public void A_viewer_rendered_document_is_refused_rather_than_rewritten(string document)
    {
        var result = FilingDocumentSubject.Parse(Identifier(document: document));

        Assert.False(result.IsAccepted);
        Assert.Equal(FilingDocumentRefusal.ViewerRenderedDocument, result.Refusal);

        // And nothing silently produced the underlying name instead.
        Assert.Null(result.Subject);
    }

    /// <summary>The three document grammars the pointers carry all parse, unmodified.</summary>
    [Theory]
    [InlineData("rimage044983_8k.htm")]
    [InlineData("shapewaysholdingsinc-form2.htm")]
    [InlineData("primary_doc.xml")]
    [InlineData("qumu-1512g_022123.htm")]
    [InlineData("0000897101-04-002197.txt")]
    [InlineData("tm2129009-10_4seq1.xml")]
    public void Every_document_grammar_the_pointers_carry_parses_unchanged(string document)
    {
        var result = FilingDocumentSubject.Parse(Identifier(document: document));

        Assert.True(result.IsAccepted, document + " was refused");
        Assert.Equal(document, result.Subject!.PrimaryDocument);
        Assert.EndsWith(document, SecEdgarEndpoints.FilingDocument(result.Subject), StringComparison.Ordinal);
    }

    // ---- 6, 7. no dispatch path exists yet ---------------------------------------------------

    /// <summary>
    /// No CIK-keyed endpoint serves filing documents, so no existing request path reaches one.
    /// </summary>
    [Fact]
    public void No_cik_keyed_endpoint_serves_filing_documents()
    {
        Assert.Null(SecEdgarEndpoints.ForCategory(DataCategory.RegulatoryFilingDocuments, Cik));

        // The submissions categories still resolve, unchanged.
        Assert.NotNull(SecEdgarEndpoints.ForCategory(DataCategory.RegulatoryFilings, Cik));
        Assert.NotNull(SecEdgarEndpoints.ForCategory(DataCategory.FinancialStatements, Cik));
    }

    /// <summary>
    /// The SEC source declares the category. F6g is the stage that added it, and opened admission.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the authorisation gate, and it is the one the ingestion gateway already consults -
    /// no second mechanism was built. Admitting the category is a deliberate, separate act, and
    /// until somebody performs it a filing-document request cannot be dispatched at all.
    /// </para>
    /// <para>
    /// <strong>The wait is the point.</strong> F4 wrote this to record that building a capability
    /// grants nothing: the category existed, the endpoint existed, and admission still refused every
    /// filing-document request before the wire. It stayed that way through F4b, F5, F6, F6b and F6c.
    /// F6d added the declaration a stage early and F6e took it back out, because a fresh
    /// installation would otherwise have registered the source with the category already admitted.
    /// It is present now, added by the stage that actually opens admission and immediately spends
    /// the authorisation it guards.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_sec_source_now_declares_the_filing_document_category()
    {
        var definition = typeof(SecEdgarSource);

        var source = File.ReadAllText(SourcePathOf(definition));

        Assert.Contains(
            "DataCategory.RegulatoryFilingDocuments",
            source,
            StringComparison.Ordinal);

        // And the definition really produces it, not merely mention it in a comment.
        Assert.Contains(
            DataCategory.RegulatoryFilingDocuments,
            new SecEdgarSource().Definition(new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc)).Categories);
    }

    /// <summary>
    /// The provider now claims the capability, and claiming one grants nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This assertion was inverted at F4b, deliberately and visibly.</strong> At F4 the
    /// provider did not declare the category, and the test asserted that absence. F4b declares it,
    /// because a connector that cannot say "I know how to fetch this" cannot be exercised against a
    /// fake transport at all.
    /// </para>
    /// <para>
    /// <strong>The gate that matters did not move.</strong> A capability says the connector knows
    /// how; admission says the source is permitted to. The registered <c>sec-edgar</c> row still
    /// does not list this category, so every filing-document request is still refused before a
    /// connector is reached - asserted by the test below, which is now the one carrying the safety
    /// property.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_sec_provider_declares_the_capability_and_that_grants_nothing()
    {
        var source = File.ReadAllText(SourcePathOf(typeof(SecEdgarProvider)));

        Assert.Contains("DataCategory.RegulatoryFilingDocuments", source, StringComparison.Ordinal);

        // And the connector refuses the subject kind it does not serve, on the way out.
        Assert.Contains("FilingDocumentSubject.SubjectKind", source, StringComparison.Ordinal);
    }

    /// <summary>22, 23. The source roles F1a sealed are untouched by this capability.</summary>
    [Fact]
    public void No_source_alias_was_created_and_sec_edgar_remains_the_filing_source()
    {
        var endpoints = File.ReadAllText(SourcePathOf(typeof(SecEdgarEndpoints)));
        var subject = File.ReadAllText(SourcePathOf(typeof(FilingDocumentSubject)));

        foreach (var text in new[] { endpoints, subject })
        {
            Assert.DoesNotContain("\"eodhd\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("eodhd-family", text, StringComparison.Ordinal);
            Assert.DoesNotContain("eodhd-alias", text, StringComparison.Ordinal);
        }

        // The document path is an EDGAR archive path and names no vendor.
        Assert.StartsWith(
            "Archives/edgar/data/",
            SecEdgarEndpoints.FilingDocument(FilingDocumentSubject.Parse(Identifier()).Subject!),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing in the capability parses a document or names an identity concept.
    /// </summary>
    /// <remarks>
    /// F4 answers "can I retrieve and preserve this document?" and must not begin to answer "what
    /// did the issuer trade as?". A symbol, an interval or a corporate action appearing here would
    /// be extraction leaking into acquisition.
    /// </remarks>
    [Fact]
    public void The_capability_names_no_identity_or_corporate_action_concept()
    {
        var subject = File.ReadAllText(SourcePathOf(typeof(FilingDocumentSubject)));

        foreach (var forbidden in new[]
                 {
                     "SymbolChange", "SecurityIdentifier", "CorporateAction", "ValidFrom", "ValidTo",
                 })
        {
            Assert.DoesNotContain(forbidden + " ", subject, StringComparison.Ordinal);
        }
    }

    /// <summary>The repository file backing a type, found from the test's own location.</summary>
    private static string SourcePathOf(Type type)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
            !Directory.Exists(Path.Combine(directory.FullName, "src", "AI.Investment.Infrastructure")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "No repository root was found above " + AppContext.BaseDirectory);

        var matches = Directory.GetFiles(
            Path.Combine(directory!.FullName, "src"),
            type.Name + ".cs",
            SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        Assert.True(matches.Count == 1, $"Expected exactly one source file for {type.Name}, found {matches.Count}.");

        return matches[0];
    }
}
