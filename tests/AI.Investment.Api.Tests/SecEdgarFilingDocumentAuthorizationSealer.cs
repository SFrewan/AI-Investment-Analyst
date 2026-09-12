using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Seals the filing-document pilot authorisation, once, behind a deliberate switch.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a gated test rather than a script.</strong> The authorisation's digest must be
/// produced by the <c>acquisition-authorization@3</c> canonicalisation that
/// <see cref="AcquisitionAuthorization.Load"/> checks it against, not by a second implementation
/// that could drift from it. That code is here, so the sealer is here too - the same reasoning that
/// puts <see cref="AcquisitionAuthorization.DigestFor"/> beside the loader rather than in whatever
/// stage drafts a successor.
/// </para>
/// <para>
/// <strong>It is off unless asked.</strong> Like the pre-acquisition audit and the SEC batch runner,
/// it reads an environment variable and skips otherwise, so a normal suite run neither writes nor
/// overwrites a sealed file. Re-running it against an existing authorisation is refused rather than
/// silently regenerating one: a sealed artefact is evidence, and evidence that rewrites itself on a
/// test run is not evidence.
/// </para>
/// <para>
/// <strong>Sealing is not authorising.</strong> The file this writes grants no permission on its
/// own: every batch in <see cref="SecEdgarFilingDocumentPartition"/> is unauthorised, the
/// <c>sec-edgar</c> registry row does not admit <c>RegulatoryFilingDocuments</c>, and nothing here
/// consumes a unit or contacts anything.
/// </para>
/// </remarks>
public sealed class SecEdgarFilingDocumentAuthorizationSealer
{
    /// <summary>Set to <c>1</c> to seal. Absent on every ordinary run.</summary>
    public const string SealVariable = "AIINV_SEAL_FILING_AUTHORIZATION";

    private const string EvidenceBase = "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";
    private const string PilotSchema = "filing-document-pilot-declaration@1";
    private const string DigestField = "PilotDigest";
    private const string Vendor = "sec";

    /// <summary>The F5 seal, pinned. The declaration is verified against these, not measured into agreement.</summary>
    private const string PilotSha =
        "d545288b6970ea511bc16f1c05ca5638f2fe77d568d2686e408af1cc99899833";

    /// <inheritdoc cref="PilotSha"/>
    private const string PilotDigest =
        "c785f4ee4344cbcf9ec9b31c8ddb78ebabbb152a6504522a668169b6590bdb32";

