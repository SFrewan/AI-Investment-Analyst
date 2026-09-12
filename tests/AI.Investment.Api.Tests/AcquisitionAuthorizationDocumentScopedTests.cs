using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The <c>acquisition-authorization@3</c> contract: bound to one sealed declaration, and expiring.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read-only against the repository, and no provider is reachable.</strong> Every test that
/// needs a mutated declaration writes a copy into a temporary directory; no declaration under
/// <c>declarations/</c> is edited, and none is created there. Nothing here opens a socket, touches
/// a database or constructs a gateway - the type under test has no way to do any of those, which is
/// itself asserted.
/// </para>
/// <para>
/// <strong>Failures are asserted by reason, not by type.</strong> Every refusal below throws
/// <see cref="InvalidOperationException"/>, so asserting the type would pass for the wrong reason
/// in nine cases out of ten. Each test names the phrase that identifies the refusal it means.
/// </para>
/// </remarks>
public sealed class AcquisitionAuthorizationDocumentScopedTests
{
    private const string EvidenceBase = "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private const string PilotRelative =
        "declarations/sec-edgar-filing-document-pilot-2026-09.json";

    private const string PilotFile = "sec-edgar-filing-document-pilot-2026-09.json";

    /// <summary>The F5 seal, pinned as a literal rather than recomputed into agreement with itself.</summary>
    private const string PilotSha =
        "d545288b6970ea511bc16f1c05ca5638f2fe77d568d2686e408af1cc99899833";

    /// <inheritdoc cref="PilotSha"/>
    private const string PilotDigest =
        "c785f4ee4344cbcf9ec9b31c8ddb78ebabbb152a6504522a668169b6590bdb32";

    private const string PilotSchema = "filing-document-pilot-declaration@1";
    private const string DigestField = "PilotDigest";
    private const string Category = "RegulatoryFilingDocuments";
    private const string SubjectKind = "FilingDocument";
    private const string Source = "sec-edgar";
    private const string Vendor = "sec";

    private static readonly DateOnly From = new(2021, 9, 1);
    private static readonly DateOnly To = new(2026, 8, 31);

