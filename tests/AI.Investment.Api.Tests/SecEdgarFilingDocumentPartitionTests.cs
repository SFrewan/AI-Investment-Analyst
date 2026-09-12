using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Ingestion.Providers;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The sealed filing-document pilot authorisation, and the six unauthorised batches it covers.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read-only, and nothing here can dispatch.</strong> No provider is constructed, no socket
/// is opened and no unit is consumed against the sealed file. Where a test needs a mutated
/// authorisation or declaration it copies both into a temporary directory and mutates the copy, so
/// <c>declarations/</c> is read and never written.
/// </para>
/// <para>
/// <strong>Sealing is not authorising.</strong> Every assertion about the authorisation is paired
/// with one about what still stands between it and the wire: every batch unauthorised, the registry
/// row silent about the category, and the scope a closed list of thirty literals.
/// </para>
/// </remarks>
public sealed class SecEdgarFilingDocumentPartitionTests
{
    private const string EvidenceBase = "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private const string PilotFile = "sec-edgar-filing-document-pilot-2026-09.json";

    private const string PilotSha =
        "d545288b6970ea511bc16f1c05ca5638f2fe77d568d2686e408af1cc99899833";

    private const string PilotDigest =
        "c785f4ee4344cbcf9ec9b31c8ddb78ebabbb152a6504522a668169b6590bdb32";

    /// <summary>The digest sealed by F6c, pinned rather than recomputed into agreement with itself.</summary>
    private const string AuthorizationDigest =
        "f927ab5d5059a25379a20fd731efece5c192f0d6aba7d255eb5ac038d3d07a3b";

