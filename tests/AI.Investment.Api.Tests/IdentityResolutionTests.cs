using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The rules that decide which security the platform would buy.
/// </summary>
/// <remarks>
/// <para>
/// These ran as a gated, database-backed stage once and produced the right answer once. That is not
/// the same as being correct: the interesting cases here - a preferred line matched by name, a
/// vendor's suffix mistaken for a different ticker, a transport failure quietly falling back to a
/// guess - appear a handful of times in four hundred members and would not be noticed if a later
/// change broke them. Every branch is pinned as a fact that runs in the ordinary suite, needs no
/// database, and takes microseconds.
/// </para>
/// </remarks>
public sealed class IdentityResolutionTests
{
    /// <summary>
    /// The thirty-six real conflicts. EDGAR named a different security and EDGAR is the authority.
    /// </summary>
    [Fact]
    public void SEC_evidence_wins_over_a_delisted_list_name_match()
    {
        var resolution = IdentityResolution.Resolve(secTicker: "NTRS", provisionalTicker: "NTRSP.US");

        Assert.Equal(IdentityResolution.Conflict, resolution.Status);
        Assert.Equal("NTRS", resolution.Ticker);

        // The losing answer is kept, not erased. NTRSP is Northern Trust's preferred line, and a
        // reader who cannot see what was rejected cannot check the rejection.
        Assert.Equal("NTRSP.US", resolution.PreviousTicker);
        Assert.NotNull(resolution.Conflict);

        Assert.True(IdentityResolution.IsAuthoritative(resolution.Status));
        Assert.True(IdentityResolution.Assess(resolution, transportFailed: false).Ready);
    }

    /// <summary>
    /// One ticker in two notations is not a disagreement, and reporting it as one buries the
    /// thirty-six that are.
    /// </summary>
    [Fact]
    public void A_vendor_suffix_does_not_manufacture_a_conflict()
    {
        var resolution = IdentityResolution.Resolve(secTicker: "DHIL", provisionalTicker: "DHIL.US");

        Assert.Equal(IdentityResolution.Confirmed, resolution.Status);
        Assert.Equal("DHIL", resolution.Ticker);
        Assert.Null(resolution.Conflict);
    }

    /// <summary>
    /// And the converse: a share-class marker is not a suffix. A preferred line and its common
    /// stock are different securities, and collapsing them is the error the whole class prevents.
    /// </summary>
    [Fact]
    public void A_share_class_marker_is_not_normalised_away()
    {
        var resolution = IdentityResolution.Resolve(secTicker: "AIV", provisionalTicker: "AIV-PA.US");

        Assert.Equal(IdentityResolution.Conflict, resolution.Status);
        Assert.Equal("AIV", resolution.Ticker);
    }

