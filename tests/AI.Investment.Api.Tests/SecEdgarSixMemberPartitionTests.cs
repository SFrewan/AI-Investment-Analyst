using System.Text.Json;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The SEC partition is cut, it is bound to the installed authorisation, and it authorises nothing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read-only, and there is nothing here that could stop being read-only.</strong> These
/// facts load two declaration files, one artefact, one settings file and one source file, and call
/// <see cref="AcquisitionAuthorization.Load"/> and <c>Covers</c>. No provider is constructed, no
/// scope is opened, no connection is made and <c>TryConsume</c> is never called - so the counter
/// this class reads at the end is the same counter it read at the start, and says so.
/// </para>
/// <para>
/// <strong>Why the partition needs proving at all.</strong> Cutting a partition is the step where a
/// six-member authorisation quietly becomes a seven-member one, or where a member is written down
/// twice and charged twice, or where the CIK next to a symbol is the wrong company's. None of those
/// would fail anything else in this repository: the authorisation would still load, the digest would
/// still match, and the error would surface as a filing read for the wrong issuer months later. So
/// each is named here, and each is checked against a source outside the partition - the sealed
/// universe for identity, the loaded authorisation for scope and arithmetic, the declaration for
/// reading scope - rather than against the partition's agreement with itself.
/// </para>
/// </remarks>
public sealed class SecEdgarSixMemberPartitionTests
{
    private const string SealedFingerprint =
        "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    /// <summary>The digest approved at review, restated as a literal for the same reason as there.</summary>
    private const string ApprovedDigest =
        "499f211f9c931c78d7faf1beb76eef3205fec12cb28db06eeeda461964567682";

    private const string SealedManifest = "universe-sample400-2021-2026.json";

    private const string IdentityArtefact = "identity-sample400.json";

    /// <summary>The partition's own source, read to prove what is absent from it.</summary>
    private const string PartitionSourceFile = "SecEdgarSixMemberPartition.cs";

    /// <summary>A member of the 400 that is not one of the six. Apple, and not in scope.</summary>
    private const string OutsiderCik = "0000320193";

    private static readonly string[] Ciks =
    [
        "0000892482", "0001335112", "0001404123", "0001426332", "0001775625", "0001784851",
    ];

    private static readonly string[] Symbols =
        ["QUMU.US", "LGIQ.US", "ONEM.US", "NGM.US", "SDC.US", "SHPW.US"];

    /// <summary>The extra forms LGIQ alone may be read for, approved at the pre-install review.</summary>
    private static readonly string[] LgiqForms = ["424B*", "S-1", "S-3"];

    private static readonly DateOnly WindowFrom = new(2021, 9, 1);

    private static readonly DateOnly WindowTo = new(2026, 8, 31);

    private static AcquisitionAuthorization Installed() =>
        AcquisitionAuthorization.Load(
            Universe.RepositoryPath("declarations", SecEdgarSixMemberPartition.Declaration),
            SealedFingerprint);

    /// <summary>
    /// Six batches, one member each, numbered in order, and the spend column steps with them.
    /// </summary>
    /// <remarks>
    /// The prior-consumption column is checked against the running total of the counts rather than
    /// restated, so the two cannot be nudged apart: a batch inserted, removed or resized breaks the
    /// step and fails here before it can reach an approval.
    /// </remarks>
    [Fact]
    public void The_partition_is_exactly_six_single_member_batches()
    {
        var partition = SecEdgarSixMemberPartition.Batches;

        Assert.Equal(string.Join(",", Ciks), string.Join(",", partition.Select(b => b.Cik)));
        Assert.Equal([1, 2, 3, 4, 5, 6], partition.Select(b => b.Index).ToList());
        Assert.Equal([1, 1, 1, 1, 1, 1], partition.Select(b => b.Count).ToList());

        var spent = 0;

        foreach (var batch in partition)
        {
            Assert.Equal(spent, batch.ExpectedPriorConsumption);

            spent += batch.Count;
        }

        Assert.Equal(SecEdgarSixMemberPartition.PlannedRequests, spent);
        Assert.Equal(SecEdgarSixMemberPartition.PlannedRequests, SecEdgarSixMemberPartition.Planned);
    }

