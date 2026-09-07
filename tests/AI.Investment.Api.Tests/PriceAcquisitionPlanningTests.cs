using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The planning invariants, checked against the repository's own sealed files.
/// </summary>
/// <remarks>
/// <para>
/// No database, no container, no network. These read two files that are already on disk - the
/// sealed manifest and the identity artefact - and assert the things that must be true before any
/// market-data request is authorised. They run in the ordinary suite, which is the point: the
/// facts a future acquisition would rest on should break a build the day they stop holding, not
/// be rediscovered by a stage that has already spent five hundred billable calls.
/// </para>
/// <para>
/// <strong>The one they exist for.</strong> A member that cannot be identified must be excluded
/// from acquisition and must remain in the universe. If those two sets are ever allowed to merge,
/// the sealed universe still says four hundred while the evidence base quietly says two hundred
/// and sixty-nine, and every result computed afterwards is measured on survivors.
/// </para>
/// </remarks>
public sealed class PriceAcquisitionPlanningTests
{
    private const string SealedFingerprint =
        "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private const int Members = 400;

    /// <summary>
    /// Acquisition-ready after the filing recovery: 269 from EDGAR submissions plus 86 recovered
    /// from issuers' own filings, of which every one was confirmed to name an equity.
    /// </summary>
    private const int Ready = 355;

    private const int NotReady = Members - Ready;

    /// <summary>What the panel was before the recovery, kept so the change is legible.</summary>
    private const int ReadyBeforeRecovery = 269;

    [Fact]
    public void The_symbol_sent_to_the_price_provider_carries_the_one_configured_exchange()
    {
        // EDGAR writes the bare ticker; the only exchange with a stated session is US.
        Assert.Equal("NTRS.US", AcquisitionPlanning.SymbolFor("NTRS"));
        Assert.Equal("AE.US", AcquisitionPlanning.SymbolFor("ae"));

        // And a symbol that already has a suffix does not gain a second one.
        Assert.Equal("DHIL.US", AcquisitionPlanning.SymbolFor("DHIL.US"));
    }

    [Fact]
    public void Only_SEC_authoritative_identities_may_be_sent_to_a_price_provider()
    {
        Assert.True(AcquisitionPlanning.MayAcquire(IdentityResolution.Confirmed));
        Assert.True(AcquisitionPlanning.MayAcquire(IdentityResolution.Conflict));
        Assert.True(AcquisitionPlanning.MayAcquire(IdentityResolution.SecOnly));

        // The four that must never buy anything, each for its own reason.
        Assert.False(AcquisitionPlanning.MayAcquire(IdentityResolution.Provisional));
        Assert.False(AcquisitionPlanning.MayAcquire(IdentityResolution.Ambiguous));
        Assert.False(AcquisitionPlanning.MayAcquire(IdentityResolution.Unmatched));
    }

    /// <summary>
    /// The cost, stated before it is spent, and linear because nothing here batches.
    /// </summary>
    [Fact]
    public void The_billable_cost_is_two_requests_per_member_and_there_is_nothing_to_batch()
    {
        Assert.Equal(2, AcquisitionPlanning.RequestsPerMember);
        Assert.Equal(710, AcquisitionPlanning.BillableRequests(Ready));
        Assert.Equal(0, AcquisitionPlanning.BillableRequests(0));

        // What it would have cost before the recovery, for comparison.
        Assert.Equal(538, AcquisitionPlanning.BillableRequests(ReadyBeforeRecovery));

        // The whole universe, were every member identifiable, would be eight hundred.
        Assert.Equal(800, AcquisitionPlanning.BillableRequests(Members));
    }

    [Fact]
    public void The_window_is_the_sealed_one_and_is_read_from_the_manifest_not_restated()
    {
        var manifest = Manifest();

        Assert.Equal(
            AcquisitionPlanning.WindowFrom,
            manifest.GetProperty("WindowFromUtc").GetString());
        Assert.Equal(
            AcquisitionPlanning.WindowTo,
            manifest.GetProperty("WindowToUtc").GetString());

        var (from, to) = AcquisitionPlanning.Window();

        // Five years of sessions, near enough to size a payload and not near enough to judge one.
        Assert.InRange(AcquisitionPlanning.ExpectedSessions(from, to), 1240, 1280);
    }

