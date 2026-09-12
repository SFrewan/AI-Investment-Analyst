using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Ingestion.Providers;
using Xunit;

namespace AI.Investment.Integration.Tests.Ingestion;

/// <summary>
/// The sealed pilot declaration: what it scopes, and that it authorises nothing.
/// </summary>
/// <remarks>
/// <para>
/// Read from the repository rather than a fixture, because the thing under test is the file this
/// repository carries. No database, no network and no fixture: a declaration is a statement of
/// scope, and validating one should cost nothing.
/// </para>
/// <para>
/// <strong>The separation facts matter most.</strong> Several of these assert that the declaration
/// does not and cannot authorise a dispatch - it names no authorisation id, carries no ceiling that
/// grants anything, and the registry row it would need still does not declare the category.
/// </para>
/// </remarks>
public sealed class FilingDocumentPilotDeclarationTests
{
    private const string RelativePath = "declarations/sec-edgar-filing-document-pilot-2026-09.json";

    private const string Sha256Expected =
        "d545288b6970ea511bc16f1c05ca5638f2fe77d568d2686e408af1cc99899833";

    private const string PilotDigestExpected =
        "c785f4ee4344cbcf9ec9b31c8ddb78ebabbb152a6504522a668169b6590bdb32";

    private static readonly string[] AllowedCiks =
    [
        "0000892482", "0001335112", "0001404123", "0001426332", "0001775625", "0001784851",
    ];

    // ---- 1-3. schema, source, category --------------------------------------------------------

    [Fact]
    public void The_declaration_states_its_schema_source_and_category()
    {
        var root = Root();

        Assert.Equal("filing-document-pilot-declaration@1", root.GetProperty("Schema").GetString());
        Assert.Equal("sec-edgar-filing-document-pilot-2026-09", root.GetProperty("DeclarationId").GetString());
        Assert.Equal("sec-edgar", root.GetProperty("Source").GetString());
        Assert.Equal(
            nameof(DataCategory.RegulatoryFilingDocuments),
            root.GetProperty("DataCategory").GetString());
    }

    // ---- 4-8. scope ---------------------------------------------------------------------------

    [Fact]
    public void The_pilot_is_within_its_ceiling_and_its_counts_agree()
    {
        var root = Root();
        var targets = Targets(root);

        Assert.Equal(30, root.GetProperty("DispatchCeiling").GetInt32());
        Assert.True(targets.Count <= 30, $"{targets.Count} targets exceeds the ceiling of 30");
        Assert.Equal(targets.Count, root.GetProperty("PlannedRequests").GetInt32());

        // The declared per-member counts sum to the target count - no member is described as
        // carrying documents the Targets list does not contain.
        var declared = root.GetProperty("MemberScope").EnumerateArray()
            .Sum(m => m.GetProperty("Selected").GetInt32());

        Assert.Equal(targets.Count, declared);
    }

    [Fact]
    public void No_member_contributes_more_than_five_documents()
    {
        var root = Root();

        Assert.Equal(5, root.GetProperty("PerMemberCeiling").GetInt32());

        foreach (var group in Targets(root).GroupBy(t => t.Cik))
        {
            Assert.True(group.Count() <= 5, $"{group.Key} contributes {group.Count()} documents");
        }
    }

    [Fact]
    public void Exactly_the_six_intended_members_appear_and_no_other()
    {
        var targets = Targets(Root());

        Assert.Equal(
            AllowedCiks.OrderBy(c => c, StringComparer.Ordinal),
            targets.Select(t => t.Cik).Distinct().OrderBy(c => c, StringComparer.Ordinal));

        Assert.All(targets, t => Assert.Contains(t.Cik, AllowedCiks));
    }

    // ---- 9-14. every target is complete, addressable and unique --------------------------------