    /// <summary>
    /// The instant this authorisation is sealed at, and the instant its validity begins.
    /// </summary>
    /// <remarks>
    /// <strong>A stated constant rather than the clock.</strong> A sealed artefact in this
    /// repository is byte-reproducible - regenerate it and you get the same file - and an
    /// authorisation whose digest covers "whenever this last ran" could not be. The instant is the
    /// day of sealing, written down and reviewable. <c>IClock</c> is what <em>judges</em> an
    /// authorisation at dispatch, which is a different question from what it was sealed at, and
    /// <see cref="AcquisitionAuthorization.IsValidAt"/> takes that instant from its caller.
    /// </remarks>
    internal static readonly DateTime IssuedAtUtc = new(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Exactly fourteen days, as F6a decided and F6b's loader permits.</summary>
    internal static readonly DateTime ExpiresAtUtc = IssuedAtUtc.AddDays(14);

    [SkippableFact]
    public void Seal_the_filing_document_pilot_authorisation()
    {
        Skip.IfNot(
            string.Equals(Environment.GetEnvironmentVariable(SealVariable), "1", StringComparison.Ordinal),
            $"Sealing is off. Set {SealVariable}=1 to seal the filing-document pilot authorisation. " +
            "It writes one file, contacts nothing and authorises no batch.");

        var declarationPath = Universe.RepositoryPath(
            "declarations", "sec-edgar-filing-document-pilot-2026-09.json");

        var authorizationPath = Universe.RepositoryPath(
            "declarations", SecEdgarFilingDocumentPartition.Declaration);

        Assert.False(
            File.Exists(authorizationPath),
            $"An authorisation already exists at {authorizationPath}. Sealing again would rewrite " +
            "evidence, so it is refused: delete it deliberately if it is genuinely to be replaced.");

        // ---- Part B. every check, before anything is written ------------------------------------

        var bytes = File.ReadAllBytes(declarationPath);

        // 2. whole-file SHA-256, against the F5 seal.
        Assert.Equal(PilotSha, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

        using var declaration = JsonDocument.Parse(bytes);

        var root = declaration.RootElement;

        // 5, 6. schema and path are the expected ones.
        Assert.Equal(PilotSchema, root.GetProperty("Schema").GetString());
        Assert.Equal(
            SecEdgarFilingDocumentPartition.PilotDeclarationPath,
            "declarations/" + Path.GetFileName(declarationPath));

        // 3, 4. the PilotDigest, recomputed from the declaration's own stated rule.
        Assert.Equal(PilotDigest, root.GetProperty(DigestField).GetString());
        Assert.Equal(PilotDigest, RecomputePilotDigest(root));

        var targets = root.GetProperty("Targets").EnumerateArray().ToList();

        // 7. exactly thirty.
        Assert.Equal(SecEdgarFilingDocumentPartition.PlannedRequests, targets.Count);

        // 9. every target carries the full F5 identity and nothing URL-shaped.
        var identifiers = new List<string>();

        foreach (var target in targets)
        {
            var cik = target.GetProperty("Cik").GetString()!;
            var accession = target.GetProperty("AccessionNumber").GetString()!;
            var document = target.GetProperty("PrimaryDocument").GetString()!;
            var identifier = target.GetProperty("SubjectIdentifier").GetString()!;

            Assert.Matches("^[0-9]{10}$", cik);
            Assert.Matches("^[0-9]{10}-[0-9]{2}-[0-9]{6}$", accession);
            Assert.False(string.IsNullOrWhiteSpace(document));
            Assert.Equal($"{cik}|{accession}|{document}", identifier);
            Assert.DoesNotContain("://", identifier, StringComparison.Ordinal);

            identifiers.Add(identifier);
        }

        // 10. no duplicates.
        Assert.Equal(identifiers.Count, identifiers.Distinct(StringComparer.Ordinal).Count());

        // 8. six members, five each.
        var byMember = identifiers
            .GroupBy(i => i.Split('|')[0], StringComparer.Ordinal)
            .ToList();

        Assert.Equal(6, byMember.Count);
        Assert.All(byMember, g => Assert.Equal(SecEdgarFilingDocumentPartition.PerBatch, g.Count()));

        // 11. no wildcard or dynamic scope.
        //
        // Asserted on the scope itself, not on the file's raw text. The declaration legitimately
        // says "an empty HTTP 200" when it carries the F4b empty-payload finding forward, and a
        // check that banned the word would fail on the very sentence that makes the pilot safe to
        // interpret. What matters is that the scope is a closed list of literals: the target fields
        // above carry no wildcard, no query character and no URL, and the three counts agree with
        // each other so there is nothing for a pattern to expand into.
        Assert.Equal(targets.Count, root.GetProperty("PlannedRequests").GetInt32());
        Assert.Equal(targets.Count, root.GetProperty("DispatchCeiling").GetInt32());
        Assert.Equal(SecEdgarFilingDocumentPartition.PerBatch, root.GetProperty("PerMemberCeiling").GetInt32());
        Assert.True(root.TryGetProperty("NoAutomaticExpansion", out _));

        // And the partition agrees with the declaration, batch by batch.
        Assert.Equal(
            identifiers,
            SecEdgarFilingDocumentPartition.Batches
                .SelectMany(b => SecEdgarFilingDocumentPartition.TargetsFor(b.Index))
                .ToList());

        // ---- Part C, D. expiry and ceiling -------------------------------------------------------

        Assert.True(IssuedAtUtc < ExpiresAtUtc);
        Assert.Equal(14, (ExpiresAtUtc - IssuedAtUtc).TotalDays);
        Assert.Equal(DateTimeKind.Utc, IssuedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, ExpiresAtUtc.Kind);
        Assert.Equal(SecEdgarFilingDocumentPartition.PlannedRequests, SecEdgarFilingDocumentPartition.Planned);

        // ---- Part F. the digest, from the real @3 canonicalisation --------------------------------

        var scope = new AcquisitionAuthorization.DocumentScope(
            SecEdgarFilingDocumentPartition.DataCategory,
            SecEdgarFilingDocumentPartition.SubjectKind,
            SecEdgarFilingDocumentPartition.PilotDeclarationPath,
            PilotSchema,
            DigestField,
            PilotDigest,
            PilotSha,
            IssuedAtUtc,
            ExpiresAtUtc);

        var digest = AcquisitionAuthorization.DigestForDocumentScoped(
            EvidenceBase,
            Vendor,
            [SecEdgarFilingDocumentPartition.Source],
            new DateOnly(2021, 9, 1),
            new DateOnly(2026, 8, 31),
            identifiers.Count,
            0,
            identifiers.Count,
            identifiers,
            scope);

        File.WriteAllText(authorizationPath, Compose(scope, identifiers, digest), new UTF8Encoding(false));

        // ---- Part F. reload from disk and verify everything again ---------------------------------

        var reloaded = AcquisitionAuthorization.Load(
            authorizationPath, EvidenceBase, Universe.RepositoryPath());

        Assert.Equal(digest, reloaded.Digest);
        Assert.Equal(AcquisitionAuthorization.DocumentScopedSchema, reloaded.SchemaVersion);
        Assert.Equal(30, reloaded.DispatchCeiling);
        Assert.Equal(30, reloaded.Symbols.Count);
        Assert.Equal(0, reloaded.Consumed);

        var reloadedScope = reloaded.Scope!;

        Assert.Equal(PilotSha, reloadedScope.DeclarationSha256);
        Assert.Equal(PilotDigest, reloadedScope.DeclarationDigest);
        Assert.Equal(IssuedAtUtc, reloadedScope.IssuedAtUtc);
        Assert.Equal(ExpiresAtUtc, reloadedScope.ExpiresAtUtc);
        Assert.True(reloaded.IsValidAt(IssuedAtUtc).Allowed);
        Assert.False(reloaded.IsValidAt(ExpiresAtUtc).Allowed);
        Assert.Null(reloaded.SupersedesAuthorizationId);
    }

    /// <summary>
    /// The declaration's <c>PilotDigest</c>, rebuilt from the rule the declaration itself states.
    /// </summary>
    /// <remarks>
    /// Recomputed rather than read, because the point of Part B is to establish that the file on
    /// disk is the one F5 sealed - and a digest copied out of the file it is supposed to protect
    /// establishes nothing.
    /// </remarks>
    private static string RecomputePilotDigest(JsonElement root)
    {
        var canonical = new List<string>
        {
            root.GetProperty("Schema").GetString()!,
            root.GetProperty("DeclarationId").GetString()!,
            root.GetProperty("Source").GetString()!,
            root.GetProperty("DataCategory").GetString()!,
            root.GetProperty("EvidenceBaseFingerprint").GetString()!,
            root.GetProperty("SelectionInputFingerprint").GetString()!,
            root.GetProperty("PlannedRequests").GetInt32().ToString(CultureInfo.InvariantCulture),
        };

        foreach (var t in root.GetProperty("Targets").EnumerateArray())
        {
            canonical.AddRange(
            [
                t.GetProperty("Cik").GetString()!,
                t.GetProperty("AccessionNumber").GetString()!,
                t.GetProperty("PrimaryDocument").GetString()!,
                t.GetProperty("Form").GetString()!,
                t.GetProperty("FilingDate").GetString()!,
            ]);
        }

        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', canonical))))
            .ToLowerInvariant();
    }

    /// <summary>
    /// The authorisation document, in the shape and order the repository's others use.
    /// </summary>
    /// <remarks>
    /// Two-space indent and CRLF, matching the sealed declaration it binds to, so the two files read
    /// alike in a diff. It carries no <c>SupersedesAuthorizationId</c> and no <c>AlreadyConsumed</c>:
    /// this is an original for a scope nothing has authorised before, and the repository's
    /// convention for an original is to omit those fields rather than to null them - which is what
    /// the sealed SEC submissions authorisation says of itself in the same words.
    /// </remarks>
    private static string Compose(
        AcquisitionAuthorization.DocumentScope scope,
        List<string> identifiers,
        string digest)
    {
        var document = new JsonObject
        {
            ["Schema"] = AcquisitionAuthorization.DocumentScopedSchema,
            ["Status"] = "SEALED - LOADABLE. NO BATCH IS AUTHORISED AND NOTHING HAS BEEN DISPATCHED.",
            ["StatusNote"] =
                "Sealed means this authorisation exists, binds to the F5 pilot declaration by path, "
                + "whole-file SHA-256 and PilotDigest, and loads. It does NOT mean a request may "
                + "leave. Dispatch additionally requires: a batch in SecEdgarFilingDocumentPartition "
                + "marked Authorised (every one is false), the sec-edgar registry row to declare "
                + "RegulatoryFilingDocuments (it does not), the connector enabled with a deployment "
                + "identity, and a deliberate operator act. Each is a separate decision and none is "
                + "performed by this file existing.",
            ["AuthorizationId"] = SecEdgarFilingDocumentPartition.AuthorizationId,
            ["EvidenceBaseFingerprint"] = EvidenceBase,
            ["Note"] =
                "A ceiling, not a licence. Thirty documents, six members, five each, every one named "
                + "in full. This authorisation consumes nothing from any other, supersedes nothing "
                + "and is superseded by nothing: it is an original for a category no authorisation "
                + "has covered before.",
            ["Purpose"] =
                "Permit the bounded retrieval of the thirty SEC filing documents the F5 declaration "
                + "names, so that a later stage may inspect them. It does not assume they contain a "
                + "symbol change, does not assert that a filing establishes ValidFrom or ValidTo, "
                + "and creates no historical ticker evidence.",
            ["Vendor"] = Vendor,
            ["Sources"] = new JsonArray(SecEdgarFilingDocumentPartition.Source),
            ["DataCategory"] = scope.DataCategory,
            ["DataCategoryNote"] =
                "Distinct from RegulatoryFilings, which serves the submissions index. That category "
                + "names documents; this one is the documents. The sec-edgar registry row does not "
                + "declare it, so admission refuses a filing-document request before any connector "
                + "is reached.",
            ["SubjectKind"] = scope.SubjectKind,
            ["SubjectIdentifier"] = "{cik}|{accessionNumber}|{primaryDocument}",
            ["SubjectIdentifierNote"] =
                "The exact string FilingDocumentSubject.Parse accepts. No URL, ticker, company name, "
                + "filename alone or accession alone is an identity here; the archive path is derived "
                + "from the identifier inside the connector and is deliberately not stated.",
            ["Region"] = "UnitedStates",
            ["DeclarationPath"] = scope.DeclarationPath,
            ["DeclarationSchema"] = scope.DeclarationSchema,
            ["DeclarationDigestField"] = scope.DeclarationDigestField,
            ["DeclarationDigest"] = scope.DeclarationDigest,
            ["DeclarationSha256"] = scope.DeclarationSha256,
            ["DeclarationBindingNote"] =
                "Three independent facts, all inside the canonical form and all recomputed from the "
                + "file on disk when this loads. The SHA-256 catches any byte change including prose; "
                + "the PilotDigest catches a change to the pilot scope specifically, because the "
                + "declaration's own digest rule excludes prose; the path makes a substituted file a "
                + "different binding. A superseding declaration is a different file with a different "
                + "SHA, so this authorisation cannot follow one.",
            ["IssuedAtUtc"] = scope.IssuedAtUtc.ToString(
                AcquisitionAuthorization.InstantFormat, CultureInfo.InvariantCulture),
            ["ExpiresAtUtc"] = scope.ExpiresAtUtc.ToString(
                AcquisitionAuthorization.InstantFormat, CultureInfo.InvariantCulture),
            ["ExpiryNote"] =
                "Fourteen days. Validity is IssuedAtUtc <= now < ExpiresAtUtc, exclusive at the far "
                + "end, judged against an IClock instant supplied by the runner before the "
                + "consumption boundary. Expiry bounds the right to cause an external request and "
                + "nothing else: evidence acquired while this was valid stays immutable, retrievable "
                + "and citable for ever. It is NOT WindowFromUtc/WindowToUtc, which are an evidence "
                + "scope, and NOT the targets' filing dates, which are facts about documents.",
            ["WindowFromUtc"] = "2021-09-01",
            ["WindowToUtc"] = "2026-08-31",
            ["WindowNote"] =
                "The sealed universe window, restated so this authorisation is scoped to the same "
                + "evidence base. It is NOT a request parameter and NOT an expiry: a filing-document "
                + "request carries no period at all, and this is passed to Covers() as literal "
                + "arguments and nowhere else.",
            ["PlannedRequests"] = identifiers.Count,
            ["AlreadySatisfied"] = 0,
            ["DispatchCeiling"] = identifiers.Count,
            ["BatchPartition"] =
                "Six batches of five, one per member, defined in SecEdgarFilingDocumentPartition and "
                + "derived from this declaration's own target order rather than reselected. Every "
                + "batch is unauthorised and each requires its own approval.",
            ["NoAutomaticExpansion"] =
                "The Symbols list is the whole scope. There is no wildcard, no pattern, no query "
                + "predicate and no provider-side discovery. A document referencing another grants no "
                + "authority to fetch it, and Covers refuses any subject not in the list before any "
                + "counting happens.",
            ["AuthorizationDigest"] = digest,
            ["DigestNote"] =
                "Computed by AcquisitionAuthorization.DigestForDocumentScoped, the same code "
                + "AcquisitionAuthorization.Load checks it against. The @3 canonical form is the @1 "
                + "prefix - schema, evidence base, vendor, sources, window, planned, satisfied, "
                + "ceiling, then each symbol - followed by the @3 tail: data category, subject kind, "
                + "declaration path, declaration schema, declaration digest field, declaration "
                + "digest, declaration SHA-256, issued instant, expiry instant.",
            ["Symbols"] = new JsonArray([.. identifiers.Select(i => (JsonNode)JsonValue.Create(i)!)]),
            ["SymbolsNote"] =
                "Subject identifiers, not tickers. EDGAR identifies a filing document by CIK, "
                + "accession number and primary document, so that triple is the subject identifier "
                + "here in the same way a ten-digit CIK is one for the submissions authorisation and "
                + "an EODHD symbol is one for the price authorisations. None was invented, "
                + "substituted or reformatted by hand: each is copied from the sealed declaration.",
        };

        var json = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        return json.ReplaceLineEndings("\r\n") + "\r\n";
    }
}