    /// <summary>
    /// Two hundred and sixty-nine may be priced; one hundred and thirty-one may not; four hundred
    /// remain members. The third clause is the one that matters.
    /// </summary>
    [Fact]
    public void The_excluded_members_are_excluded_from_acquisition_and_not_from_the_universe()
    {
        var identities = Identities();
        var manifestCiks = ManifestCiks();

        Assert.Equal(Members, identities.Count);
        Assert.Equal(Members, manifestCiks.Count);

        var ready = identities.Where(r => r.Ready).ToList();
        var excluded = identities.Where(r => !r.Ready).ToList();

        Assert.Equal(Ready, ready.Count);
        Assert.Equal(NotReady, excluded.Count);

        // Nothing was dropped on the way: the two sets partition the four hundred exactly.
        Assert.Equal(Members, ready.Count + excluded.Count);

        // And every excluded member is still in the sealed manifest, by CIK.
        Assert.All(excluded, r => Assert.Contains(r.Cik, manifestCiks));
        Assert.All(ready, r => Assert.Contains(r.Cik, manifestCiks));
    }

    [Fact]
    public void No_unverified_identity_can_reach_the_acquisition_set()
    {
        var identities = Identities();

        foreach (var status in new[]
        {
            IdentityResolution.Provisional,
            IdentityResolution.Ambiguous,
            IdentityResolution.Unmatched,
        })
        {
            var carrying = identities.Where(r =>
                string.Equals(r.Status, status, StringComparison.Ordinal)).ToList();

            Assert.NotEmpty(carrying);
            Assert.All(carrying, r =>
            {
                Assert.False(r.Ready);
                Assert.False(AcquisitionPlanning.MayAcquire(r.Status));
            });
        }

        // The transport-failed member is unresolved, and unresolved is not acquirable however
        // plausible the symbol the delisted list offered for it looked.
        // The one transport failure was retried and recovered, so there may be none left. What
        // must hold is that any that remain are not acquirable - never that some remain.
        Assert.All(
            identities.Where(r => r.TransportFailed),
            r => Assert.False(r.Ready));

        // Every acquirable member, conversely, rests on EDGAR.
        Assert.All(
            identities.Where(r => r.Ready),
            r => Assert.True(AcquisitionPlanning.MayAcquire(r.Status)));
    }