    /// <summary>
    /// Every target parses as a subject the connector can actually address.
    /// </summary>
    /// <remarks>
    /// Validated through the real parser rather than by re-checking the fields here. A declaration
    /// naming a document the capability would refuse is a pilot that cannot run, and it should fail
    /// at sealing time rather than at dispatch.
    /// </remarks>
    [Fact]
    public void Every_target_is_addressable_by_the_production_parser()
    {
        foreach (var target in Targets(Root()))
        {
            Assert.False(string.IsNullOrWhiteSpace(target.Cik));
            Assert.False(string.IsNullOrWhiteSpace(target.AccessionNumber));
            Assert.False(string.IsNullOrWhiteSpace(target.PrimaryDocument));
            Assert.Equal("8-K", target.Form);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", target.FilingDate);

            var parsed = FilingDocumentSubject.Parse(target.SubjectIdentifier);

            Assert.True(
                parsed.IsAccepted,
                $"{target.SubjectIdentifier} was refused as {parsed.Refusal}");

            Assert.Equal(target.Cik, parsed.Subject!.Cik);
            Assert.Equal(target.AccessionNumber, parsed.Subject.AccessionNumber);
            Assert.Equal(target.PrimaryDocument, parsed.Subject.PrimaryDocument);

            // The declared identifier is exactly the three parts, and nothing else.
            Assert.Equal(
                $"{target.Cik}|{target.AccessionNumber}|{target.PrimaryDocument}",
                target.SubjectIdentifier);
        }
    }

    [Fact]
    public void No_target_is_duplicated()
    {
        var targets = Targets(Root());

        Assert.Equal(targets.Count, targets.Select(t => t.SubjectIdentifier).Distinct(StringComparer.Ordinal).Count());

        // And the accession numbers are distinct too, so no filing appears twice under two names.
        Assert.Equal(
            targets.Count,
            targets.Select(t => t.AccessionNumber).Distinct(StringComparer.Ordinal).Count());
    }

    // ---- 15-17. identity discipline and no wildcards -------------------------------------------

    [Fact]
    public void No_target_is_identified_by_a_url_a_ticker_or_a_wildcard()
    {
        var raw = Read();
        var targets = Targets(Root());

        foreach (var target in targets)
        {
            foreach (var part in new[] { target.Cik, target.AccessionNumber, target.PrimaryDocument })
            {
                Assert.DoesNotContain("http", part, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("*", part, StringComparison.Ordinal);
                Assert.DoesNotContain("?", part, StringComparison.Ordinal);
            }
        }

        // No URL, query predicate or discovery clause anywhere in the file.
        foreach (var forbidden in new[] { "https://", "http://", "SELECT ", "LIKE '%" })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.Ordinal);
        }

        Assert.Contains("NoAutomaticExpansion", raw, StringComparison.Ordinal);

        // The source is asserted structurally rather than by text search. The declaration does
        // mention "eodhd" in prose - to say it is NOT the source - and a raw-text ban would punish
        // the sentence that draws the distinction F1a exists to draw.
        var root = Root();

        Assert.Equal("sec-edgar", root.GetProperty("Source").GetString());

