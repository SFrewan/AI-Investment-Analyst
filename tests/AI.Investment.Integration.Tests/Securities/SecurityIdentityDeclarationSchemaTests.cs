using System.Security.Cryptography;
using AI.Investment.Application.Securities;
using AI.Investment.Infrastructure.Identity;
using Xunit;

namespace AI.Investment.Integration.Tests.Securities;

/// <summary>
/// The schema transition: v1 still reads exactly as before, v2 reads, and neither becomes the other.
/// </summary>
/// <remarks>
/// <para>
/// Both files are read from the repository rather than from fixtures. A copy would drift, and the
/// thing under test is that the sealed evidence this repository carries is what the reader accepts.
/// </para>
/// <para>
/// No database is touched here, so these need no fixture and no authorisation. Reading a declaration
/// is the one part of the identity chain that has no side effect at all.
/// </para>
/// </remarks>
public sealed class SecurityIdentityDeclarationSchemaTests
{
    private const string PredecessorDigest =
        "3c26008918782eb703ffadb1b3fbf1566a10be51fb7b9056bb535b9235216f7d";

    private const string PredecessorSha256 =
        "a7ee0474269e2c6a7a0cc647c200ef481c4f87d49a6d8d0a06d431b50a65edf5";

    private const string ActiveDigest =
        "86c610e2de640f8f759f3cc5cb78489027c9f5605ee404b0ed18e9a84eb9fd28";

    private const string ActiveSha256 =
        "8f26afe7edd3baadd8b817467471af708cf8b20128bf90158fcd1ce6cdfa4f69";

    /// <summary>A. The predecessor still parses, under its own schema and its own digest rule.</summary>
    /// <remarks>
    /// The point is not that it parses but that it parses <em>unchanged</em>: same schema, same
    /// digest, same member count. Re-hashing a sealed file under a later rule would declare it
    /// broken, and a seal states what was sealed under the rule in force when it was written.
    /// </remarks>
    [Fact]
    public void The_predecessor_declaration_still_reads_exactly_as_before()
    {
        var v1 = SecurityIdentityDeclaration.Parse(Read(SecurityIdentityDeclaration.PredecessorRelativePath));

        Assert.Equal(SecurityIdentityDeclaration.SchemaV1, v1.Schema);
        Assert.Equal(PredecessorDigest, v1.IdentityDigest);
        Assert.Equal("security-identity-sample400-2021-2026", v1.DeclarationId);
        Assert.Equal(400, v1.Members.Count);
        Assert.Equal("us-pit-sample400-2021-09-to-2026-08@f78cfe45b962", v1.EvidenceBaseFingerprint);
    }

    /// <summary>H. The predecessor file is byte-identical to what F1a sealed.</summary>
    [Fact]
    public void The_predecessor_file_is_byte_identical()
    {
        Assert.Equal(PredecessorSha256, Sha256Of(SecurityIdentityDeclaration.PredecessorRelativePath));
    }

    /// <summary>B, E. The active declaration parses, and its digest verifies under v2's rule.</summary>
    [Fact]
    public void The_active_declaration_parses_and_its_digest_verifies()
    {
        var active = SecurityIdentityDeclaration.Parse(Read(SecurityIdentityDeclaration.RelativePath));

        Assert.Equal(SecurityIdentityDeclaration.SchemaV2, active.Schema);
        Assert.Equal(SecurityIdentityDeclaration.ActiveSchema, active.Schema);
        Assert.Equal(ActiveDigest, active.IdentityDigest);
        Assert.Equal("security-identity-sample400-2021-2026-v2", active.DeclarationId);
        Assert.Equal(400, active.Members.Count);

        // Parse recomputes the digest and refuses on a mismatch, so reaching this line is the
        // verification - the equality above only pins which value it verified against.
    }