    private static readonly DateTime Issued = new(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The pilot's own 14 days, well inside the loader's 30-day clamp.</summary>
    private static readonly DateTime Expires = Issued.AddDays(14);

    // ---- 1-4. the contract loads, and the old ones still do ------------------------------------

    /// <summary>1, 4. A well-formed @3 authorisation loads and its digest round-trips.</summary>
    [Fact]
    public void A_document_scoped_authorisation_loads_and_binds_to_the_sealed_declaration()
    {
        using var fixture = Fixture.Create();

        var authorization = fixture.Load();

        Assert.Equal(AcquisitionAuthorization.DocumentScopedSchema, authorization.SchemaVersion);
        Assert.True(authorization.IsDocumentScoped);

        var scope = Assert.IsType<AcquisitionAuthorization.DocumentScope>(authorization.Scope);

        Assert.Equal(Category, scope.DataCategory);
        Assert.Equal(SubjectKind, scope.SubjectKind);
        Assert.Equal(PilotRelative, scope.DeclarationPath);
        Assert.Equal(PilotSchema, scope.DeclarationSchema);
        Assert.Equal(DigestField, scope.DeclarationDigestField);
        Assert.Equal(PilotDigest, scope.DeclarationDigest);
        Assert.Equal(PilotSha, scope.DeclarationSha256);
        Assert.Equal(Issued, scope.IssuedAtUtc);
        Assert.Equal(Expires, scope.ExpiresAtUtc);

        // The sealed pilot's own numbers, carried through rather than restated.
        Assert.Equal(30, authorization.PlannedRequests);
        Assert.Equal(30, authorization.DispatchCeiling);
        Assert.Equal(30, authorization.Symbols.Count);
        Assert.Equal(0, authorization.AlreadySatisfied);

        // 4. The digest the loader computed is the one the file states, which is the round trip.
        Assert.Equal(fixture.Digest, authorization.Digest);
    }

    /// <summary>2, 3. Every authorisation already on disk loads, and its stored digest is unchanged.</summary>
    /// <remarks>
    /// <strong>The regression guard for the whole stage.</strong> @3 appends a tail to the canonical
    /// form, and the one way that could go wrong invisibly is by perturbing the @1 or @2 form. These
    /// five files were digested before @3 existed, so if any of them still recomputes to its stored
    /// value, the prefix is untouched.
    /// </remarks>
    [Theory]
    [InlineData("acquisition-eodhd-sample400.json", "acquisition-authorization@1")]
    [InlineData("acquisition-sec-edgar-gate6-six-members-2021-09-to-2026-08.json", "acquisition-authorization@1")]
    [InlineData("acquisition-eodhd-splits-sample400.json", "acquisition-authorization@2")]
    [InlineData("acquisition-eodhd-splits-remainder-2021-09-to-2026-08.json", "acquisition-authorization@2")]
    [InlineData("acquisition-eodhd-splits-final-2021-09-to-2026-08.json", "acquisition-authorization@2")]
    public void Every_existing_authorisation_still_loads_with_its_stored_digest(string file, string schema)
    {
        var path = Universe.RepositoryPath("declarations", file);

        var authorization = AcquisitionAuthorization.Load(path, EvidenceBase);

        Assert.Equal(schema, authorization.SchemaVersion);

        // Load throws unless the computed digest equals the stored one, so reaching here is the
        // assertion. Comparing to the file's own stated value says it out loud.
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        Assert.Equal(
            document.RootElement.GetProperty("AuthorizationDigest").GetString(),
            authorization.Digest,
            ignoreCase: true);

        // And none of them acquired a validity span or a declaration binding they never had.
        Assert.False(authorization.IsDocumentScoped);
        Assert.Null(authorization.Scope);
    }

    // ---- 5-8. the declaration binding -----------------------------------------------------------

    /// <summary>5. A declaration that changed by one byte no longer matches its SHA.</summary>
    /// <remarks>
    /// The byte changed is inside a prose field, chosen deliberately: it does not move the pilot's
    /// own digest, so this isolates the whole-file hash as the thing that caught it.
    /// </remarks>
    [Fact]
    public void A_declaration_changed_by_one_byte_is_refused()
    {
        using var fixture = Fixture.Create();

        fixture.MutateDeclaration(text => text.Replace(
            "\"Purpose\": \"Answer one question",
            "\"Purpose\": \"answer one question",
            StringComparison.Ordinal));

        var refusal = Assert.Throws<InvalidOperationException>(() => fixture.Load());

        Assert.Contains("hashes to", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("changed after the authorisation was approved", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>6. A declaration whose stated scope digest differs is refused.</summary>
    [Fact]
    public void A_declaration_whose_pilot_digest_changed_is_refused()
    {
        using var fixture = Fixture.Create();

        // Rewrite the declaration's own digest, then re-sign the authorisation against the new file
        // so the SHA matches. Only the scope binding can catch this, which is the point.
        fixture.MutateDeclaration(text => text.Replace(
            PilotDigest,
            new string('0', 64),
            StringComparison.Ordinal));

        fixture.ResignAgainstDeclarationBytes();

        var refusal = Assert.Throws<InvalidOperationException>(() => fixture.Load());

        Assert.Contains(DigestField, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("declared scope changed", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>7. An authorisation naming a declaration that is not there is refused.</summary>
    [Fact]
    public void A_declaration_at_another_path_is_refused()
    {
        using var fixture = Fixture.Create(declarationPath: "declarations/some-other-declaration.json");

        var refusal = Assert.Throws<InvalidOperationException>(() => fixture.Load());

        Assert.Contains("does not exist under the expected root", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>8. A declaration of another kind is a different document, whatever else matches.</summary>
    [Fact]
    public void A_declaration_of_another_schema_is_refused()
    {
        using var fixture = Fixture.Create();

        fixture.MutateDeclaration(text => text.Replace(
            $"\"Schema\": \"{PilotSchema}\"",
            "\"Schema\": \"security-identity-evidence@2\"",
            StringComparison.Ordinal));

        fixture.ResignAgainstDeclarationBytes();

        var refusal = Assert.Throws<InvalidOperationException>(() => fixture.Load());

        Assert.Contains("declares schema", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("a declaration of another", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The named digest field must actually be in the declaration.</summary>
    [Fact]
    public void A_declaration_without_the_named_digest_field_is_refused()
    {
        using var fixture = Fixture.Create(digestField: "NoSuchDigestField");

        var refusal = Assert.Throws<InvalidOperationException>(() => fixture.Load());

        Assert.Contains("carries no field", refusal.Message, StringComparison.Ordinal);
    }

    // ---- 9-10. every @3 field is inside the digest ----------------------------------------------

    /// <summary>9, 10, and the rest. Editing any digest-covered field after signing is refused.</summary>
    /// <remarks>
    /// One theory rather than nine tests, because the assertion is identical in every case and the
    /// value of the check is its coverage: each of these fields is claimed to be in the canonical
    /// form, and each row proves it by editing the file without re-signing it.
    /// </remarks>
    [Theory]
    [InlineData("DataCategory", "\"RegulatoryFilings\"")]
    [InlineData("SubjectKind", "\"Company\"")]
    [InlineData("DeclarationPath", "\"declarations/universe-sample400-2021-2026.json\"")]
    [InlineData("DeclarationSchema", "\"filing-document-pilot-declaration@2\"")]
    [InlineData("DeclarationDigestField", "\"IdentityDigest\"")]
    [InlineData("ExpiresAtUtc", "\"2026-10-10T00:00:00Z\"")]
    [InlineData("IssuedAtUtc", "\"2026-09-11T00:00:00Z\"")]
    [InlineData("DispatchCeiling", "31")]
    [InlineData("PlannedRequests", "31")]
    public void Editing_a_digest_covered_field_after_signing_is_refused(string field, string replacement)
    {
        using var fixture = Fixture.Create();

        fixture.MutateAuthorization(field, replacement);

        var refusal = Assert.Throws<InvalidOperationException>(() => fixture.Load());

        Assert.Contains("digest does not match its content", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>Changing a target is changing the scope, and the digest covers the whole list.</summary>
    [Fact]
    public void Editing_a_target_after_signing_is_refused()
    {
        using var fixture = Fixture.Create();

        var first = fixture.Symbols[0];

        fixture.MutateAuthorizationText(text => text.Replace(
            $"\"{first}\"",
            "\"0000320193|0000320193-24-000001|aapl-20240101.htm\"",
            StringComparison.Ordinal));

        var refusal = Assert.Throws<InvalidOperationException>(() => fixture.Load());

        Assert.Contains("digest does not match its content", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A @3 authorisation must carry every @3 field. An absent one is not a default.</summary>
    [Theory]
    [InlineData("DataCategory")]
    [InlineData("SubjectKind")]
    [InlineData("DeclarationPath")]
    [InlineData("DeclarationSchema")]
    [InlineData("DeclarationDigestField")]
    [InlineData("DeclarationDigest")]
    [InlineData("DeclarationSha256")]
    [InlineData("IssuedAtUtc")]
    [InlineData("ExpiresAtUtc")]
    public void A_document_scoped_authorisation_missing_a_required_field_is_refused(string field)
    {
        using var fixture = Fixture.Create();

        fixture.RemoveAuthorizationProperty(field);

        var refusal = Assert.Throws<InvalidOperationException>(() => fixture.Load());

        Assert.Contains(field, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("must carry", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>An older schema may not carry a field its own digest cannot protect.</summary>
    /// <remarks>
    /// The rule @1 has always applied to the @2 fields, now applied to @3's. It is the whole reason
    /// a third schema exists rather than nine more fields on the first one.
    /// </remarks>
    [Theory]
    [InlineData("ExpiresAtUtc")]
    [InlineData("IssuedAtUtc")]
    [InlineData("DeclarationSha256")]
    [InlineData("DeclarationDigest")]
    [InlineData("DeclarationPath")]
    [InlineData("DeclarationSchema")]
    [InlineData("DeclarationDigestField")]
    public void An_older_schema_carrying_a_document_scoped_field_is_refused(string field)
    {
        using var fixture = Fixture.Create();

        fixture.DowngradeToSchemaOneCarrying(field);

        var refusal = Assert.Throws<InvalidOperationException>(() => fixture.Load());

        Assert.Contains(field, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("digest cannot protect", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>SubjectKind</c> on an older schema is prose, and stays permitted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Four of the five authorisations on disk already carry it</strong> - the three splits
    /// successors and the sealed six-member SEC authorisation - as a descriptive field nothing
    /// reads. Refusing it would invalidate four approved declarations in order to tidy a field that
    /// claims nothing, which is exactly the retroactive breakage a new schema version exists to
    /// avoid.
    /// </para>
    /// <para>
    /// It is safe there because an older authorisation has no <c>Scope</c>, so there is no
    /// category or subject-kind check for an unprotected field to weaken. The fields that would
    /// read as a guarantee - the declaration binding and the validity instants - are refused
    /// instead, which is the test above.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("acquisition-eodhd-splits-sample400.json")]
    [InlineData("acquisition-sec-edgar-gate6-six-members-2021-09-to-2026-08.json")]
    public void An_older_schema_may_still_carry_subject_kind_as_prose(string file)
    {
        var path = Universe.RepositoryPath("declarations", file);

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        // The premise: these files really do carry it.
        Assert.True(document.RootElement.TryGetProperty("SubjectKind", out _));

        // And they still load, unchanged.
        var authorization = AcquisitionAuthorization.Load(path, EvidenceBase);

        Assert.False(authorization.IsDocumentScoped);
    }

    // ---- path safety ----------------------------------------------------------------------------

    /// <summary>A declaration path that escapes the root is refused even though it is signed.</summary>
    /// <remarks>
    /// The digest verifies before the path is resolved, so by this point the path is one that was
    /// signed rather than one that was merely written. Keeping it inside the root anyway costs
    /// nothing and means a binding can never name a file outside the repository it binds into.
    /// </remarks>
    [Theory]
    [InlineData("../outside-the-root.json")]
    [InlineData("declarations/../../outside-the-root.json")]
    public void A_declaration_path_that_escapes_the_root_is_refused(string escaping)
    {
        using var fixture = Fixture.Create(declarationPath: escaping);

        var refusal = Assert.Throws<InvalidOperationException>(() => fixture.Load());

        Assert.Contains("resolves outside the repository root", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>An absolute declaration path means whatever the machine happens to hold.</summary>
    [Fact]
    public void An_absolute_declaration_path_is_refused()
    {
        using var fixture = Fixture.Create(
            declarationPath: Path.Combine(Path.GetTempPath(), "absolute.json"));

        var refusal = Assert.Throws<InvalidOperationException>(() => fixture.Load());

        Assert.Contains("is absolute", refusal.Message, StringComparison.Ordinal);
    }

    // ---- 11-17. expiry --------------------------------------------------------------------------

    /// <summary>11. An authorisation is not yet valid before the instant it was issued at.</summary>
    [Fact]
    public void An_authorisation_is_refused_before_its_issue_instant()
    {
        using var fixture = Fixture.Create();

        var verdict = fixture.Load().IsValidAt(Issued.AddTicks(-1));

        Assert.False(verdict.Allowed);
        Assert.Contains(AcquisitionAuthorization.NotYetValidRule, verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>11. And it is valid at exactly the instant it was issued at.</summary>
    [Fact]
    public void An_authorisation_is_valid_at_its_issue_instant()
    {
        using var fixture = Fixture.Create();

        Assert.True(fixture.Load().IsValidAt(Issued).Allowed);
    }

    /// <summary>12, 13, 14. The far bound is exclusive, asserted on both sides of one tick.</summary>
    [Fact]
    public void Expiry_is_exclusive_to_the_tick()
    {
        using var fixture = Fixture.Create();

        var authorization = fixture.Load();

        // 14. One instant before: still valid.
        Assert.True(authorization.IsValidAt(Expires.AddTicks(-1)).Allowed);

        // 13. The exact instant: expired. Exclusive means the boundary belongs to the dead side.
        var atExpiry = authorization.IsValidAt(Expires);

        Assert.False(atExpiry.Allowed);
        Assert.Contains(AcquisitionAuthorization.ExpiredRule, atExpiry.Reason, StringComparison.Ordinal);

        // 12. And after.
        Assert.False(authorization.IsValidAt(Expires.AddTicks(1)).Allowed);
    }

    /// <summary>The instant an authorisation is judged at must be UTC, not converted into it.</summary>
    [Fact]
    public void A_non_utc_instant_is_refused_rather_than_converted()
    {
        using var fixture = Fixture.Create();

        var authorization = fixture.Load();

        Assert.Throws<ArgumentException>(
            () => authorization.IsValidAt(new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Local)));

        Assert.Throws<ArgumentException>(
            () => authorization.IsValidAt(new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Unspecified)));
    }

    /// <summary>An instant that is not in the one accepted form is refused rather than guessed at.</summary>
    [Theory]
    [InlineData("2026-09-26")]
    [InlineData("2026-09-26T00:00:00")]
    [InlineData("2026-09-26T00:00:00+02:00")]
    [InlineData("not an instant")]
    public void A_malformed_expiry_instant_is_refused(string written)
    {
        using var fixture = Fixture.Create();

        fixture.MutateAuthorization("ExpiresAtUtc", $"\"{written}\"");

        var refusal = Assert.Throws<InvalidOperationException>(() => fixture.Load());

        Assert.Contains("which is not an instant of the form", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>15. A lifetime past the loader's clamp is refused, however the file is signed.</summary>
    [Fact]
    public void An_authorisation_valid_for_longer_than_the_maximum_is_refused()
    {
        using var fixture = Fixture.Create(
            expires: Issued.AddDays(AcquisitionAuthorization.MaximumLifetimeDays + 1));

        var refusal = Assert.Throws<InvalidOperationException>(() => fixture.Load());

        Assert.Contains("above the maximum", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("permanent one with extra steps", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>15. The clamp is a boundary, and exactly the maximum is allowed.</summary>
    [Fact]
    public void An_authorisation_valid_for_exactly_the_maximum_is_accepted()
    {
        using var fixture = Fixture.Create(
            expires: Issued.AddDays(AcquisitionAuthorization.MaximumLifetimeDays));

        Assert.True(fixture.Load().IsDocumentScoped);
    }

    /// <summary>16. The pilot's own 14 days loads.</summary>
    [Fact]
    public void The_pilot_lifetime_of_fourteen_days_is_accepted()
    {
        using var fixture = Fixture.Create();

        var scope = fixture.Load().Scope!;

        Assert.Equal(14, (scope.ExpiresAtUtc - scope.IssuedAtUtc).TotalDays);
    }

    /// <summary>An authorisation that expires before, or exactly when, it begins is refused.</summary>
    [Fact]
    public void An_authorisation_that_does_not_outlive_its_issue_instant_is_refused()
    {
        using var fixture = Fixture.Create(expires: Issued);

        var refusal = Assert.Throws<InvalidOperationException>(() => fixture.Load());

        Assert.Contains("is not after IssuedAtUtc", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>17. A retry after expiry is refused, and it is refused before scope or ceiling.</summary>
    /// <remarks>
    /// The order matters for whoever reads the refusal. An expired authorisation is not "out of
    /// budget" and not "out of scope" - it is not an authorisation at that instant at all, and
    /// reporting it as either of the others sends the reader looking in the wrong place.
    /// </remarks>
    [Fact]
    public void A_retry_after_expiry_is_refused_and_consumes_nothing()
    {
        using var fixture = Fixture.Create();

        var authorization = fixture.Load();
        var target = fixture.Symbols[0];

        // Inside the window: allowed, and it costs one.
        Assert.True(authorization.TryConsume(Source, target, From, To, Issued.AddDays(1)).Allowed);
        Assert.Equal(1, authorization.Consumed);

        // The same act retried after expiry.
        var retry = authorization.TryConsume(Source, target, From, To, Expires.AddSeconds(1));

        Assert.False(retry.Allowed);
        Assert.Contains(AcquisitionAuthorization.ExpiredRule, retry.Reason, StringComparison.Ordinal);
        Assert.Contains("cannot be resumed", retry.Reason, StringComparison.Ordinal);

        // Nothing further was spent, so an expired retry cannot drain a ceiling either.
        Assert.Equal(1, authorization.Consumed);
        Assert.Equal(29, authorization.Remaining);
    }

    /// <summary>A partly spent authorisation does not resume once it has expired.</summary>
    [Fact]
    public void A_partly_consumed_authorisation_cannot_resume_after_expiry()
    {
        using var fixture = Fixture.Create();

        var authorization = fixture.Load();

        for (var i = 0; i < 5; i++)
        {
            Assert.True(authorization.TryConsume(Source, fixture.Symbols[i], From, To, Issued).Allowed);
        }

        Assert.Equal(25, authorization.Remaining);

        // Twenty-five units remain and none of them is reachable.
        var resumed = authorization.TryConsume(Source, fixture.Symbols[5], From, To, Expires);

        Assert.False(resumed.Allowed);
        Assert.Contains(AcquisitionAuthorization.ExpiredRule, resumed.Reason, StringComparison.Ordinal);
        Assert.Equal(25, authorization.Remaining);
    }

    /// <summary>A @3 authorisation cannot be consumed as though it were a standing one.</summary>
    /// <remarks>
    /// The overload without an instant has no way to check a validity span, so rather than silently
    /// treating an expiring authorisation as permanent it refuses to answer. This is the check that
    /// makes the expiry impossible to forget rather than merely available.
    /// </remarks>
    [Fact]
    public void A_document_scoped_authorisation_refuses_to_be_consumed_without_an_instant()
    {
        using var fixture = Fixture.Create();

        var refusal = Assert.Throws<InvalidOperationException>(
            () => fixture.Load().TryConsume(Source, fixture.Symbols[0], From, To));

        Assert.Contains("has a validity span", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("take that instant from IClock", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>18. Expiry bounds the right to act. It does not reach archived evidence.</summary>
    /// <remarks>
    /// <para>
    /// Asserted two ways, because the claim is a negative. First, behaviourally: after expiry the
    /// declaration binding still resolves and still verifies against the bytes on disk, so evidence
    /// acquired under the authorisation remains citable and its provenance chain remains readable.
    /// </para>
    /// <para>
    /// Second, structurally: the authorisation type has no member that could reach an archive, a
    /// payload or a run, so there is no code path by which expiry could invalidate, delete or
    /// rewrite anything. An authorisation is a permission to act, never a warrant over evidence.
    /// </para>
    /// </remarks>
    [Fact]
    public void Expiry_does_not_touch_archived_evidence()
    {
        using var fixture = Fixture.Create();

        var authorization = fixture.Load();

        Assert.False(authorization.IsValidAt(Expires.AddYears(1)).Allowed);

        // The binding still resolves and still verifies, long after the right to act has lapsed.
        var scope = authorization.Scope!;
        var bytes = File.ReadAllBytes(Path.Combine(fixture.Root, scope.DeclarationPath));

        Assert.Equal(
            scope.DeclarationSha256,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

        // And nothing on the type can reach evidence at all.
        var reaching = typeof(AcquisitionAuthorization)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Select(m => m.Name)
            .Where(n =>
                n.Contains("Archive", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Payload", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Delete", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Invalidate", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Revoke", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(reaching);
    }

    // ---- 19. the scope is exact ------------------------------------------------------------------

    /// <summary>19. The authorised set is thirty literal targets and cannot be widened.</summary>
    /// <remarks>
    /// There is no pattern to widen: <c>Covers</c> is a set-membership test over strings read from
    /// the file. These rows are the shapes a widening would have to take - a wildcard, a prefix, a
    /// bare accession, a bare CIK, a URL - and each is simply not in the set.
    /// </remarks>
    [Theory]
    [InlineData("*")]
    [InlineData("0000892482|*|*")]
    [InlineData("0000892482")]
    [InlineData("0001493152-23-003970")]
    [InlineData("form8-k.htm")]
    [InlineData("https://www.sec.gov/Archives/edgar/data/892482/000149315223003970/form8-k.htm")]
    [InlineData("0000320193|0000320193-24-000001|aapl-20240101.htm")]
    public void No_target_outside_the_sealed_thirty_is_covered(string attempt)
    {
        using var fixture = Fixture.Create();

        var verdict = fixture.Load().Covers(Source, attempt, From, To);

        Assert.False(verdict.Allowed);
        Assert.Contains("is not in the 30 this authorisation names", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>Each of the thirty sealed targets is covered, and there are exactly thirty.</summary>
    [Fact]
    public void Every_sealed_target_is_covered_and_there_are_exactly_thirty()
    {
        using var fixture = Fixture.Create();

        var authorization = fixture.Load();

        Assert.Equal(30, fixture.Symbols.Count);

        foreach (var target in fixture.Symbols)
        {
            Assert.True(authorization.Covers(Source, target, From, To).Allowed, target);

            // The F5 identity, not a URL and not a filename: three parts, in order.
            var parts = target.Split('|');

            Assert.Equal(3, parts.Length);
            Assert.Matches("^[0-9]{10}$", parts[0]);
            Assert.Matches("^[0-9]{10}-[0-9]{2}-[0-9]{6}$", parts[1]);
            Assert.DoesNotContain("://", target, StringComparison.Ordinal);
        }
    }

    /// <summary>A sealed target under the wrong category or subject kind is not covered.</summary>
    [Fact]
    public void A_sealed_target_under_another_category_or_subject_kind_is_refused()
    {
        using var fixture = Fixture.Create();

        var authorization = fixture.Load();
        var target = fixture.Symbols[0];

        var wrongCategory = authorization.Covers(Source, target, From, To, "RegulatoryFilings", SubjectKind);

        Assert.False(wrongCategory.Allowed);
        Assert.Contains("is not the authorised", wrongCategory.Reason, StringComparison.Ordinal);

        var wrongKind = authorization.Covers(Source, target, From, To, Category, "Company");

        Assert.False(wrongKind.Allowed);
        Assert.Contains("is not the authorised", wrongKind.Reason, StringComparison.Ordinal);

        // The right pair is still covered, so the refusals above are about the pair and not the target.
        Assert.True(authorization.Covers(Source, target, From, To, Category, SubjectKind).Allowed);
    }

    /// <summary>The wrong source is refused even when the target is one of the thirty.</summary>
    [Fact]
    public void Another_source_is_refused_for_a_sealed_target()
    {
        using var fixture = Fixture.Create();

        var verdict = fixture.Load().Covers("eodhd-eod", fixture.Symbols[0], From, To);

        Assert.False(verdict.Allowed);
        Assert.Contains("is not authorised", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>The ceiling is thirty and the thirty-first dispatch is refused.</summary>
    [Fact]
    public void The_ceiling_of_thirty_cannot_be_exceeded()
    {
        using var fixture = Fixture.Create();

        var authorization = fixture.Load();

        foreach (var target in fixture.Symbols)
        {
            Assert.True(authorization.TryConsume(Source, target, From, To, Issued).Allowed);
        }

        Assert.Equal(30, authorization.Consumed);
        Assert.Equal(0, authorization.Remaining);

        var thirtyFirst = authorization.TryConsume(Source, fixture.Symbols[0], From, To, Issued);

        Assert.False(thirtyFirst.Allowed);
        Assert.Contains("ceiling of 30 dispatches is reached", thirtyFirst.Reason, StringComparison.Ordinal);
    }

    // ---- 20. the safety seam ---------------------------------------------------------------------

    /// <summary>20. The authorisation cannot dispatch, archive, or reach a provider.</summary>
    /// <remarks>
    /// <para>
    /// <strong>@3 adds a gate; it does not become one.</strong> The safety seam is
    /// <c>ActionProposal → ActionGateway → PolicyEngine → kill switch → idempotency → audit</c>,
    /// and the way an authorisation could weaken it is by acquiring a way to act on its own. This
    /// asserts it has none: no HTTP, no gateway, no archive, no store, no clock of its own.
    /// </para>
    /// <para>
    /// The one instant-taking method takes it as a parameter, which is what keeps the clock a
    /// caller's <c>IClock</c> rather than a second source of "now" hidden in here.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_authorisation_can_reach_no_transport_gateway_or_store()
    {
        var type = typeof(AcquisitionAuthorization);

        var referenced = type
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .OfType<MethodBase>()
            .SelectMany(m => m.GetParameters())
            .Select(p => p.ParameterType.Name)
            .Concat(type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance).Select(f => f.FieldType.Name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        string[] forbidden =
        [
            "HttpClient", "HttpMessageHandler", "IIngestionGateway", "IngestionGateway",
            "IRawResponseArchive", "IIngestionRunStore", "IActionGateway", "IDataProvider",
            "IClock", "AppDbContext", "IDataAcquisition",
        ];

        foreach (var name in forbidden)
        {
            Assert.DoesNotContain(name, referenced, StringComparer.Ordinal);
        }

        // It cannot dispatch: nothing on it returns a run, a response or a payload.
        var returns = type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(m => m.ReturnType.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.DoesNotContain("IngestionRun", returns, StringComparer.Ordinal);
        Assert.DoesNotContain("ProviderResponse", returns, StringComparer.Ordinal);
        Assert.DoesNotContain("AcquisitionResult", returns, StringComparer.Ordinal);
    }

    /// <summary>An authorisation for another evidence base does not carry across a reseal.</summary>
    [Fact]
    public void An_authorisation_for_another_universe_is_refused()
    {
        using var fixture = Fixture.Create();

        var refusal = Assert.Throws<InvalidOperationException>(
            () => AcquisitionAuthorization.Load(
                fixture.AuthorizationPath,
                "us-pit-something-else@000000000000",
                fixture.Root));

        Assert.Contains("does not carry across a reseal", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An authorisation does not follow a superseded declaration, because it cannot see one.
    /// </summary>
    /// <remarks>
    /// The successor is written beside the original under a new name, which is how every
    /// supersession in this repository has been done - F1a's identity v2 was a new file and v1 was
    /// left byte-identical. The authorisation keeps naming the file it was signed against, so
    /// following the successor is impossible rather than merely discouraged. It keeps loading, and
    /// it keeps authorising the old scope, which is the correct and auditable outcome.
    /// </remarks>
    [Fact]
    public void An_authorisation_does_not_follow_a_superseded_declaration()
    {
        using var fixture = Fixture.Create();

        var successor = Path.Combine(fixture.Root, "declarations", "sec-edgar-filing-document-pilot-2026-10.json");

        File.WriteAllText(
            successor,
            File.ReadAllText(fixture.DeclarationFullPath)
                .Replace("sec-edgar-filing-document-pilot-2026-09", "sec-edgar-filing-document-pilot-2026-10", StringComparison.Ordinal));

        var authorization = fixture.Load();

        // Still bound to the original: same path, same SHA, same scope digest.
        Assert.Equal(PilotRelative, authorization.Scope!.DeclarationPath);
        Assert.Equal(PilotSha, authorization.Scope.DeclarationSha256);
        Assert.Equal(PilotDigest, authorization.Scope.DeclarationDigest);

        // And the successor, which exists and is different, was neither read nor followed.
        Assert.True(File.Exists(successor));

        Assert.NotEqual(
            PilotSha,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(successor))).ToLowerInvariant());
    }

    // ---- fixture ---------------------------------------------------------------------------------

    /// <summary>
    /// A temporary repository root holding a copy of the sealed pilot and a @3 authorisation for it.
    /// </summary>
    /// <remarks>
    /// <strong>The real declaration is copied, never edited.</strong> Mutation tests change the copy
    /// under a temporary root, so <c>declarations/</c> is read and nothing in it is written. The
    /// copy starts byte-identical, which is why the pinned F5 SHA is the right expectation for it.
    /// </remarks>
    private sealed class Fixture : IDisposable
    {
        /// <summary>Every field only a @3 authorisation may carry, mirrored for the downgrade test.</summary>
        private static readonly string[] DocumentScopedProperties =
        [
            "DataCategory", "SubjectKind", "DeclarationPath", "DeclarationSchema",
            "DeclarationDigestField", "DeclarationDigest", "DeclarationSha256",
            "IssuedAtUtc", "ExpiresAtUtc",
        ];

        private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

        private Fixture(string root, string declarationPath, IReadOnlyList<string> symbols, string digest)
        {
            Root = root;
            DeclarationRelative = declarationPath;
            Symbols = symbols;
            Digest = digest;
        }

        public string Root { get; }

        public string DeclarationRelative { get; }

        public IReadOnlyList<string> Symbols { get; }

        public string Digest { get; }

        public string AuthorizationPath => Path.Combine(Root, "declarations", "authorization.json");

        public string DeclarationFullPath => Path.Combine(Root, "declarations", PilotFile);

        public static Fixture Create(
            string declarationPath = PilotRelative,
            string digestField = DigestField,
            DateTime? issued = null,
            DateTime? expires = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "f6b-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(Path.Combine(root, "declarations"));

            var source = Universe.RepositoryPath("declarations", PilotFile);

            File.Copy(source, Path.Combine(root, "declarations", PilotFile));

            using var pilot = JsonDocument.Parse(File.ReadAllText(source));

            var symbols = pilot.RootElement
                .GetProperty("Targets")
                .EnumerateArray()
                .Select(t => t.GetProperty("SubjectIdentifier").GetString()!)
                .ToList();

            var issuedAt = issued ?? Issued;
            var expiresAt = expires ?? Expires;

            var scope = new AcquisitionAuthorization.DocumentScope(
                Category,
                SubjectKind,
                declarationPath,
                PilotSchema,
                digestField,
                PilotDigest,
                PilotSha,
                issuedAt,
                expiresAt);

            var digest = AcquisitionAuthorization.DigestForDocumentScoped(
                EvidenceBase, Vendor, [Source], From, To,
                symbols.Count, 0, symbols.Count, symbols, scope);

            var fixture = new Fixture(root, declarationPath, symbols, digest);

            fixture.WriteAuthorization(scope, digest);

            return fixture;
        }

        public AcquisitionAuthorization Load() =>
            AcquisitionAuthorization.Load(AuthorizationPath, EvidenceBase, Root);

        public void MutateDeclaration(Func<string, string> edit)
        {
            var path = DeclarationFullPath;

            File.WriteAllText(path, edit(File.ReadAllText(path)));
        }

        /// <summary>Re-signs the authorisation against the declaration's current bytes.</summary>
        /// <remarks>
        /// Used only where a test must isolate the scope binding from the whole-file hash: without
        /// this the SHA would refuse first and the test would pass for the wrong reason.
        /// </remarks>
        public void ResignAgainstDeclarationBytes()
        {
            var bytes = File.ReadAllBytes(DeclarationFullPath);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            // The SHA is refreshed and NOTHING else is. Re-signing the schema or the scope digest
            // too would make the authorisation agree with whatever the declaration had become,
            // which is the opposite of what these tests are for: the point is to isolate one
            // binding by satisfying the others.
            var scope = new AcquisitionAuthorization.DocumentScope(
                Category, SubjectKind, DeclarationRelative, PilotSchema, DigestField, PilotDigest, sha,
                Issued, Expires);

            var digest = AcquisitionAuthorization.DigestForDocumentScoped(
                EvidenceBase, Vendor, [Source], From, To,
                Symbols.Count, 0, Symbols.Count, Symbols, scope);

            WriteAuthorization(scope, digest);
        }

        public void MutateAuthorization(string property, string replacementJson) =>
            MutateAuthorizationText(text => ReplaceProperty(text, property, replacementJson));

        public void MutateAuthorizationText(Func<string, string> edit) =>
            File.WriteAllText(AuthorizationPath, edit(File.ReadAllText(AuthorizationPath)));

        public void RemoveAuthorizationProperty(string property) =>
            MutateAuthorizationText(text =>
            {
                var node = JsonNodeFor(text);

                node.Remove(property);

                return Serialize(node);
            });

        /// <summary>Rewrites the file as a @1 authorisation still carrying one @3 field.</summary>
        public void DowngradeToSchemaOneCarrying(string keptField) =>
            MutateAuthorizationText(text =>
            {
                var node = JsonNodeFor(text);

                foreach (var property in DocumentScopedProperties)
                {
                    if (!string.Equals(property, keptField, StringComparison.Ordinal))
                    {
                        node.Remove(property);
                    }
                }

                node["Schema"] = AcquisitionAuthorization.Schema;

                return Serialize(node);
            });

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not a test failure.
            }
        }

        private void WriteAuthorization(AcquisitionAuthorization.DocumentScope scope, string digest)
        {
            var document = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["Schema"] = AcquisitionAuthorization.DocumentScopedSchema,
                ["AuthorizationId"] = "f6b-test-authorization",
                ["EvidenceBaseFingerprint"] = EvidenceBase,
                ["Vendor"] = Vendor,
                ["Sources"] = new[] { Source },
                ["DataCategory"] = scope.DataCategory,
                ["SubjectKind"] = scope.SubjectKind,
                ["DeclarationPath"] = scope.DeclarationPath,
                ["DeclarationSchema"] = scope.DeclarationSchema,
                ["DeclarationDigestField"] = scope.DeclarationDigestField,
                ["DeclarationDigest"] = scope.DeclarationDigest,
                ["DeclarationSha256"] = scope.DeclarationSha256,
                ["IssuedAtUtc"] = scope.IssuedAtUtc.ToString(
                    AcquisitionAuthorization.InstantFormat, CultureInfo.InvariantCulture),
                ["ExpiresAtUtc"] = scope.ExpiresAtUtc.ToString(
                    AcquisitionAuthorization.InstantFormat, CultureInfo.InvariantCulture),
                ["WindowFromUtc"] = From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["WindowToUtc"] = To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["PlannedRequests"] = Symbols.Count,
                ["AlreadySatisfied"] = 0,
                ["DispatchCeiling"] = Symbols.Count,
                ["AuthorizationDigest"] = digest,
                ["Symbols"] = Symbols,
            };

            File.WriteAllText(
                AuthorizationPath,
                JsonSerializer.Serialize(document, Indented));
        }

        private static System.Text.Json.Nodes.JsonObject JsonNodeFor(string text) =>
            System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();

        private static string Serialize(System.Text.Json.Nodes.JsonObject node) =>
            node.ToJsonString(Indented);

        private static string ReplaceProperty(string text, string property, string replacementJson)
        {
            var node = JsonNodeFor(text);

            node[property] = System.Text.Json.Nodes.JsonNode.Parse(replacementJson);

            return Serialize(node);
        }
    }
}
