using System.Globalization;
using System.Text.Json;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The installed SEC EDGAR authorisation: loadable, bounded to six members, and authorising nothing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read-only, and no provider is reachable from here.</strong> This reads two files and
/// calls <see cref="AcquisitionAuthorization.Load"/>. It opens no connection, enables no connector,
/// dispatches nothing and touches no database. It is the proof that installation happened and the
/// proof that installation is all that happened.
/// </para>
/// <para>
/// <strong>Why an installed declaration needs its own test at all.</strong> This repository has no
/// declaration auto-discovery: every caller of <see cref="AcquisitionAuthorization.Load"/> is given
/// an explicit path, and the only enumeration of <c>declarations/</c> is a safety scan that reads
/// one field. So "installed" cannot be demonstrated by a runner picking the file up - nothing
/// picks anything up. It is demonstrated by showing that the loader accepts the file against the
/// sealed evidence base, that its identity is the one that was reviewed, and that a spent or
/// widened authorisation would fail here.
/// </para>
/// <para>
/// <strong>The digest assertion is the point of the whole file.</strong> An authorisation is only
/// worth installing if it is the one that was approved, and the digest is what makes that
/// checkable. It is asserted as a literal because a literal is what a reviewer approved; recomputing
/// it from the file's own fields would assert only that the file agrees with itself.
/// </para>
/// </remarks>
public sealed class SecEdgarSixMemberInstallationTests
{
    /// <summary>The installed declaration, under the same convention as the EODHD ones.</summary>
    private const string InstalledDeclaration =
        "acquisition-sec-edgar-gate6-six-members-2021-09-to-2026-08.json";

    /// <summary>The draft it was installed from, which must no longer be an authorisation.</summary>
    private const string RetiredDraft = "sec-edgar-gate6-six-members-2021-2026.draft.json";

    private const string SealedFingerprint =
        "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    /// <summary>The digest approved at review. A literal, deliberately.</summary>
    private const string ApprovedDigest =
        "499f211f9c931c78d7faf1beb76eef3205fec12cb28db06eeeda461964567682";

    private const string AuthorizationId = "sec-edgar-gate6-six-members-2021-09-to-2026-08";

    private const int Members = 6;

    /// <summary>The six, in the ordinal order the declaration lists them.</summary>
    private static readonly string[] Ciks =
    [
        "0000892482", "0001335112", "0001404123", "0001426332", "0001775625", "0001784851",
    ];

    /// <summary>The extra forms LGIQ alone may be read for, approved at the pre-install review.</summary>
    private static readonly string[] LgiqForms = ["424B*", "S-1", "S-3"];

    /// <summary>
    /// The loader accepts the installed file, and what it accepts is what was approved.
    /// </summary>
    [Fact]
    public void The_installed_authorisation_loads_and_is_the_one_that_was_approved()
    {
        var path = Universe.RepositoryPath("declarations", InstalledDeclaration);

        Assert.True(File.Exists(path), $"`{InstalledDeclaration}` is not installed in declarations/");

        var installed = AcquisitionAuthorization.Load(path, SealedFingerprint);

        Assert.Equal(AuthorizationId, installed.AuthorizationId);
        Assert.Equal(ApprovedDigest, installed.Digest);
        Assert.Equal(AcquisitionAuthorization.Schema, installed.SchemaVersion);

        // An original, not a successor. The @1 loader refuses supersession fields outright, so
        // this states the intent as well as the fact.
        Assert.Null(installed.SupersedesAuthorizationId);

        Assert.Equal(Members, installed.PlannedRequests);
        Assert.Equal(0, installed.AlreadySatisfied);
        Assert.Equal(Members, installed.DispatchCeiling);

        // Nothing has been dispatched under it, which is the whole point of installing without
        // authorising: the ceiling is intact and the counter has never moved.
        Assert.Equal(Members, installed.Remaining);
    }