    /// <summary>
    /// BGEPF stands for this case: an exact name match, and the wrong tradable line.
    /// </summary>
    [Fact]
    public void A_provisional_identity_never_becomes_authoritative_without_SEC_evidence()
    {
        var resolution = IdentityResolution.Resolve(secTicker: null, provisionalTicker: "BGEPF.US");

        Assert.Equal(IdentityResolution.Provisional, resolution.Status);
        Assert.Equal("BGEPF.US", resolution.Ticker);
        Assert.False(IdentityResolution.IsAuthoritative(resolution.Status));

        var readiness = IdentityResolution.Assess(resolution, transportFailed: false);

        Assert.False(readiness.Ready);
        Assert.Contains("BGEPF", readiness.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ambiguous_name_match_is_refused_rather_than_resolved()
    {
        var resolution = IdentityResolution.Resolve(
            secTicker: null,
            provisionalTicker: "RAD.US",
            ambiguous: true);

        Assert.Equal(IdentityResolution.Ambiguous, resolution.Status);

        // No ticker survives an ambiguity. Choosing one would be the guess this refuses to make.
        Assert.Null(resolution.Ticker);
        Assert.False(IdentityResolution.Assess(resolution, transportFailed: false).Ready);
    }

    /// <summary>
    /// A transport failure is a missing answer, never a licence to use the other source's guess.
    /// </summary>
    [Fact]
    public void A_transport_failure_is_recorded_and_never_falls_back_to_a_name_match()
    {
        var nothing = IdentityResolution.Resolve(secTicker: null, provisionalTicker: null);

        Assert.Equal(IdentityResolution.Unmatched, nothing.Status);
        Assert.Null(nothing.Ticker);

        var readiness = IdentityResolution.Assess(nothing, transportFailed: true);

        Assert.False(readiness.Ready);
        Assert.Contains("could not be retrieved", readiness.Reason, StringComparison.Ordinal);

        // And with a name match available, the failure still does not promote it.
        var guessed = IdentityResolution.Resolve(secTicker: null, provisionalTicker: "SOMETHING.US");

        Assert.Equal(IdentityResolution.Provisional, guessed.Status);
        Assert.False(IdentityResolution.IsAuthoritative(guessed.Status));
        Assert.False(IdentityResolution.Assess(guessed, transportFailed: true).Ready);
    }

    /// <summary>
    /// Every input produces a member. No combination of missing evidence removes one.
    /// </summary>
    /// <remarks>
    /// The sealed universe is more important than the ticker list, so the resolver is total by
    /// construction: there is no null return and no exception path that a caller could turn into a
    /// dropped row while iterating four hundred of them.
    /// </remarks>
    [Fact]
    public void An_unidentifiable_member_is_still_a_member()
    {
        string?[] tickers = [null, "", "   ", "ABC", "ABC.US"];

        foreach (var sec in tickers)
        {
            foreach (var provisional in tickers)
            {
                foreach (var ambiguous in new[] { false, true })
                {
                    var resolution = IdentityResolution.Resolve(sec, provisional, ambiguous);

                    Assert.NotNull(resolution);
                    Assert.False(string.IsNullOrEmpty(resolution.Status));

                    // Readiness is likewise total, and says why whenever it says no.
                    var readiness = IdentityResolution.Assess(resolution, transportFailed: false);

                    Assert.True(readiness.Ready == (readiness.Reason is null));
                }
            }
        }
    }

    /// <summary>
    /// The source names what supplied the symbol, never what was merely asked.
    /// </summary>
    /// <remarks>
    /// EDGAR is queried for all four hundred members, so labelling every row "EDGAR submissions"
    /// is true of the request and false of the evidence: it tells a reader that EDGAR supplied an
    /// identity for members EDGAR answered without naming a ticker at all. The label decides
    /// whether someone reading the table believes the symbol.
    /// </remarks>
    [Fact]
    public void The_source_names_what_supplied_the_symbol_not_what_was_queried()
    {
        Assert.Equal(
            "EDGAR submissions",
            IdentityResolution.SymbolSource(IdentityResolution.Confirmed, transportFailed: false));
        Assert.Equal(
            "EDGAR submissions",
            IdentityResolution.SymbolSource(IdentityResolution.Conflict, transportFailed: false));
        Assert.Equal(
            "EDGAR submissions",
            IdentityResolution.SymbolSource(IdentityResolution.SecOnly, transportFailed: false));

        // The delisted list supplied it, and the label has to say so - this is the row a reader
        // must not mistake for EDGAR evidence.
        Assert.StartsWith(
            "EODHD",
            IdentityResolution.SymbolSource(IdentityResolution.Provisional, transportFailed: false),
            StringComparison.Ordinal);

        // Candidates were supplied and none accepted, which is a third thing again.
        var ambiguous = IdentityResolution.SymbolSource(
            IdentityResolution.Ambiguous,
            transportFailed: false);

        Assert.StartsWith("EODHD", ambiguous, StringComparison.Ordinal);
        Assert.Contains("ambiguous", ambiguous, StringComparison.Ordinal);

        // It is the one status with a source and no accepted symbol, and the label says both:
        // candidates came from the delisted list, and none of them was taken.
        Assert.Null(
            IdentityResolution.Resolve(null, "RAD.US", ambiguous: true).Ticker);
        Assert.Contains("none accepted", ambiguous, StringComparison.Ordinal);
    }

    [Fact]
    public void A_member_with_no_symbol_never_claims_EDGAR_supplied_its_identity()
    {
        var nothing = IdentityResolution.SymbolSource(
            IdentityResolution.Unmatched,
            transportFailed: false);

        Assert.Equal("none", nothing);

        // A transport failure is distinguishable from "EDGAR answered and named nothing", because
        // the two mean different things about how much is known.
        var unreachable = IdentityResolution.SymbolSource(
            IdentityResolution.Unmatched,
            transportFailed: true);

        Assert.StartsWith("none", unreachable, StringComparison.Ordinal);
        Assert.Contains("could not be retrieved", unreachable, StringComparison.Ordinal);

        // No status without an accepted symbol may be labelled as sourced from EDGAR.
        foreach (var status in new[]
        {
            IdentityResolution.Provisional,
            IdentityResolution.Ambiguous,
            IdentityResolution.Unmatched,
        })
        {
            foreach (var failed in new[] { false, true })
            {
                Assert.False(IdentityResolution.IsAuthoritative(status));
                Assert.DoesNotContain(
                    "EDGAR submissions",
                    IdentityResolution.SymbolSource(status, failed),
                    StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// An ambiguity is not a disagreement, and must not be counted as one.
    /// </summary>
    /// <remarks>
    /// Both carry a conflict message, so selecting the conflict table on "has a message" swept in
    /// twenty-six ambiguous members with blank symbol columns and reported sixty-two conflicts
    /// where there are thirty-six. The count is the thing a reader uses to judge how often the
    /// delisted list was wrong, so inflating it with cases EDGAR never adjudicated is not a
    /// cosmetic error.
    /// </remarks>
    [Fact]
    public void An_ambiguous_match_is_not_a_conflict_and_is_excluded_from_the_conflict_table()
    {
        var ambiguous = IdentityResolution.Resolve(
            secTicker: null,
            provisionalTicker: "RAD.US",
            ambiguous: true);

        Assert.NotNull(ambiguous.Conflict);
        Assert.False(IdentityResolution.IsConflict(ambiguous.Status));

        // A genuine conflict has both a winner and a loser to show.
        var conflict = IdentityResolution.Resolve(secTicker: "NTRS", provisionalTicker: "NTRSP.US");

        Assert.True(IdentityResolution.IsConflict(conflict.Status));
        Assert.NotNull(conflict.Ticker);
        Assert.NotNull(conflict.PreviousTicker);

        // Selecting on the message would return both; selecting on the status returns one.
        var rows = new[] { ambiguous, conflict };

        var byMessage = rows.Where(r => r.Conflict is not null).ToList();
        var byStatus = rows.Where(r => IdentityResolution.IsConflict(r.Status)).ToList();

        Assert.Contains(ambiguous, byMessage);
        Assert.Contains(conflict, byMessage);

        // The one the table should show, and only that one.
        var only = Assert.Single(byStatus);

        Assert.Same(conflict, only);
        Assert.DoesNotContain(ambiguous, byStatus);

        // And it has symbols in both columns, which is what the table is for.
        Assert.NotNull(only.Ticker);
        Assert.NotNull(only.PreviousTicker);
    }

    /// <summary>
    /// Notation is not disagreement: the normalised pair must stay out of the conflict count.
    /// </summary>
    [Fact]
    public void The_normalised_pair_is_confirmed_and_never_counted_as_a_conflict()
    {
        var resolution = IdentityResolution.Resolve(secTicker: "DHIL", provisionalTicker: "DHIL.US");

        Assert.Equal(IdentityResolution.Confirmed, resolution.Status);
        Assert.False(IdentityResolution.IsConflict(resolution.Status));
        Assert.Null(resolution.Conflict);
        Assert.Equal(
            "EDGAR submissions",
            IdentityResolution.SymbolSource(resolution.Status, transportFailed: false));
    }

    /// <summary>
    /// An identity recovered from the issuer's own filing is SEC evidence, and is authoritative.
    /// </summary>
    /// <remarks>
    /// EDGAR's submissions document loses a company's tickers when it deregisters; the company's
    /// own 10-K does not, and cannot be withdrawn. That is what recovered the members that died -
    /// Bed Bath &amp; Beyond filing under a successor shell's name, Meredith under an acquirer's -
    /// where no vendor list could have. It is admitted only after the security classification
    /// confirms the symbol names an equity rather than a bond.
    /// </remarks>
    [Fact]
    public void An_identity_recovered_from_a_filing_is_authoritative_and_acquirable()
    {
        Assert.True(IdentityResolution.IsAuthoritative(IdentityResolution.FilingRecovered));

        // Its source is the filing, not the submissions document, and the label says which.
        Assert.Equal(
            "SEC filing cover page, issuer-stated",
            IdentityResolution.SymbolSource(IdentityResolution.FilingRecovered, transportFailed: false));

        // It is a distinct status: it does not collapse into sec-only or into a name match.
        Assert.NotEqual(IdentityResolution.SecOnly, IdentityResolution.FilingRecovered);
        Assert.NotEqual(IdentityResolution.Provisional, IdentityResolution.FilingRecovered);

        // And the sources that are still not authoritative have not become so.
        Assert.False(IdentityResolution.IsAuthoritative(IdentityResolution.Provisional));
        Assert.False(IdentityResolution.IsAuthoritative(IdentityResolution.Ambiguous));
        Assert.False(IdentityResolution.IsAuthoritative(IdentityResolution.Unmatched));
    }

    /// <summary>
    /// The seal is checked against the value that was approved, not against the file's own claim.
    /// </summary>
    [Fact]
    public void A_manifest_that_is_not_the_sealed_one_is_refused()
    {
        const string expected = "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

        Assert.True(IdentityResolution.IsSealedAs(
            """{"EvidenceBaseFingerprint":"us-pit-sample400-2021-09-to-2026-08@f78cfe45b962"}""",
            expected));

        // One character different is a different universe.
        Assert.False(IdentityResolution.IsSealedAs(
            """{"EvidenceBaseFingerprint":"us-pit-sample400-2021-09-to-2026-08@f78cfe45b963"}""",
            expected));

        // And a manifest that carries no fingerprint at all is not the sealed one either.
        Assert.False(IdentityResolution.IsSealedAs("""{"Members":[]}""", expected));
    }
}