    /// <summary>F, G. The F1a hashes are exactly what the artifact recorded.</summary>
    [Fact]
    public void The_active_declaration_carries_the_hashes_f1a_recorded()
    {
        Assert.Equal(ActiveSha256, Sha256Of(SecurityIdentityDeclaration.RelativePath));

        Assert.StartsWith("8f26afe7edd3baadd8b817467471af708cf8b20128", ActiveSha256, StringComparison.Ordinal);
        Assert.StartsWith("86c610e2de640f8f759f3cc5cb78489027c9f5605e", ActiveDigest, StringComparison.Ordinal);

        var active = SecurityIdentityDeclaration.Parse(Read(SecurityIdentityDeclaration.RelativePath));

        Assert.Equal(ActiveDigest, active.IdentityDigest);
    }

    /// <summary>The active declaration names the predecessor it supersedes, with its hashes.</summary>
    /// <remarks>
    /// The supersession chain has to be walkable from the file itself, not only from an artifact
    /// that lives outside version control.
    /// </remarks>
    [Fact]
    public void The_active_declaration_names_its_predecessor_and_that_predecessor_matches()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            Read(SecurityIdentityDeclaration.RelativePath));

        var supersedes = document.RootElement.GetProperty("Supersedes");

        Assert.Equal(
            SecurityIdentityDeclaration.PredecessorRelativePath,
            supersedes.GetProperty("File").GetString());

        Assert.Equal(PredecessorSha256, supersedes.GetProperty("Sha256").GetString());
        Assert.Equal(PredecessorDigest, supersedes.GetProperty("IdentityDigest").GetString());
        Assert.Equal(SecurityIdentityDeclaration.SchemaV1, supersedes.GetProperty("Schema").GetString());

        // And the file it names really is the one on disk.
        Assert.Equal(PredecessorSha256, Sha256Of(SecurityIdentityDeclaration.PredecessorRelativePath));
    }

    /// <summary>C. Under v2 the assertion's source is explicit, and every one is present.</summary>
    [Fact]
    public void Every_vendor_assertion_in_the_active_declaration_states_its_source()
    {
        var active = SecurityIdentityDeclaration.Parse(Read(SecurityIdentityDeclaration.RelativePath));

        var vendor = active.Members.Where(m => m.VendorSeries is not null).ToList();

        Assert.NotEmpty(vendor);
        Assert.All(vendor, m => Assert.False(string.IsNullOrWhiteSpace(m.VendorSeries!.SourceAuthority)));
    }

    /// <summary>I, J, K. The source separation F1a sealed, asserted against the active file.</summary>
    /// <remarks>
    /// <c>eodhd</c> is a vendor label and names no registered source; <c>eodhd-splits</c> is the
    /// corporate-actions feed and is the provenance of events, never the authority behind an
    /// identity assertion. Neither may appear as an assertion source.
    /// </remarks>
    [Fact]
    public void Vendor_assertions_are_sourced_eodhd_eod_and_nothing_else()
    {
        var active = SecurityIdentityDeclaration.Parse(Read(SecurityIdentityDeclaration.RelativePath));

        var sources = active.Members
            .Where(m => m.VendorSeries is not null)
            .Select(m => m.VendorSeries!.SourceAuthority)
            .Distinct()
            .ToList();

        Assert.Equal(["eodhd-eod"], sources);
        Assert.DoesNotContain("eodhd", sources);
        Assert.DoesNotContain("eodhd-splits", sources);
    }

    /// <summary>D, L, M. Intervals are explicit, and no ticker interval was invented.</summary>
    [Fact]
    public void Ticker_intervals_remain_explicitly_absent_and_vendor_intervals_explicitly_present()
    {
        var active = SecurityIdentityDeclaration.Parse(Read(SecurityIdentityDeclaration.RelativePath));

        // Read explicitly, and explicitly null: no source dates a symbol change, so no ticker
        // assertion may carry an interval.
        var symbols = active.Members.Where(m => m.Symbol is not null).Select(m => m.Symbol!).ToList();

        Assert.NotEmpty(symbols);
        Assert.All(symbols, s => Assert.Null(s.ValidFrom));
        Assert.All(symbols, s => Assert.Null(s.ValidTo));
        Assert.All(symbols, s => Assert.False(s.ValidityIntervalEvidenced));

        // Vendor series intervals are present on both ends for every assertion.
        var vendor = active.Members.Where(m => m.VendorSeries is not null).Select(m => m.VendorSeries!).ToList();

        Assert.All(vendor, v => Assert.True(v.FirstBarDate <= v.LastBarDate));
    }

    /// <summary>N. SHPW.US carries no vendor assertion, so it stays out of the persistable set.</summary>
    /// <remarks>
    /// Its only interval evidence came from a split row - <c>CloseObservations: 0</c> - so no
    /// interval could be attributed to the price feed. Re-sourcing it to the corporate-actions feed
    /// would have made a split its own identity evidence.
    /// </remarks>
    [Fact]
    public void Shpw_has_no_vendor_assertion_in_the_active_declaration()
    {
        var active = SecurityIdentityDeclaration.Parse(Read(SecurityIdentityDeclaration.RelativePath));

        Assert.DoesNotContain(
            active.Members.Where(m => m.VendorSeries is not null),
            m => m.VendorSeries!.Value == "SHPW.US");

        var shpw = active.Members.Single(m => m.Cik == "0001784851");

        Assert.Null(shpw.VendorSeries);

        // It remains a member with issuer-stated identity - excluded from persistence, not dropped.
        Assert.Equal(SecurityIdentityEvidence.IssuerStatedSecurityIdentity, shpw.IdentityClass);

        // The predecessor did carry it, which is what changed and why.
        var v1 = SecurityIdentityDeclaration.Parse(Read(SecurityIdentityDeclaration.PredecessorRelativePath));

        Assert.NotNull(v1.Members.Single(m => m.Cik == "0001784851").VendorSeries);
    }

    /// <summary>O. AXIL.US keeps the corrected lower bound.</summary>
    [Fact]
    public void Axil_carries_the_corrected_lower_bound()
    {
        var active = SecurityIdentityDeclaration.Parse(Read(SecurityIdentityDeclaration.RelativePath));

        var axil = active.Members.Single(m => m.VendorSeries?.Value == "AXIL.US").VendorSeries!;

        Assert.Equal(new DateOnly(2025, 4, 11), axil.FirstBarDate);
        Assert.NotEqual(new DateOnly(2024, 1, 16), axil.FirstBarDate);
        Assert.Equal("eodhd-eod", axil.SourceAuthority);

        // The predecessor's bound came from a split row, which is the correction.
        var v1 = SecurityIdentityDeclaration.Parse(Read(SecurityIdentityDeclaration.PredecessorRelativePath));

        Assert.Equal(
            new DateOnly(2024, 1, 16),
            v1.Members.Single(m => m.VendorSeries?.Value == "AXIL.US").VendorSeries!.FirstBarDate);
    }

    /// <summary>Identity content is otherwise unchanged between the two declarations.</summary>
    /// <remarks>
    /// F1a was a source-role correction. This pins that claim: every member, class, status, symbol
    /// and rejected candidate matches, so the only differences are the two recorded corrections.
    /// </remarks>
    [Fact]
    public void Identity_content_is_unchanged_between_the_predecessor_and_the_active_declaration()
    {
        var v1 = SecurityIdentityDeclaration.Parse(Read(SecurityIdentityDeclaration.PredecessorRelativePath));
        var v2 = SecurityIdentityDeclaration.Parse(Read(SecurityIdentityDeclaration.RelativePath));

        Assert.Equal(v1.Members.Count, v2.Members.Count);

        Assert.Equal(
            v1.Members.Select(m => (m.Cik, m.IdentityClass, m.ResolutionStatus, m.Symbol?.Value, m.RejectedSymbol)),
            v2.Members.Select(m => (m.Cik, m.IdentityClass, m.ResolutionStatus, m.Symbol?.Value, m.RejectedSymbol)));

        // The vendor assertions differ in exactly the two ways F1a recorded, and no others.
        var changed = v1.Members
            .Zip(v2.Members, (a, b) => (a, b))
            .Where(p => p.a.VendorSeries?.Value != p.b.VendorSeries?.Value
                || p.a.VendorSeries?.FirstBarDate != p.b.VendorSeries?.FirstBarDate
                || p.a.VendorSeries?.LastBarDate != p.b.VendorSeries?.LastBarDate)
            .Select(p => p.a.Cik)
            .ToList();

        Assert.Equal(["0001718500", "0001784851"], changed.OrderBy(c => c, StringComparer.Ordinal));
    }

    /// <summary>An unknown schema is refused rather than read as the nearest one it knows.</summary>
    [Fact]
    public void A_schema_the_reader_was_not_taught_is_refused()
    {
        var mutated = Read(SecurityIdentityDeclaration.RelativePath)
            .Replace(
                "\"Schema\": \"security-identity-evidence@2\"",
                "\"Schema\": \"security-identity-evidence@3\"",
                StringComparison.Ordinal);

        var thrown = Assert.ThrowsAny<Exception>(() => SecurityIdentityDeclaration.Parse(mutated));

        Assert.Contains("security-identity-evidence@3", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// v1 is not silently reinterpreted as v2: relabelling the predecessor breaks its digest.
    /// </summary>
    /// <remarks>
    /// The strongest available proof that the two digest rules are genuinely different. If v2's
    /// canonical form matched v1's, relabelling would parse cleanly and the extension would be
    /// decorative.
    /// </remarks>
    [Fact]
    public void Relabelling_the_predecessor_as_v2_is_refused_by_the_digest()
    {
        var relabelled = Read(SecurityIdentityDeclaration.PredecessorRelativePath)
            .Replace(
                "\"Schema\": \"security-identity-evidence@1\"",
                "\"Schema\": \"security-identity-evidence@2\"",
                StringComparison.Ordinal);

        var thrown = Assert.ThrowsAny<Exception>(() => SecurityIdentityDeclaration.Parse(relabelled));

        Assert.Contains("digest", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Under v2, changing an assertion's source breaks the seal. Under v1 it did not.</summary>
    /// <remarks>
    /// This is the hole F1a found, closed. The wrong source could be sealed under v1 precisely
    /// because v1's canonical form did not cover it.
    /// </remarks>
    [Fact]
    public void Changing_a_source_breaks_the_v2_seal_and_did_not_break_the_v1_seal()
    {
        var tamperedV2 = Read(SecurityIdentityDeclaration.RelativePath)
            .Replace("\"SourceAuthority\": \"eodhd-eod\"", "\"SourceAuthority\": \"eodhd\"", StringComparison.Ordinal);

        var thrown = Assert.ThrowsAny<Exception>(() => SecurityIdentityDeclaration.Parse(tamperedV2));

        Assert.Contains("digest", thrown.Message, StringComparison.OrdinalIgnoreCase);

        // The same edit against v1's rule changes nothing, because v1 never hashed the source.
        var tamperedV1 = Read(SecurityIdentityDeclaration.PredecessorRelativePath)
            .Replace("\"SourceAuthority\": \"eodhd\"", "\"SourceAuthority\": \"eodhd-eod\"", StringComparison.Ordinal);

        Assert.Equal(400, SecurityIdentityDeclaration.Parse(tamperedV1).Members.Count);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath));

    private static string Sha256Of(string relativePath) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(RepositoryRoot(), relativePath))))
            .ToLowerInvariant();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, SecurityIdentityDeclaration.RelativePath)))
        {
            directory = directory.Parent;
        }

        Assert.True(
            directory is not null,
            $"No repository root containing {SecurityIdentityDeclaration.RelativePath} was found " +
            $"above {AppContext.BaseDirectory}.");

        return directory!.FullName;
    }
}