    /// <summary>
    /// The finding that decides whether this acquisition is worth making at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The universe is survivorship-free: about twenty-nine per cent of its members stopped
    /// appearing before the window closed, comfortably over gate 2's five per cent floor. The set
    /// that can actually be priced is not. EDGAR strips a company's tickers when it deregisters,
    /// so the members that died are very nearly the same members whose symbols could not be
    /// established - and the priced panel comes out at under two per cent.
    /// </para>
    /// <para>
    /// This test does not judge that. It pins it, so that the number is a fact in the build rather
    /// than a paragraph in a report, and so that any later change which appears to fix it gets
    /// looked at rather than accepted.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_acquirable_set_now_clears_the_survivorship_floor_and_so_does_the_universe()
    {
        var identities = Identities().ToDictionary(r => r.Cik, StringComparer.Ordinal);
        var manifest = ManifestMembers();

        static bool Dropout(JsonElement member) =>
            member.TryGetProperty("Cohorts", out var cohorts) &&
            cohorts.ValueKind == JsonValueKind.Array &&
            cohorts.GetArrayLength() < 5;

        var universeDropouts = manifest.Count(Dropout);

        var acquirable = manifest
            .Where(m => identities[Cik(m)].Ready)
            .ToList();

        var acquirableDropouts = acquirable.Count(Dropout);

        Assert.Equal(Ready, acquirable.Count);

        // The universe clears the floor, as gate 2 already records.
        Assert.True(
            AcquisitionPlanning.ClearsSurvivorshipFloor(universeDropouts, Members),
            string.Create(
                CultureInfo.InvariantCulture,
                $"The sealed universe holds {universeDropouts} dropouts in {Members} members."));

        // And so, now, does the set that can actually be priced. Before the filing recovery this
        // assertion was its inverse: 5 dropouts in 269 members, 1.9%, a gate 2 failure hiding
        // behind a manifest that truthfully said 28.8%. Recovering identities from the issuers'
        // own filings is what closed that gap, and this is the assertion that says so.
        Assert.True(
            AcquisitionPlanning.ClearsSurvivorshipFloor(acquirableDropouts, Ready),
            string.Create(
                CultureInfo.InvariantCulture,
                $"The acquirable set holds {acquirableDropouts} dropouts in {Ready} members, which does not clear the floor."));

        // Most of the universe's dropouts can now be priced, where almost none could before.
        Assert.True(
            acquirableDropouts * 2 > universeDropouts,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Only {acquirableDropouts} of {universeDropouts} dropouts are acquirable."));
    }

    /// <summary>
    /// The manifest is byte-identical to the file that was sealed, hash and all.
    /// </summary>
    /// <remarks>
    /// The fingerprint inside the file says what the file claims to be; this says the file has not
    /// changed. Both are needed, because a rewrite that kept the fingerprint string would pass the
    /// first check and is exactly the edit nobody would notice. The literal below is the SHA-256
    /// of the sealed manifest as approved - 245,126 bytes - and no stage in this project is
    /// permitted to move it.
    /// </remarks>
    [Fact]
    public void The_sealed_manifest_is_byte_identical_to_the_file_that_was_approved()
    {
        var bytes = File.ReadAllBytes(ManifestPath());

        Assert.Equal(245126, bytes.Length);
        Assert.Equal(
            "c3667215b1e28c2093ed430e7ade180c40dfe616aef1f8d64e485721938f68db",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    [Fact]
    public void The_sealed_fingerprint_is_the_one_this_plan_was_written_against()
    {
        var manifest = ManifestText();

        Assert.True(IdentityResolution.IsSealedAs(manifest, SealedFingerprint));

        var identities = File.ReadAllText(IdentityPath());

        using var document = JsonDocument.Parse(identities);

        Assert.Equal(
            SealedFingerprint,
            document.RootElement.GetProperty("EvidenceBaseFingerprint").GetString());
    }

    /// <summary>
    /// Planning reads. It does not write, and it certainly does not fetch.
    /// </summary>
    /// <remarks>
    /// Hashing the two files either side of the whole planning computation is a cheap, total
    /// check: a stage that had quietly rewritten either of them would fail here rather than in a
    /// review six steps later.
    /// </remarks>
    [Fact]
    public void Planning_touches_nothing_it_reads()
    {
        var manifestBefore = Hash(ManifestText());
        var identitiesBefore = Hash(File.ReadAllText(IdentityPath()));

        var identities = Identities();

        var plan = identities
            .Where(r => r.Ready)
            .Select(r => (Symbol: AcquisitionPlanning.SymbolFor(r.Ticker!), r.Cik))
            .ToList();

        Assert.Equal(Ready, plan.Count);
        Assert.Equal(710, AcquisitionPlanning.BillableRequests(plan.Count));

        // Every planned symbol is distinct, so no member's series can be attributed to another.
        Assert.Equal(plan.Count, plan.Select(p => p.Symbol).Distinct(StringComparer.Ordinal).Count());
        Assert.All(plan, p => Assert.EndsWith(
            AcquisitionPlanning.UsSuffix, p.Symbol, StringComparison.Ordinal));

        Assert.Equal(manifestBefore, Hash(ManifestText()));
        Assert.Equal(identitiesBefore, Hash(File.ReadAllText(IdentityPath())));
    }

    // ---- reading, with no provider anywhere near it ---------------------------------------------

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static string ManifestPath() =>
        Universe.RepositoryPath("declarations", "universe-sample400-2021-2026.json");

    private static string IdentityPath() =>
        Universe.RepositoryPath("artifacts", "universe", "identity-sample400.json");

    private static string ManifestText() => File.ReadAllText(ManifestPath());

    private static JsonElement Manifest()
    {
        using var document = JsonDocument.Parse(ManifestText());

        // Cloned so it outlives the document, which is disposed here rather than left to a
        // finaliser that would hold the buffer for the whole run.
        return document.RootElement.Clone();
    }

    private static List<JsonElement> ManifestMembers() =>
        Manifest().GetProperty("Members").EnumerateArray().Select(e => e.Clone()).ToList();

    private static HashSet<string> ManifestCiks() =>
        ManifestMembers().Select(Cik).ToHashSet(StringComparer.Ordinal);

    private static string Cik(JsonElement member) =>
        member.GetProperty("Cik").GetString() ?? string.Empty;

    private static List<PlannedMember> Identities()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(IdentityPath()));

        return document.RootElement
            .GetProperty("Members")
            .EnumerateArray()
            .Select(e => new PlannedMember(
                e.GetProperty("Cik").GetString() ?? string.Empty,
                e.GetProperty("Status").GetString() ?? string.Empty,
                e.TryGetProperty("Ticker", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()
                    : null,
                e.GetProperty("Ready").GetBoolean(),
                e.TryGetProperty("TransportFailure", out var f) &&
                    f.ValueKind == JsonValueKind.String))
            .ToList();
    }

    private sealed record PlannedMember(
        string Cik,
        string Status,
        string? Ticker,
        bool Ready,
        bool TransportFailed);
}