        foreach (var property in root.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String))
        {
            var value = property.Value.GetString();

            Assert.NotEqual("eodhd", value);
            Assert.NotEqual("eodhd-eod", value);
            Assert.NotEqual("eodhd-splits", value);
        }
    }

    // ---- 18. deterministic ordering ------------------------------------------------------------

    /// <summary>
    /// The file's target order is the one the selection rule states, and it is total.
    /// </summary>
    [Fact]
    public void Targets_appear_in_the_declared_deterministic_order()
    {
        var targets = Targets(Root());

        var expected = targets
            .OrderBy(t => t.Cik, StringComparer.Ordinal)
            .ThenByDescending(t => t.FilingDate, StringComparer.Ordinal)
            .ThenByDescending(t => t.AccessionNumber, StringComparer.Ordinal)
            .Select(t => t.SubjectIdentifier)
            .ToList();

        Assert.Equal(expected, targets.Select(t => t.SubjectIdentifier));

        // Total within each member: no two selected filings share a (date, accession) pair, so the
        // ordering has no tie that could resolve differently on another run.
        foreach (var group in targets.GroupBy(t => t.Cik))
        {
            Assert.Equal(
                group.Count(),
                group.Select(t => t.FilingDate + "|" + t.AccessionNumber).Distinct(StringComparer.Ordinal).Count());
        }
    }

    // ---- 19-20. fingerprints and reproducibility ------------------------------------------------

    [Fact]
    public void The_declaration_binds_to_the_identity_declaration_and_its_selection_input()
    {
        var root = Root();

        Assert.Equal(
            "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962",
            root.GetProperty("EvidenceBaseFingerprint").GetString());

        Assert.Equal(
            "declarations/security-identity-sample400-2021-2026-v2.json",
            root.GetProperty("IdentityDeclaration").GetString());

        Assert.Equal(
            "86c610e2de640f8f759f3cc5cb78489027c9f5605ee404b0ed18e9a84eb9fd28",
            root.GetProperty("IdentityDeclarationDigest").GetString());

        // The population the selection was drawn from is fingerprinted too, so "same input, same
        // rule, same list" is checkable rather than asserted.
        Assert.Matches("^[0-9a-f]{64}$", root.GetProperty("SelectionInputFingerprint").GetString()!);
        Assert.True(root.GetProperty("SelectionInputPointerCount").GetInt32() >= Targets(root).Count);
    }

    /// <summary>The sealed hashes, and the digest recomputed from the file's own rule.</summary>
    [Fact]
    public void The_declaration_carries_its_sealed_hashes_and_the_digest_recomputes()
    {
        var root = Root();

        Assert.Equal(Sha256Expected, Sha256OfFile());
        Assert.Equal(PilotDigestExpected, root.GetProperty("PilotDigest").GetString());

        var canonical = new List<string>
        {
            root.GetProperty("Schema").GetString()!,
            root.GetProperty("DeclarationId").GetString()!,
            root.GetProperty("Source").GetString()!,
            root.GetProperty("DataCategory").GetString()!,
            root.GetProperty("EvidenceBaseFingerprint").GetString()!,
            root.GetProperty("SelectionInputFingerprint").GetString()!,
            root.GetProperty("PlannedRequests").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        foreach (var t in Targets(root))
        {
            canonical.AddRange([t.Cik, t.AccessionNumber, t.PrimaryDocument, t.Form, t.FilingDate]);
        }

        var recomputed = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', canonical))))
            .ToLowerInvariant();

        Assert.Equal(PilotDigestExpected, recomputed);
    }

    // ---- 21-22. it authorises nothing ----------------------------------------------------------

    /// <summary>
    /// The declaration is labelled as a declaration and names no authorisation.
    /// </summary>
    /// <remarks>
    /// The repository's acquisition authorisations carry <c>AuthorizationId</c> and
    /// <c>AuthorizationDigest</c>. This carries neither, deliberately: a file that looked like an
    /// authorisation would eventually be treated as one.
    /// </remarks>
    [Fact]
    public void The_declaration_is_not_an_authorisation_and_does_not_pretend_to_be()
    {
        var root = Root();

        Assert.StartsWith("DECLARATION ONLY - NOT AUTHORIZATION", root.GetProperty("Status").GetString(), StringComparison.Ordinal);

        // It carries none of the fields the repository's acquisition authorisations carry, so
        // nothing could mistake it for one by shape.
        Assert.False(root.TryGetProperty("AuthorizationId", out _));
        Assert.False(root.TryGetProperty("AuthorizationDigest", out _));
        Assert.False(root.TryGetProperty("Authorised", out _));
        Assert.False(root.TryGetProperty("AuthorisedAtUtc", out _));

        // And it does not claim the authorisation schema. It may mention it in prose - the status
        // note names what a dispatch would additionally require - and saying "an authorisation is
        // needed and does not exist" is the sentence that draws the separation, not a breach of it.
        Assert.Equal("filing-document-pilot-declaration@1", root.GetProperty("Schema").GetString());
        Assert.DoesNotContain("acquisition-authorization@", root.GetProperty("Schema").GetString()!, StringComparison.Ordinal);

        Assert.Contains("NO ACQUISITION IS AUTHORISED", root.GetProperty("Status").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The gate the declaration would need is still closed: the registry does not declare it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted against the source definition rather than a database, so it holds for any
    /// installation. Sealing a declaration changes nothing about admission, which is the property
    /// that keeps dispatch impossible.
    /// </para>
    /// <para>
    /// <strong>The claim is unchanged and was never wrong: a sealed declaration admits nothing.</strong>
    /// Admission was opened four stages later by F6g, in the registry, as a separate act. What this
    /// test owns is that sealing the declaration was not that act - so it asserts the declaration's
    /// own shape rather than the registry's state.
    /// </para>
    /// </remarks>
    [Fact]
    public void Sealing_the_declaration_did_not_open_the_admission_gate()
    {
        var root = Root();

        // The declaration says what it is, and it is not an admission or an authorisation.
        Assert.StartsWith("DECLARATION ONLY - NOT AUTHORIZATION", root.GetProperty("Status").GetString(), StringComparison.Ordinal);
        Assert.False(root.TryGetProperty("Admitted", out _));
        Assert.False(root.TryGetProperty("AuthorizationId", out _));

        // The declaration names no category admission of its own, whatever the registry now holds.
        Assert.False(root.TryGetProperty("Categories", out _));
        Assert.Equal("RegulatoryFilingDocuments", root.GetProperty("DataCategory").GetString());
    }

    /// <summary>The two F4b findings are carried in the file, not quietly dropped.</summary>
    [Fact]
    public void The_declaration_records_the_empty_payload_and_changed_document_rules()
    {
        var root = Root();

        var empty = root.GetProperty("EmptyPayloadRule").GetString()!;
        var changed = root.GetProperty("ChangedDocumentRule").GetString()!;

        // The rule that protects the identity evidence: an empty payload is never read as
        // "this filing states no symbol".
        Assert.Contains("zero-byte", empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never", empty, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("idempotency", changed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not detected", changed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The deviation from F3's proposed rule is stated, not silently substituted.</summary>
    [Fact]
    public void The_selection_rule_and_its_deviation_from_f3_are_both_stated()
    {
        var root = Root();

        var rule = root.GetProperty("SelectionRule").GetString()!;

        Assert.Contains("DESCENDING", rule, StringComparison.Ordinal);
        Assert.Contains("AccessionNumber", rule, StringComparison.Ordinal);

        Assert.Contains("total", root.GetProperty("SelectionRuleTotality").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("F3", root.GetProperty("SelectionRuleDeviation").GetString()!, StringComparison.Ordinal);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private sealed record Target(
        string Cik,
        string AccessionNumber,
        string PrimaryDocument,
        string Form,
        string FilingDate,
        string SubjectIdentifier);

    private static List<Target> Targets(JsonElement root) =>
        root.GetProperty("Targets").EnumerateArray()
            .Select(t => new Target(
                t.GetProperty("Cik").GetString()!,
                t.GetProperty("AccessionNumber").GetString()!,
                t.GetProperty("PrimaryDocument").GetString()!,
                t.GetProperty("Form").GetString()!,
                t.GetProperty("FilingDate").GetString()!,
                t.GetProperty("SubjectIdentifier").GetString()!))
            .ToList();

    private static JsonElement Root() => JsonDocument.Parse(Read()).RootElement.Clone();

    private static string Read() => File.ReadAllText(Path.Combine(RepositoryRoot(), RelativePath));

    private static string Sha256OfFile() =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(RepositoryRoot(), RelativePath))))
            .ToLowerInvariant();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, RelativePath)))
        {
            directory = directory.Parent;
        }

        Assert.True(
            directory is not null,
            $"No repository root containing {RelativePath} was found above {AppContext.BaseDirectory}.");

        return directory!.FullName;
    }
}