    private static readonly DateTime Issued = new(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Expires = new(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Every authorisation that existed before F6c, with the bytes it had.</summary>
    private static readonly (string File, string Sha)[] PreExisting =
    [
        ("acquisition-eodhd-sample400.json",
            "b0587ff16aebe5f6a420615e538e5bafa0ce9f324fe700f0ad4b5136863e6335"),
        ("acquisition-eodhd-splits-final-2021-09-to-2026-08.json",
            "2f655465a5458ebbe8960ce76cb6c7588efe8d8d538af572667271caee48f397"),
        ("acquisition-eodhd-splits-remainder-2021-09-to-2026-08.json",
            "145a7aff6536419fd7ef7988f7c7918ae270f39668d412544708387b898e05ab"),
        ("acquisition-eodhd-splits-sample400.json",
            "3156568091065eaffee2ed0b2530caf8e98e5fea2367713003cd1f0beeeb5ec0"),
        ("acquisition-sec-edgar-gate6-six-members-2021-09-to-2026-08.json",
            "f19b06f03e0ed78dade748a199d711b27c59aee3da2db6ef1b944778e631feed"),
    ];

    // ---- 1-3. exactly one, and it loads ----------------------------------------------------------

    /// <summary>1. Exactly one <c>@3</c> authorisation exists in the repository.</summary>
    [Fact]
    public void Exactly_one_document_scoped_authorisation_exists()
    {
        var documentScoped = Directory
            .EnumerateFiles(Universe.RepositoryPath("declarations"), "*.json")
            .Where(IsDocumentScoped)
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal([SecEdgarFilingDocumentPartition.Declaration], documentScoped);
    }

    /// <summary>2, 3. It loads, and the digest it states is the digest its content produces.</summary>
    [Fact]
    public void The_authorisation_loads_and_its_digest_round_trips()
    {
        var authorization = Load();

        Assert.Equal(AcquisitionAuthorization.DocumentScopedSchema, authorization.SchemaVersion);
        Assert.Equal(SecEdgarFilingDocumentPartition.AuthorizationId, authorization.AuthorizationId);

        // Load refuses unless the computed digest equals the stated one, so reaching here is the
        // round trip. Comparing against the pinned value says which digest it is.
        Assert.Equal(AuthorizationDigest, authorization.Digest);
        Assert.Equal(AuthorizationDigest, Stated("AuthorizationDigest"), ignoreCase: true);
    }

    // ---- 4-9. the binding ------------------------------------------------------------------------

    /// <summary>4-9. Every binding field is the F5 value, and the category pair is the F4/F5 one.</summary>
    [Fact]
    public void The_authorisation_binds_to_the_sealed_declaration_exactly()
    {
        var scope = Load().Scope!;

        Assert.Equal(PilotSha, scope.DeclarationSha256);
        Assert.Equal(PilotDigest, scope.DeclarationDigest);
        Assert.Equal("PilotDigest", scope.DeclarationDigestField);
        Assert.Equal("declarations/" + PilotFile, scope.DeclarationPath);
        Assert.Equal("filing-document-pilot-declaration@1", scope.DeclarationSchema);

        // Taken from the production enum and the production parser, not retyped as literals.
        Assert.Equal(nameof(DataCategory.RegulatoryFilingDocuments), scope.DataCategory);
        Assert.Equal(FilingDocumentSubject.SubjectKind, scope.SubjectKind);

        // And the declaration on disk really is the one those hashes name.
        var bytes = File.ReadAllBytes(Universe.RepositoryPath("declarations", PilotFile));

        Assert.Equal(PilotSha, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

        using var declaration = JsonDocument.Parse(bytes);

        Assert.Equal(PilotDigest, declaration.RootElement.GetProperty("PilotDigest").GetString());
    }

    // ---- 10-15. the scope ------------------------------------------------------------------------

    /// <summary>10, 13, 14, 15. Thirty targets, unique, and exactly the declaration's.</summary>
    [Fact]
    public void The_thirty_authorised_targets_are_exactly_the_declaration_targets()
    {
        var authorization = Load();

        Assert.Equal(30, authorization.Symbols.Count);
        Assert.Equal(30, authorization.DispatchCeiling);
        Assert.Equal(30, authorization.PlannedRequests);
        Assert.Equal(0, authorization.AlreadySatisfied);

        var declared = DeclarationTargets();

        Assert.Equal(30, declared.Count);

        // 13. Unique on both sides.
        Assert.Equal(30, declared.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(30, authorization.Symbols.Distinct(StringComparer.Ordinal).Count());

        // 14, 15. Set equality in both directions: nothing missing, nothing extra.
        Assert.Empty(declared.Except(authorization.Symbols, StringComparer.Ordinal));
        Assert.Empty(authorization.Symbols.Except(declared, StringComparer.Ordinal));
    }

    /// <summary>16. The scope is a closed list of literals with no way to widen it.</summary>
    /// <remarks>
    /// Asserted on the scope, not on the file's prose. The authorisation legitimately explains that
    /// a document referencing another grants no authority to fetch it, and a check that banned the
    /// words would fail on the sentence that states the rule.
    /// </remarks>
    [Theory]
    [InlineData("*")]
    [InlineData("0000892482|*|*")]
    [InlineData("0000892482")]
    [InlineData("0001493152-23-003970")]
    [InlineData("form8-k.htm")]
    [InlineData("https://www.sec.gov/Archives/edgar/data/892482/000149315223003970/form8-k.htm")]
    [InlineData("0000320193|0000320193-24-000001|aapl-20240101.htm")]
    public void No_wildcard_or_foreign_target_is_covered(string attempt)
    {
        var verdict = Load().Covers(
            SecEdgarFilingDocumentPartition.Source, attempt,
            new DateOnly(2021, 9, 1), new DateOnly(2026, 8, 31));

        Assert.False(verdict.Allowed);
        Assert.Contains("is not in the 30", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>Every authorised target is the F5 identity: CIK, accession, primary document.</summary>
    [Fact]
    public void Every_authorised_target_carries_the_full_filing_document_identity()
    {
        foreach (var target in Load().Symbols)
        {
            var parts = target.Split('|');

            Assert.Equal(3, parts.Length);
            Assert.Matches("^[0-9]{10}$", parts[0]);
            Assert.Matches("^[0-9]{10}-[0-9]{2}-[0-9]{6}$", parts[1]);
            Assert.False(string.IsNullOrWhiteSpace(parts[2]));
            Assert.DoesNotContain("://", target, StringComparison.Ordinal);

            // And the production parser accepts it, so a target that could not be requested would
            // fail here rather than at dispatch.
            Assert.True(FilingDocumentSubject.Parse(target).IsAccepted, target);
        }
    }

    // ---- 11, 12, 28, 29. the partition -----------------------------------------------------------

    /// <summary>11, 12. Six batches, five documents each, thirty in total.</summary>
    [Fact]
    public void There_are_six_batches_of_five()
    {
        var batches = SecEdgarFilingDocumentPartition.Batches;

        Assert.Equal(6, batches.Length);
        Assert.Equal(30, SecEdgarFilingDocumentPartition.Planned);
        Assert.Equal([1, 2, 3, 4, 5, 6], batches.Select(b => b.Index));

        // Prior consumption gates the order: batch n can only run once n-1 batches have been spent.
        //
        // Asserted for batches 2-6, which is where the sequencing lives. Batch 1's value is NOT
        // pinned to zero, because it records what its most recent approval was given against and
        // that legitimately moves: attempt 1 spent five units, so attempt 2 had to be approved
        // against five or runner.prior-consumption@1 would have refused it. Pinning the initial
        // value would make a correctly-recorded retry look like a defect.
        Assert.Equal(
            [5, 10, 15, 20, 25],
            batches.Skip(1).Select(b => b.ExpectedPriorConsumption));

        // Batch 1's own value is still a whole number of batches, and still leaves room for one.
        var first = batches[0].ExpectedPriorConsumption;

        Assert.Equal(0, first % SecEdgarFilingDocumentPartition.PerBatch);
        Assert.InRange(first, 0, SecEdgarFilingDocumentPartition.PlannedRequests - SecEdgarFilingDocumentPartition.PerBatch);

        foreach (var batch in batches)
        {
            Assert.Equal(5, batch.Count);
            Assert.Equal(5, SecEdgarFilingDocumentPartition.TargetsFor(batch.Index).Count);
            Assert.Equal(SecEdgarFilingDocumentPartition.Declaration, batch.Declaration);
        }
    }

    /// <summary>28. A batch covers five targets and there is no way to ask it for more.</summary>
    [Fact]
    public void A_batch_cannot_be_expanded_beyond_its_five_targets()
    {
        // Every batch is exactly five, and the six together are exactly the thirty - so a batch
        // that grew would have to take another batch's documents, and the member check refuses that.
        var all = SecEdgarFilingDocumentPartition.Batches
            .SelectMany(b => SecEdgarFilingDocumentPartition.TargetsFor(b.Index))
            .ToList();

        Assert.Equal(30, all.Count);
        Assert.Equal(30, all.Distinct(StringComparer.Ordinal).Count());

        // There is no seventh batch to reach for, and no default.
        Assert.Throws<ArgumentOutOfRangeException>(() => SecEdgarFilingDocumentPartition.TargetsFor(7));
        Assert.Throws<ArgumentOutOfRangeException>(() => SecEdgarFilingDocumentPartition.TargetsFor(0));

        // And each batch holds only its own member's documents.
        foreach (var batch in SecEdgarFilingDocumentPartition.Batches)
        {
            Assert.All(
                SecEdgarFilingDocumentPartition.TargetsFor(batch.Index),
                t => Assert.StartsWith(batch.Cik + "|", t, StringComparison.Ordinal));
        }
    }

    /// <summary>29. The partition is derived from the declaration and repeats identically.</summary>
    [Fact]
    public void The_six_batch_partition_is_deterministic_and_derived()
    {
        var first = SecEdgarFilingDocumentPartition.Batches
            .SelectMany(b => SecEdgarFilingDocumentPartition.TargetsFor(b.Index))
            .ToList();

        var second = SecEdgarFilingDocumentPartition.Batches
            .SelectMany(b => SecEdgarFilingDocumentPartition.TargetsFor(b.Index))
            .ToList();

        Assert.Equal(first, second);

        // Derived, not chosen: the concatenated batches are the declaration's own target order.
        Assert.Equal(DeclarationTargets(), first);
    }

    // ---- 17-19. expiry ---------------------------------------------------------------------------

    /// <summary>17, 18, 19. Fourteen days, exclusive at the far end, and dead after it.</summary>
    [Fact]
    public void The_authorisation_is_valid_for_exactly_fourteen_days_and_expires_exclusively()
    {
        var authorization = Load();
        var scope = authorization.Scope!;

        // 17.
        Assert.Equal(Issued, scope.IssuedAtUtc);
        Assert.Equal(Expires, scope.ExpiresAtUtc);
        Assert.Equal(14, (scope.ExpiresAtUtc - scope.IssuedAtUtc).TotalDays);
        Assert.Equal(DateTimeKind.Utc, scope.IssuedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, scope.ExpiresAtUtc.Kind);

        // 18. Valid at the issue instant and one tick before expiry; dead at expiry itself.
        Assert.True(authorization.IsValidAt(Issued).Allowed);
        Assert.True(authorization.IsValidAt(Expires.AddTicks(-1)).Allowed);

        var atExpiry = authorization.IsValidAt(Expires);

        Assert.False(atExpiry.Allowed);
        Assert.Contains(AcquisitionAuthorization.ExpiredRule, atExpiry.Reason, StringComparison.Ordinal);

        // 19. And a dispatch attempted after expiry is refused and costs nothing.
        var afterExpiry = authorization.TryConsume(
            SecEdgarFilingDocumentPartition.Source,
            authorization.Symbols.First(),
            new DateOnly(2021, 9, 1), new DateOnly(2026, 8, 31),
            Expires.AddSeconds(1));

        Assert.False(afterExpiry.Allowed);
        Assert.Contains(AcquisitionAuthorization.ExpiredRule, afterExpiry.Reason, StringComparison.Ordinal);
        Assert.Equal(0, authorization.Consumed);
    }

    /// <summary>The expiry is not the evidence window, and the two are different values.</summary>
    [Fact]
    public void The_expiry_is_not_the_acquisition_window()
    {
        var authorization = Load();

        Assert.Equal(new DateOnly(2021, 9, 1), authorization.WindowFrom);
        Assert.Equal(new DateOnly(2026, 8, 31), authorization.WindowTo);

        Assert.NotEqual(authorization.WindowFrom, DateOnly.FromDateTime(authorization.Scope!.IssuedAtUtc));
        Assert.NotEqual(authorization.WindowTo, DateOnly.FromDateTime(authorization.Scope.ExpiresAtUtc));
    }

    // ---- 20, 30. it is sealed and unused ---------------------------------------------------------

    /// <summary>20, 30. Nothing has been authorised, nothing consumed, nothing dispatched.</summary>
    [Fact]
    public void The_authorisation_is_sealed_and_entirely_unused()
    {
        var authorization = Load();

        Assert.Equal(0, authorization.Consumed);
        Assert.Equal(30, authorization.Remaining);
        Assert.Equal(0, authorization.Suppressed);

        // Every batch is unauthorised. This is the flag an operator raises, one at a time, and
        // raising one without the deliberate act that goes with it should fail the build.
        Assert.All(SecEdgarFilingDocumentPartition.Batches, b => Assert.False(b.Authorised));

        // The file says so too, in the words the repository's other sealed artefacts use.
        Assert.StartsWith("SEALED - LOADABLE. NO BATCH IS AUTHORISED", Stated("Status"), StringComparison.Ordinal);
    }

    /// <summary>The SEC source admits filing documents, and F6g is the stage that opened it.</summary>
    /// <remarks>
    /// Through F4, F4b, F5, F6, F6b, F6c and F6e the definition deliberately did not declare the
    /// category, and every one of those stages re-proved it: admission was the gate that kept
    /// dispatch closed while the capability, the declaration and the authorisation were built behind
    /// it. F6g is the stage that opens it, in the same run that spends the authorisation - so the
    /// test that guarded it changes with it rather than being deleted.
    /// </remarks>
    [Fact]
    public void The_registered_sec_source_now_admits_filing_documents()
    {
        var definition = new SecEdgarSource().Definition(Issued);

        Assert.Contains(DataCategory.RegulatoryFilingDocuments, definition.Categories);
        Assert.Contains(DataCategory.RegulatoryFilings, definition.Categories);

        // Exactly one category was added, and nothing else came with it.
        Assert.Equal(6, definition.Categories.Count);
    }

    /// <summary>30. Neither the partition nor the sealer can reach a transport or a gateway.</summary>
    [Fact]
    public void Nothing_in_this_stage_can_dispatch()
    {
        string[] forbidden =
        [
            "HttpClient", "HttpMessageHandler", "IIngestionGateway", "IngestionGateway",
            "IDataAcquisition", "IDataProvider", "IRawResponseArchive", "IActionGateway",
            "SecFilingDocumentFetcher", "IFilingDocumentFetcher",
        ];

        foreach (var type in new[]
        {
            typeof(SecEdgarFilingDocumentPartition),
            typeof(SecEdgarFilingDocumentAuthorizationSealer),
        })
        {
            var referenced = type
                .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .OfType<MethodBase>()
                .SelectMany(m => m.GetParameters())
                .Select(p => p.ParameterType.Name)
                .Concat(type.GetFields(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                    .Select(f => f.FieldType.Name))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (var name in forbidden)
            {
                Assert.DoesNotContain(name, referenced, StringComparer.Ordinal);
            }
        }
    }

    // ---- 21, 22. nothing else moved --------------------------------------------------------------

    /// <summary>21. Every authorisation that existed before F6c is byte-identical.</summary>
    [Fact]
    public void No_pre_existing_authorisation_changed()
    {
        foreach (var (file, sha) in PreExisting)
        {
            var bytes = File.ReadAllBytes(Universe.RepositoryPath("declarations", file));

            Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }

        // And the sealed declaration this authorisation binds to is untouched by being bound.
        Assert.Equal(
            PilotSha,
            Convert.ToHexString(SHA256.HashData(
                File.ReadAllBytes(Universe.RepositoryPath("declarations", PilotFile)))).ToLowerInvariant());
    }

    /// <summary>22. It supersedes nothing, and nothing claims to supersede it.</summary>
    /// <remarks>
    /// An original omits the supersession fields rather than nulling them - the convention the
    /// <c>@1</c> loader enforces and the sealed SEC submissions authorisation states of itself in
    /// the same words. Asserted from the file's shape as well as from the loaded object, because
    /// "absent" and "present and null" are different files.
    /// </remarks>
    [Fact]
    public void The_authorisation_supersedes_nothing_and_is_superseded_by_nothing()
    {
        var authorization = Load();

        Assert.Null(authorization.SupersedesAuthorizationId);
        Assert.Null(authorization.SupersedesAuthorizationDigest);
        Assert.Equal(0, authorization.AlreadyConsumed);

        using var document = JsonDocument.Parse(File.ReadAllText(AuthorizationPath));

        Assert.False(document.RootElement.TryGetProperty("SupersedesAuthorizationId", out _));
        Assert.False(document.RootElement.TryGetProperty("AlreadyConsumed", out _));
        Assert.False(document.RootElement.TryGetProperty("SupersedesAuthorizationDigest", out _));

        // And no other authorisation names this one as its predecessor.
        foreach (var (file, _) in PreExisting)
        {
            using var other = JsonDocument.Parse(
                File.ReadAllText(Universe.RepositoryPath("declarations", file)));

            if (other.RootElement.TryGetProperty("SupersedesAuthorizationId", out var supersedes))
            {
                Assert.NotEqual(
                    SecEdgarFilingDocumentPartition.AuthorizationId,
                    supersedes.GetString());
            }
        }
    }

    // ---- 23-27. tampering, on temporary copies ---------------------------------------------------

    /// <summary>23. Editing any digest-covered field invalidates the authorisation.</summary>
    [Theory]
    [InlineData("DataCategory", "\"RegulatoryFilings\"")]
    [InlineData("SubjectKind", "\"Company\"")]
    [InlineData("ExpiresAtUtc", "\"2026-10-12T00:00:00Z\"")]
    [InlineData("IssuedAtUtc", "\"2026-09-11T00:00:00Z\"")]
    [InlineData("DispatchCeiling", "31")]
    [InlineData("PlannedRequests", "31")]
    [InlineData("Vendor", "\"eodhd\"")]
    public void Editing_a_digest_covered_field_invalidates_the_authorisation(string field, string json)
    {
        using var copy = Sandbox.Create();

        copy.EditAuthorization(field, json);

        var refusal = Assert.Throws<InvalidOperationException>(() => copy.Load());

        Assert.Contains("digest does not match its content", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>25, 26, 27. Editing a binding field invalidates it too.</summary>
    [Theory]
    [InlineData("DeclarationPath", "\"declarations/universe-sample400-2021-2026.json\"")]
    [InlineData("DeclarationDigest", "\"0000000000000000000000000000000000000000000000000000000000000000\"")]
    [InlineData("DeclarationSha256", "\"1111111111111111111111111111111111111111111111111111111111111111\"")]
    [InlineData("DeclarationSchema", "\"filing-document-pilot-declaration@2\"")]
    [InlineData("DeclarationDigestField", "\"IdentityDigest\"")]
    public void Editing_a_binding_field_invalidates_the_authorisation(string field, string json)
    {
        using var copy = Sandbox.Create();

        copy.EditAuthorization(field, json);

        var refusal = Assert.Throws<InvalidOperationException>(() => copy.Load());

        Assert.Contains("digest does not match its content", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>Editing an authorised target invalidates the authorisation.</summary>
    [Fact]
    public void Editing_an_authorised_target_invalidates_the_authorisation()
    {
        using var copy = Sandbox.Create();

        copy.EditText(text => text.Replace(
            DeclarationTargets()[0],
            "0000320193|0000320193-24-000001|aapl-20240101.htm",
            StringComparison.Ordinal));

        var refusal = Assert.Throws<InvalidOperationException>(() => copy.Load());

        Assert.Contains("digest does not match its content", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>24. Changing the declaration breaks the binding, even by one prose byte.</summary>
    [Fact]
    public void Changing_the_declaration_invalidates_the_binding()
    {
        using var copy = Sandbox.Create();

        copy.EditDeclaration(text => text.Replace(
            "\"Purpose\": \"Answer one question",
            "\"Purpose\": \"answer one question",
            StringComparison.Ordinal));

        var refusal = Assert.Throws<InvalidOperationException>(() => copy.Load());

        Assert.Contains("hashes to", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("changed after the authorisation was approved", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>24. And removing the declaration entirely refuses rather than degrading.</summary>
    [Fact]
    public void A_missing_declaration_invalidates_the_binding()
    {
        using var copy = Sandbox.Create();

        File.Delete(Path.Combine(copy.Root, "declarations", PilotFile));

        var refusal = Assert.Throws<InvalidOperationException>(() => copy.Load());

        Assert.Contains("does not exist under the expected root", refusal.Message, StringComparison.Ordinal);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static string AuthorizationPath =>
        Universe.RepositoryPath("declarations", SecEdgarFilingDocumentPartition.Declaration);

    private static AcquisitionAuthorization Load() =>
        AcquisitionAuthorization.Load(AuthorizationPath, EvidenceBase, Universe.RepositoryPath());

    private static string Stated(string property)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(AuthorizationPath));

        return document.RootElement.GetProperty(property).GetString()!;
    }

    private static List<string> DeclarationTargets()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Universe.RepositoryPath("declarations", PilotFile)));

        return document.RootElement
            .GetProperty("Targets")
            .EnumerateArray()
            .Select(t => t.GetProperty("SubjectIdentifier").GetString()!)
            .ToList();
    }

    private static bool IsDocumentScoped(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        // Not every file in declarations/ is an object - strategies.json is a bare array - so the
        // kind is checked before a property is asked for. A scanner that threw on one file would
        // report "no @3 authorisation exists" for the wrong reason.
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("Schema", out var schema)
            && string.Equals(
                schema.GetString(),
                AcquisitionAuthorization.DocumentScopedSchema,
                StringComparison.Ordinal);
    }

    /// <summary>
    /// A temporary root holding copies of the sealed authorisation and its declaration.
    /// </summary>
    /// <remarks>
    /// Both are copied byte-for-byte, so the copy starts valid and any refusal below is caused by
    /// the edit the test made. <c>declarations/</c> itself is only ever read.
    /// </remarks>
    private sealed class Sandbox : IDisposable
    {
        private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

        private Sandbox(string root) => Root = root;

        public string Root { get; }

        private string AuthorizationCopy =>
            Path.Combine(Root, "declarations", SecEdgarFilingDocumentPartition.Declaration);

        public static Sandbox Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "f6c-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(Path.Combine(root, "declarations"));

            foreach (var file in new[] { SecEdgarFilingDocumentPartition.Declaration, PilotFile })
            {
                File.Copy(
                    Universe.RepositoryPath("declarations", file),
                    Path.Combine(root, "declarations", file));
            }

            return new Sandbox(root);
        }

        public AcquisitionAuthorization Load() =>
            AcquisitionAuthorization.Load(AuthorizationCopy, EvidenceBase, Root);

        public void EditAuthorization(string property, string replacementJson) =>
            EditText(text =>
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();

                node[property] = System.Text.Json.Nodes.JsonNode.Parse(replacementJson);

                return node.ToJsonString(Indented);
            });

        public void EditText(Func<string, string> edit) =>
            File.WriteAllText(AuthorizationCopy, edit(File.ReadAllText(AuthorizationCopy)));

        public void EditDeclaration(Func<string, string> edit)
        {
            var path = Path.Combine(Root, "declarations", PilotFile);

            File.WriteAllText(path, edit(File.ReadAllText(path)));
        }

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
    }
}
