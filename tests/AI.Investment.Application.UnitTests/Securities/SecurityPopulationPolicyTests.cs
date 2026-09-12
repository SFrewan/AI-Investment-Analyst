using AI.Investment.Application.Securities;
using Xunit;

namespace AI.Investment.Application.UnitTests.Securities;

/// <summary>
/// Which members of the sealed identity declaration may become securities, and why the rest may not.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and exercised without a database, because these are the rules that decide whether the
/// platform creates an instrument. A rule that only ran inside an environment-gated integration
/// stage would be checked once, by hand, on the day it was written.
/// </para>
/// <para>
/// Every refusal has its own fact. Collapsing them into "not eligible" would let two different
/// reasons swap places without anything failing, and the reasons are the point: an issuer-identity
/// refusal says the evidence named the wrong kind of thing, while an ambiguity refusal says the
/// evidence named several and settled none.
/// </para>
/// </remarks>
public sealed class SecurityPopulationPolicyTests
{
    private static readonly VendorSeriesAssertion Series = new(
        "AE.US", "eodhd", new DateOnly(2021, 9, 1), new DateOnly(2025, 2, 4));

    /// <summary>A. The one shape that creates a security.</summary>
    [Fact]
    public void An_issuer_stated_equity_with_a_vendor_series_is_eligible()
    {
        var decision = SecurityPopulationPolicy.Decide(IssuerStated());

        Assert.True(decision.IsEligible);
        Assert.Equal(SecurityPopulationRefusal.None, decision.Refusal);
        Assert.Equal(Series, decision.VendorSymbol);
    }

    /// <summary>E. EDGAR named a ticker for the issuer. That is not the instrument.</summary>
    [Fact]
    public void Issuer_identity_only_is_refused_and_says_so()
    {
        var decision = SecurityPopulationPolicy.Decide(IssuerStated() with
        {
            IdentityClass = "issuer-identity-only",
            ResolutionStatus = "sec-only",
        });

        Assert.False(decision.IsEligible);
        Assert.Equal(SecurityPopulationRefusal.IssuerIdentityOnly, decision.Refusal);
        Assert.Null(decision.VendorSymbol);
    }

    /// <summary>F. A vendor name match is never promoted, however plausible it looked.</summary>
    [Fact]
    public void A_vendor_symbol_claim_is_refused_even_when_a_series_exists()
    {
        var decision = SecurityPopulationPolicy.Decide(IssuerStated() with
        {
            IdentityClass = "vendor-symbol-claim-only",
            ResolutionStatus = "eodhd-only-provisional",
            Symbol = new SymbolAssertion(
                "CMRE-PE",
                "eodhd-delisted-list-name-match",
                IsIssuerStated: false,
                IsAccepted: false,
                SecurityTitleObserved: null,
                ValidityIntervalEvidenced: false),
        });

        Assert.False(decision.IsEligible);
        Assert.Equal(SecurityPopulationRefusal.VendorSymbolClaimOnly, decision.Refusal);
    }

    /// <summary>
    /// G. No source named a symbol, and that is not the same as sources disagreeing.
    /// </summary>
    /// <remarks>
    /// The declaration marks these <c>no-candidate</c> rather than <c>none</c>, so a policy that
    /// simply refused every non-<c>none</c> ambiguity status would report all 27 of them as
    /// unresolved ambiguities and inflate that count more than fourfold.
    /// </remarks>
    [Fact]
    public void A_member_nobody_named_is_refused_as_having_no_evidence_not_as_ambiguous()
    {
        var decision = SecurityPopulationPolicy.Decide(IssuerStated() with
        {
            IdentityClass = "no-security-evidence",
            ResolutionStatus = "unmatched",
            AmbiguityStatus = "no-candidate",
            Symbol = null,
        });

        Assert.False(decision.IsEligible);
        Assert.Equal(SecurityPopulationRefusal.NoSecurityEvidence, decision.Refusal);
        Assert.NotEqual(SecurityPopulationRefusal.UnresolvedAmbiguity, decision.Refusal);
    }

    /// <summary>D. Several candidates, none accepted. It stays unresolved.</summary>
    [Fact]
    public void An_ambiguous_member_is_refused_and_no_candidate_is_chosen()
    {
        var decision = SecurityPopulationPolicy.Decide(IssuerStated() with
        {
            IdentityClass = "unresolved-ambiguous",
            ResolutionStatus = "ambiguous",
            AmbiguityStatus = "refused-multiple-candidates",
        });

        Assert.False(decision.IsEligible);
        Assert.Equal(SecurityPopulationRefusal.UnresolvedAmbiguity, decision.Refusal);
        Assert.Null(decision.VendorSymbol);
    }