    /// <summary>
    /// It is bounded to six named companies, and there is no path by which it widens.
    /// </summary>
    /// <remarks>
    /// <c>Covers</c> is asked directly rather than reasoned about. A symbol outside the six is
    /// refused on scope before any counting happens, which is the distinction that matters: an
    /// unauthorised subject is out of scope, not merely over budget.
    /// </remarks>
    [Fact]
    public void It_is_bounded_to_the_six_and_refuses_everything_else()
    {
        var installed = AcquisitionAuthorization.Load(
            Universe.RepositoryPath("declarations", InstalledDeclaration),
            SealedFingerprint);

        var from = DateOnly.ParseExact("2021-09-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = DateOnly.ParseExact("2026-08-31", "yyyy-MM-dd", CultureInfo.InvariantCulture);

        foreach (var cik in Ciks)
        {
            Assert.True(
                installed.Covers("sec-edgar", cik, from, to).Allowed,
                Universe.Inv($"the authorisation does not cover `{cik}`, which is one of its six"));
        }

        // A member of the 355 that is not one of the six. Refused on scope.
        Assert.False(installed.Covers("sec-edgar", "0000320193", from, to).Allowed);

        // An EODHD symbol. This authorisation names CIKs and nothing else.
        Assert.False(installed.Covers("sec-edgar", "AAPL.US", from, to).Allowed);

        // The right subject through the wrong source. Vendor scope is checked before symbol scope.
        Assert.False(installed.Covers("eodhd-eod", Ciks[0], from, to).Allowed);
    }

    /// <summary>
    /// Installation authorises no acquisition: no approved batch, no enabled connector.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two partitions are asserted now, and the difference between them is the point. The split
    /// partition belongs to a runner that can dispatch, so what matters there is that no batch is
    /// approved and that none of its batches has been pointed at this declaration - a filing batch
    /// run by the split runner would charge the wrong authorisation against the wrong endpoint.
    /// </para>
    /// <para>
    /// The SEC partition has no runner at all. It is a table of facts, cut so that an approval could
    /// refer to something legible, and <see cref="SecEdgarSixMemberPartitionTests"/> proves it
    /// cannot reach a provider or charge an authorisation. It is asserted here as well because
    /// "installation authorised nothing" has to keep meaning that now a partition exists, and the
    /// cheapest way for it to stop meaning it is an <c>Authorised</c> flag flipped in a file nobody
    /// thought to re-read.
    /// </para>
    /// </remarks>
    [Fact]
    public void Installation_authorises_no_acquisition()
    {
        Assert.DoesNotContain(AcquisitionSplitBatchTests.Partition, b => b.Authorised);

        Assert.DoesNotContain(
            AcquisitionSplitBatchTests.Partition,
            b => b.Declaration.Contains("sec-edgar", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(SecEdgarSixMemberPartition.Batches, b => b.Authorised);
    }

    /// <summary>
    /// The draft it came from is no longer readable as an authorisation.
    /// </summary>
    /// <remarks>
    /// Two files carrying the same authorisation would be two things that could be loaded and one
    /// that could be edited unnoticed. The draft is kept, because four reports cite its path, but
    /// it is kept with no schema, no symbols and no counters - so nothing can mistake it for the
    /// authorisation it used to hold.
    /// </remarks>
    [Fact]
    public async Task The_draft_it_was_installed_from_is_no_longer_an_authorisation()
    {
        var draft = Universe.RepositoryPath("declarations", RetiredDraft);

        if (!File.Exists(draft))
        {
            // Removed outright is also a correct outcome, and a stricter one.
            return;
        }

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(draft));

        Assert.False(document.RootElement.TryGetProperty("Schema", out _));
        Assert.False(document.RootElement.TryGetProperty("Symbols", out _));
        Assert.False(document.RootElement.TryGetProperty("DispatchCeiling", out _));

        Assert.Throws<KeyNotFoundException>(
            () => AcquisitionAuthorization.Load(draft, SealedFingerprint));
    }

    /// <summary>
    /// The six CIKs are the sealed universe's own, and the reading scope is bounded to them.
    /// </summary>
    [Fact]
    public async Task Its_members_come_from_the_sealed_universe_and_its_reading_scope_stays_inside_them()
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("declarations", "universe-sample400-2021-2026.json")));

        var sealedCiks = manifest.RootElement
            .GetProperty("Members")
            .EnumerateArray()
            .Select(m => m.GetProperty("Cik").GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(Ciks, cik => Assert.True(
            sealedCiks.Contains(cik),
            Universe.Inv($"`{cik}` is not a member of the sealed universe")));

        using var declaration = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("declarations", InstalledDeclaration)));

        var root = declaration.RootElement;

        Assert.Equal(
            Ciks,
            root.GetProperty("Symbols")
                .EnumerateArray()
                .Select(s => s.GetString() ?? string.Empty)
                .ToArray());

        var selection = root.GetProperty("FilingSelection");

        // Every breach the reading scope names belongs to one of the six.
        var breaches = selection.GetProperty("Breaches").EnumerateArray().ToList();

        Assert.Equal(23, breaches.Count);
        Assert.All(breaches, b => Assert.True(
            Ciks.Contains(b.GetProperty("Cik").GetString() ?? string.Empty, StringComparer.Ordinal),
            "a breach in the reading scope names a company outside the six"));

        // The per-member form scope names one member, and that member is one of the six.
        var scope = selection.GetProperty("PerMemberFormScope");
        var scoped = scope.EnumerateObject()
            .Where(p => !string.Equals(p.Name, "Note", StringComparison.Ordinal))
            .ToList();

        Assert.Single(scoped);

        Assert.True(
            Ciks.Contains(scoped[0].Name, StringComparer.Ordinal),
            "the per-member form scope names a company outside the six");
        Assert.Equal("LGIQ.US", scoped[0].Value.GetProperty("Symbol").GetString());

        Assert.Equal(
            LgiqForms,
            scoped[0].Value.GetProperty("AdditionalForms")
                .EnumerateArray()
                .Select(f => f.GetString() ?? string.Empty)
                .ToArray());
    }
}