    /// <summary>
    /// Every batch's CIK and symbol are the sealed universe's own pairing, not the partition's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two sources, because neither alone settles it. The sealed manifest is the authority on who is
    /// a member, and it is asked for membership only - it carries no ticker for two of the six, which
    /// is exactly why the identity recovery happened. The recovered identities are the authority on
    /// what each company traded as, and the symbol is derived through the same
    /// <see cref="AcquisitionPlanning.SymbolFor"/> the acquisition runners use rather than by
    /// appending a suffix here.
    /// </para>
    /// <para>
    /// A transposition - the right six CIKs against the wrong six symbols - is the failure this
    /// exists for, and it is the one a reviewer is least likely to catch by eye.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Each_batch_names_the_cik_and_symbol_pairing_the_sealed_universe_states()
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("declarations", SealedManifest)));

        var members = manifest.RootElement
            .GetProperty("Members")
            .EnumerateArray()
            .Select(m => m.GetProperty("Cik").GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

        using var identity = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("artifacts", "universe", IdentityArtefact)));

        var recovered = identity.RootElement
            .GetProperty("Members")
            .EnumerateArray()
            .Where(m => m.TryGetProperty("Ready", out var ready) && ready.ValueKind == JsonValueKind.True)
            .Where(m => m.TryGetProperty("Ticker", out var ticker) && ticker.ValueKind == JsonValueKind.String)
            .ToDictionary(
                m => m.GetProperty("Cik").GetString() ?? string.Empty,
                m => AcquisitionPlanning.SymbolFor(m.GetProperty("Ticker").GetString()!),
                StringComparer.Ordinal);

        foreach (var batch in SecEdgarSixMemberPartition.Batches)
        {
            Assert.True(
                members.Contains(batch.Cik),
                Universe.Inv($"batch {batch.Index} names `{batch.Cik}`, which is not a member of the sealed universe"));

            Assert.True(
                recovered.ContainsKey(batch.Cik),
                Universe.Inv($"batch {batch.Index} names `{batch.Cik}`, which carries no recovered, ready identity"));

            Assert.Equal(recovered[batch.Cik], batch.Symbol);
        }

        // And the pairing is the one the reports and the breach evidence were written against.
        Assert.Equal(
            string.Join(",", Symbols),
            string.Join(",", SecEdgarSixMemberPartition.Batches.Select(b => b.Symbol)));
    }

    /// <summary>
    /// Every batch names the installed authorisation, and it covers that batch's subject.
    /// </summary>
    /// <remarks>
    /// The binding is asked of the loader rather than asserted from the file: the declaration is
    /// loaded against the sealed evidence base, its identity and digest are the reviewed ones, and
    /// <c>Covers</c> is then asked for each batch exactly as a runner would ask it. A batch whose
    /// subject the authorisation does not cover fails here rather than at dispatch.
    /// </remarks>
    [Fact]
    public void Every_batch_is_bound_to_the_installed_authorisation_which_covers_its_subject()
    {
        var installed = Installed();

        Assert.Equal(SecEdgarSixMemberPartition.AuthorizationId, installed.AuthorizationId);
        Assert.Equal(ApprovedDigest, installed.Digest);
        Assert.Null(installed.SupersedesAuthorizationId);

        foreach (var batch in SecEdgarSixMemberPartition.Batches)
        {
            Assert.Equal(SecEdgarSixMemberPartition.Declaration, batch.Declaration);

            Assert.True(
                installed.Covers(SecEdgarSixMemberPartition.Source, batch.Cik, WindowFrom, WindowTo).Allowed,
                Universe.Inv($"batch {batch.Index} names `{batch.Cik}`, which the installed authorisation does not cover"));
        }

        // No batch may name another authorisation. A filing batch pointed at the price or
        // corporate-actions declaration would be in scope there and would spend a budget approved
        // for something else entirely.
        Assert.DoesNotContain(
            SecEdgarSixMemberPartition.Batches,
            b => b.Declaration.Contains("eodhd", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The partition plans exactly six requests, and the six are the authorisation's own arithmetic.
    /// </summary>
    /// <remarks>
    /// The count is derived the way the loader derives it - distinct subjects times distinct sources -
    /// rather than read off the declaration and compared with itself. One GET per subject is the
    /// connector's shape, not an assumption: <c>SecEdgarProvider</c> takes no continuation token and
    /// declares no window support, so a subject cannot cost two requests or half of one.
    /// </remarks>
    [Fact]
    public void The_partition_plans_exactly_six_requests_and_agrees_with_the_authorisation()
    {
        var installed = Installed();

        var subjects = SecEdgarSixMemberPartition.Batches
            .Select(b => b.Cik)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var sources = SecEdgarSixMemberPartition.Batches
            .Select(_ => SecEdgarSixMemberPartition.Source)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var planned = subjects.Count * sources.Count;

        Assert.Equal(6, planned);
        Assert.Equal(planned, SecEdgarSixMemberPartition.Planned);
        Assert.Equal(planned, installed.PlannedRequests);
        Assert.Equal(planned, installed.DispatchCeiling);
        Assert.Equal(0, installed.AlreadySatisfied);
        Assert.Equal(installed.PlannedRequests - installed.AlreadySatisfied, installed.DispatchCeiling);

        // The source the partition names is the only source the authorisation knows.
        Assert.Equal(
            SecEdgarSixMemberPartition.Source,
            string.Join(",", installed.Sources.Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// No member is written down twice, and nothing outside the six is written down at all.
    /// </summary>
    /// <remarks>
    /// Set equality in both directions against the authorisation's own symbol list, because the two
    /// failures are opposite and only one of them looks like an error. A duplicate charges a member
    /// twice and exhausts the ceiling early; an extra member is an unauthorised subject dressed as an
    /// authorised one; a missing member is a question that silently stops being asked.
    /// </remarks>
    [Fact]
    public void No_member_appears_twice_and_nothing_outside_the_six_appears_at_all()
    {
        var installed = Installed();

        var ciks = SecEdgarSixMemberPartition.Batches.Select(b => b.Cik).ToList();
        var symbols = SecEdgarSixMemberPartition.Batches.Select(b => b.Symbol).ToList();

        Assert.Equal(
            string.Join(",", ciks),
            string.Join(",", ciks.Distinct(StringComparer.Ordinal)));

        Assert.Equal(
            string.Join(",", symbols),
            string.Join(",", symbols.Distinct(StringComparer.Ordinal)));

        var authorised = string.Join(",", installed.Symbols.Order(StringComparer.Ordinal));

        // Every subject the authorisation names is cut into a batch, and no batch names one it does
        // not. Said as a single string comparison so a difference either way reads as a diff.
        Assert.Equal(authorised, string.Join(",", ciks.Order(StringComparer.Ordinal)));
        Assert.Equal(string.Join(",", Ciks.Order(StringComparer.Ordinal)), authorised);

        // A member of the 400 that is not one of the six is absent from the partition, and would be
        // refused on scope if it were present.
        Assert.DoesNotContain(
            SecEdgarSixMemberPartition.Batches,
            b => string.Equals(b.Cik, OutsiderCik, StringComparison.Ordinal));

        Assert.False(
            installed.Covers(SecEdgarSixMemberPartition.Source, OutsiderCik, WindowFrom, WindowTo).Allowed);
    }

    /// <summary>
    /// The partition preserves the window, the source and the category it was installed with.
    /// </summary>
    [Fact]
    public async Task The_window_source_and_category_are_the_ones_the_authorisation_was_installed_with()
    {
        var installed = Installed();

        Assert.Equal(WindowFrom, installed.WindowFrom);
        Assert.Equal(WindowTo, installed.WindowTo);

        using var declaration = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("declarations", SecEdgarSixMemberPartition.Declaration)));

        var root = declaration.RootElement;

        Assert.Equal(
            "submissions/CIK{cik}.json",
            root.GetProperty("Endpoints").GetProperty(SecEdgarSixMemberPartition.Source).GetString());

        Assert.Equal(
            "RegulatoryFilings",
            root.GetProperty("Categories").GetProperty(SecEdgarSixMemberPartition.Source).GetString());

        // The partition names no other source, and in particular no price or corporate-actions one.
        Assert.DoesNotContain(
            SecEdgarSixMemberPartition.Batches,
            b => b.Declaration.Contains("splits", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The reviewed secondary form scope for LGIQ survives the cut, and stays LGIQ's alone.
    /// </summary>
    /// <remarks>
    /// The forms are not copied into the partition. One statement of a fact is checkable; two are a
    /// pair that can drift, and the declaration is the one a reviewer approved. So the partition
    /// carries the CIK and this fact holds the declaration's scope to naming that same CIK, with the
    /// three forms as literals.
    /// </remarks>
    [Fact]
    public async Task The_reviewed_secondary_form_scope_belongs_to_one_batch_and_stays_there()
    {
        using var declaration = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("declarations", SecEdgarSixMemberPartition.Declaration)));

        var scoped = declaration.RootElement
            .GetProperty("FilingSelection")
            .GetProperty("PerMemberFormScope")
            .EnumerateObject()
            .Where(p => !string.Equals(p.Name, "Note", StringComparison.Ordinal))
            .ToList();

        Assert.Single(scoped);

        var lgiq = SecEdgarSixMemberPartition.Batches
            .Single(b => string.Equals(b.Symbol, "LGIQ.US", StringComparison.Ordinal));

        Assert.Equal(lgiq.Cik, scoped[0].Name);
        Assert.Equal("LGIQ.US", scoped[0].Value.GetProperty("Symbol").GetString());
        Assert.False(scoped[0].Value.GetProperty("AppliesToOtherMembers").GetBoolean());

        Assert.Equal(
            LgiqForms,
            scoped[0].Value.GetProperty("AdditionalForms")
                .EnumerateArray()
                .Select(f => f.GetString() ?? string.Empty)
                .ToArray());
    }

    /// <summary>
    /// The partition authorises nothing, can reach no provider, and has charged nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four claims, and the second is the one that needs an unusual instrument. "This cannot
    /// dispatch" is a statement about what is absent from a file, and absence is not something
    /// reflection can be asked about - so the file's own source text is read and required not to
    /// contain the tokens by which anything in this repository reaches a provider or charges an
    /// authorisation. It is blunt, and it is the same instrument the split readiness gate uses to
    /// pin its runner's statement ordering, for the same reason: the alternative is running the
    /// thing to watch it refuse.
    /// </para>
    /// <para>
    /// The counter is read last and is still zero. Every fact in this class has run against the same
    /// declaration by then, so this is also the proof that loading, covering and cutting a partition
    /// are all free - nothing in the preparation spends a unit.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_partition_authorises_nothing_and_cannot_dispatch_or_charge()
    {
        Assert.DoesNotContain(SecEdgarSixMemberPartition.Batches, b => b.Authorised);

        var source = await File.ReadAllTextAsync(Universe.RepositoryPath(
            "tests", "AI.Investment.Api.Tests", PartitionSourceFile));

        foreach (var token in new[]
        {
            "CreateScope",
            "GetRequiredService",
            "IDataAcquisition",
            "AcquireAsync",
            "HttpClient",
            "TryConsume",
            "RecordPriorConsumption",
        })
        {
            Assert.False(
                source.Contains(token, StringComparison.Ordinal),
                Universe.Inv($"`{token}` appears in `{PartitionSourceFile}`; a partition is a table of facts and must not be able to reach a provider or charge an authorisation"));
        }

        // The connector it would need is off, and turning it on is a separate, visible act.
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("src", "AI.Investment.Api", "appsettings.json")));

        Assert.False(settings.RootElement
            .GetProperty("Providers")
            .GetProperty("SecEdgar")
            .GetProperty("Enabled")
            .GetBoolean());

        var installed = Installed();

        Assert.Equal(0, installed.Consumed);
        Assert.Equal(SecEdgarSixMemberPartition.PlannedRequests, installed.Remaining);
    }
}