    /// <summary>
    /// The filing was read and its security title says the symbol does not name a tradable equity.
    /// </summary>
    [Theory]
    [InlineData("debt")]
    [InlineData("not-a-symbol")]
    [InlineData(null)]
    public void A_security_title_that_is_not_equity_is_refused(string? title)
    {
        var decision = SecurityPopulationPolicy.Decide(IssuerStated() with
        {
            Symbol = IssuerStated().Symbol! with { SecurityTitleObserved = title },
        });

        Assert.False(decision.IsEligible);
        Assert.Equal(SecurityPopulationRefusal.SecurityTitleIsNotEquity, decision.Refusal);
    }

    /// <summary>
    /// C. The issuer named the instrument, but nothing can be asserted about it.
    /// </summary>
    /// <remarks>
    /// The refusal that is easiest to mistake for survivorship and is not. The member is not
    /// refused for lacking prices: it is refused because an identifier assertion requires a
    /// <c>ValidFrom</c>, the declaration evidences no interval for any ticker, and without a vendor
    /// series there is no evidenced interval of any kind. A security created here would carry no
    /// identifier at all - nothing could name it or tell a second run it already existed.
    /// </remarks>
    [Fact]
    public void An_issuer_stated_equity_with_no_persistable_identifier_is_refused()
    {
        var decision = SecurityPopulationPolicy.Decide(IssuerStated() with { VendorSeries = null });

        Assert.False(decision.IsEligible);
        Assert.Equal(SecurityPopulationRefusal.NoPersistableIdentifier, decision.Refusal);
    }

    /// <summary>
    /// C, restated as the rule that matters: a ticker string is never enough on its own.
    /// </summary>
    [Fact]
    public void A_ticker_without_an_evidenced_interval_never_makes_a_member_eligible()
    {
        var evidence = IssuerStated() with
        {
            VendorSeries = null,
            Symbol = IssuerStated().Symbol! with { ValidityIntervalEvidenced = false },
        };

        Assert.False(SecurityPopulationPolicy.Decide(evidence).IsEligible);

        // And the symbol is present and accepted throughout - it is the interval that is missing,
        // not the name.
        Assert.Equal("AE", evidence.Symbol!.Value);
        Assert.True(evidence.Symbol.IsAccepted);
    }

    /// <summary>K. The same evidence decides the same way, every time and in order.</summary>
    [Fact]
    public void The_decision_is_deterministic_across_repeated_runs()
    {
        var members = new[]
        {
            IssuerStated(),
            IssuerStated() with { IdentityClass = "issuer-identity-only" },
            IssuerStated() with { AmbiguityStatus = "refused-multiple-candidates" },
            IssuerStated() with { VendorSeries = null },
        };

        var first = SecurityPopulationPolicy.Decide(members);
        var second = SecurityPopulationPolicy.Decide(members);

        Assert.Equal(
            first.Select(d => (d.Cik, d.IsEligible, d.Refusal)),
            second.Select(d => (d.Cik, d.IsEligible, d.Refusal)));

        Assert.Equal(
            [
                SecurityPopulationRefusal.None,
                SecurityPopulationRefusal.IssuerIdentityOnly,
                SecurityPopulationRefusal.UnresolvedAmbiguity,
                SecurityPopulationRefusal.NoPersistableIdentifier,
            ],
            first.Select(d => d.Refusal));
    }

    /// <summary>Every input produces a decision. There is no path that returns nothing.</summary>
    [Fact]
    public void The_policy_is_total()
    {
        foreach (var identityClass in new[]
                 {
                     "issuer-stated-security-identity", "issuer-identity-only",
                     "vendor-symbol-claim-only", "no-security-evidence", "unresolved-ambiguous",
                     "something-a-later-declaration-invents",
                 })
        {
            var decision = SecurityPopulationPolicy.Decide(IssuerStated() with
            {
                IdentityClass = identityClass,
            });

            Assert.Equal(decision.IsEligible, decision.Refusal == SecurityPopulationRefusal.None);
        }
    }

    private static SecurityIdentityEvidence IssuerStated() =>
        new(
            "0000002178",
            "ADAMS RESOURCES & ENERGY, INC.",
            "issuer-stated-security-identity",
            "sec-filing-recovered",
            "none",
            new SymbolAssertion(
                "AE",
                "issuer-filing-cover-page",
                IsIssuerStated: true,
                IsAccepted: true,
                SecurityTitleObserved: "equity",
                ValidityIntervalEvidenced: false),
            Series);
}
