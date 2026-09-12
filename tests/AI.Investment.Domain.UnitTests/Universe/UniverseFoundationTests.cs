using AI.Investment.Domain.Coverage;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Securities;
using AI.Investment.Domain.Universe;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Universe;

/// <summary>
/// The D2 foundation: a sealed universe version, and who was in it.
/// </summary>
/// <remarks>
/// These test the two properties the model would be worthless without - that a version's identity is
/// a digest of its own content, so it cannot be edited and keep its name; and that membership stores
/// cohort cuts rather than a span, so the span stays derived and the history stays reproducible.
/// </remarks>
public sealed class UniverseFoundationTests
{
    private const string SealedFingerprint = "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private static readonly DateTime Now = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SealedAt = new(2026, 9, 4, 0, 34, 9, DateTimeKind.Utc);

    private static readonly DateOnly[] Cuts =
    [
        new(2021, 8, 31), new(2022, 8, 31), new(2023, 8, 31), new(2024, 8, 31), new(2025, 8, 31),
    ];

    // ---------------------------------------------------------------- 1/2. Identity

    [Fact]
    public void A_universe_id_wraps_the_sealed_fingerprint_and_splits_it()
    {
        var id = UniverseId.Create(SealedFingerprint);

        Assert.Equal(SealedFingerprint, id.Value);
        Assert.Equal("us-pit-sample400-2021-09-to-2026-08", id.BaseName);
        Assert.Equal("f78cfe45b962", id.Digest);
        Assert.Equal(UniverseId.DigestLength, id.Digest.Length);
    }

    [Theory]
    [InlineData("no-separator")]
    [InlineData("@f78cfe45b962")]
    [InlineData("base@")]
    [InlineData("base@tooshort")]
    [InlineData("base@F78CFE45B962")]
    [InlineData("base@f78cfe45b96z")]
    public void A_malformed_fingerprint_is_refused_rather_than_stored(string candidate)
    {
        Assert.Throws<DomainValidationException>(() => UniverseId.Create(candidate));
    }

    [Fact]
    public void Two_universes_with_the_same_fingerprint_are_the_same_version()
    {
        Assert.Equal(UniverseId.Create(SealedFingerprint), UniverseId.Create(SealedFingerprint));
        Assert.NotEqual(UniverseId.Create(SealedFingerprint), UniverseId.Create("other@f78cfe45b962"));
    }

    [Fact]
    public void Universe_identity_is_the_fingerprint_and_not_a_surrogate()
    {
        var universe = Seal();

        Assert.Equal(SealedFingerprint, universe.Id.Value);

        // No Guid key anywhere on the aggregate: the identity is content-derived by construction.
        Assert.DoesNotContain(
            typeof(global::AI.Investment.Domain.Universe.Universe).GetProperties(),
            p => p.PropertyType == typeof(Guid));
    }

    // ---------------------------------------------------------------- 3. Immutability

    [Fact]
    public void A_universe_exposes_no_operation_that_changes_a_sealed_version()
    {
        var mutators = typeof(global::AI.Investment.Domain.Universe.Universe).GetMethods()
            .Where(m => m.DeclaringType == typeof(global::AI.Investment.Domain.Universe.Universe))
            .Select(m => m.Name)
            .ToList();

        Assert.DoesNotContain(mutators, n =>
            n.StartsWith("Set", StringComparison.Ordinal)
            || n.StartsWith("Change", StringComparison.Ordinal)
            || n.StartsWith("Update", StringComparison.Ordinal)
            || n.StartsWith("Reseal", StringComparison.Ordinal));

        var settable = typeof(global::AI.Investment.Domain.Universe.Universe).GetProperties()
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(settable);
    }

    [Fact]
    public void There_is_no_current_universe_flag()
    {
        var names = typeof(global::AI.Investment.Domain.Universe.Universe)
            .GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(names, n =>
            n.Contains("Current", StringComparison.OrdinalIgnoreCase)
            || n.Contains("IsActive", StringComparison.OrdinalIgnoreCase)
            || n.Contains("IsLatest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_universe_must_state_the_cohort_cuts_it_was_built_from()
    {
        var thrown = Assert.Throws<DomainValidationException>(() => global::AI.Investment.Domain.Universe.Universe.Seal(
            UniverseId.Create(SealedFingerprint),
            new DateOnly(2021, 9, 1),
            new DateOnly(2026, 8, 31),
            [],
            SealedAt,
            ContentHash.Compute("manifest"u8),
            "population",
            "membership",
            Now));

        Assert.Contains("no member's span can be derived", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_universe_window_cannot_end_before_it_starts()
    {
        Assert.Throws<DomainRuleViolationException>(() => global::AI.Investment.Domain.Universe.Universe.Seal(
            UniverseId.Create(SealedFingerprint),
            new DateOnly(2026, 8, 31),
            new DateOnly(2021, 9, 1),
            Cuts,
            SealedAt,
            ContentHash.Compute("manifest"u8),
            "population",
            "membership",
            Now));
    }

    // ---------------------------------------------------------------- 8. Cohort fidelity

    [Fact]
    public void Cohort_cut_dates_are_kept_exactly_normalised_to_ascending_and_distinct()
    {
        var universe = global::AI.Investment.Domain.Universe.Universe.Seal(
            UniverseId.Create(SealedFingerprint),
            new DateOnly(2021, 9, 1),
            new DateOnly(2026, 8, 31),
            [Cuts[4], Cuts[0], Cuts[2], Cuts[0], Cuts[1], Cuts[3]],
            SealedAt,
            ContentHash.Compute("manifest"u8),
            "population",
            "membership",
            Now);

        Assert.Equal(Cuts, universe.CohortCutDates);
        Assert.Equal(new DateOnly(2025, 8, 31), universe.FinalCut);
    }

    [Fact]
    public void The_final_cut_is_derived_and_not_a_second_stored_copy()
    {
        // A stored FinalCut could disagree with the list beside it. Asserting it is computed keeps
        // the two from drifting.
        Assert.Null(typeof(global::AI.Investment.Domain.Universe.Universe)
            .GetProperty(nameof(global::AI.Investment.Domain.Universe.Universe.FinalCut))!.SetMethod);
    }

    // ---------------------------------------------------------------- 12. Point-in-time

    [Fact]
    public void A_version_is_visible_only_to_a_reader_standing_at_or_after_its_seal()
    {
        var universe = Seal();

        Assert.False(universe.WasSealedBy(SealedAt.AddTicks(-1)));
        Assert.True(universe.WasSealedBy(SealedAt));
        Assert.True(universe.WasSealedBy(Now));
    }

    [Fact]
    public void Point_in_time_selection_takes_the_version_sealed_at_or_before_the_instant()
    {
        var older = Seal("older@aaaaaaaaaaaa", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = Seal("newer@bbbbbbbbbbbb", new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var versions = new[] { older, newer };
        var asOf = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        var selected = versions
            .Where(u => u.WasSealedBy(asOf))
            .OrderByDescending(u => u.SealedAtUtc)
            .First();

        // The older one, because the newer did not exist yet. Selecting "the current universe"
        // would have returned the newer and answered a March question with June's population.
        Assert.Equal(older.Id, selected.Id);
    }

    [Fact]
    public void A_seal_check_refuses_a_non_utc_instant()
    {
        var universe = Seal();

        Assert.ThrowsAny<Exception>(
            () => universe.WasSealedBy(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local)));
    }

    // ---------------------------------------------------------------- 4/5/6. Membership

    [Fact]
    public void A_membership_names_a_universe_and_a_security_and_nothing_else()
    {
        var membership = Member(Cuts[..3]);

        Assert.Equal(SealedFingerprint, membership.UniverseId.Value);
        Assert.NotEqual(default, membership.SecurityId);

        var names = typeof(UniverseMembership).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain("CompanyId", names);
        Assert.DoesNotContain("Cik", names);
        Assert.DoesNotContain("Ticker", names);
        Assert.DoesNotContain("Symbol", names);
    }

    [Fact]
    public void A_membership_exposes_no_operation_that_changes_it()
    {
        var settable = typeof(UniverseMembership).GetProperties()
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(settable);

        Assert.DoesNotContain(
            typeof(UniverseMembership).GetMethods().Where(m => m.DeclaringType == typeof(UniverseMembership)),
            m => m.Name.StartsWith("Set", StringComparison.Ordinal)
                || m.Name.StartsWith("Change", StringComparison.Ordinal)
                || m.Name.StartsWith("Update", StringComparison.Ordinal));
    }

    [Fact]
    public void A_member_present_at_no_cut_was_not_a_member()
    {
        var thrown = Assert.Throws<DomainValidationException>(
            () => UniverseMembership.Record(Guid.NewGuid(), UniverseId.Create(SealedFingerprint), SecurityId.New(), [], Now));

        Assert.Contains("was not a member", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Membership_cuts_are_kept_exactly_normalised_to_ascending_and_distinct()
    {
        var membership = Member([Cuts[2], Cuts[0], Cuts[2], Cuts[1]]);

        Assert.Equal([Cuts[0], Cuts[1], Cuts[2]], membership.CohortCuts);
    }

    [Fact]
    public void Survival_is_decided_against_the_universes_final_cut()
    {
        var universe = Seal();

        var survivor = Member(Cuts);
        var departed = Member(Cuts[..3]);

        Assert.True(survivor.SurvivedTo(universe.FinalCut));
        Assert.False(departed.SurvivedTo(universe.FinalCut));
    }

    /// <summary>
    /// The survivorship doctrine: a member that left is still a member of the version it was in.
    /// </summary>
    [Fact]
    public void A_member_that_left_the_universe_keeps_its_membership_record()
    {
        var departed = Member([Cuts[0], Cuts[1]]);

        Assert.Equal(2, departed.CohortCuts.Count);
        Assert.False(departed.SurvivedTo(new DateOnly(2025, 8, 31)));

        // Nothing removes it, and nothing marks it removed. It simply holds the cuts it held.
        Assert.DoesNotContain(
            typeof(UniverseMembership).GetProperties().Select(p => p.Name),
            n => n.Contains("Removed", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Delisted", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Exited", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- 7. Span stays derived

    [Fact]
    public void A_membership_stores_cuts_and_never_a_span()
    {
        var names = typeof(UniverseMembership).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain("SpanFrom", names);
        Assert.DoesNotContain("SpanTo", names);
        Assert.DoesNotContain("MembershipSpan", names);
        Assert.DoesNotContain("Span", names);
    }

    /// <summary>
    /// The stored cuts feed the existing derivation, which is why they are what is stored.
    /// </summary>
    /// <remarks>
    /// This is the join between D1b and D2: <c>MembershipSpan.Derive</c> already takes cohorts, a
    /// final cut and a window, and those are exactly what a universe and a membership hold. Proving
    /// it here means the shape was not chosen to look right.
    /// </remarks>
    [Fact]
    public void The_stored_cuts_and_window_derive_the_span_through_the_existing_domain_rule()
    {
        var universe = Seal();

        var survivor = Member(Cuts);
        var departed = Member(Cuts[..3]);

        var survivorSpan = MembershipSpan.Derive(
            survivor.CohortCuts, universe.FinalCut, universe.WindowFrom, universe.WindowTo);

        var departedSpan = MembershipSpan.Derive(
            departed.CohortCuts, universe.FinalCut, universe.WindowFrom, universe.WindowTo);

        // The survivor runs to the window end; the departed stops at its own last cut. Both starts
        // are floored at the window, which is the case the three 2021-08-31 members sit on.
        Assert.Equal((universe.WindowFrom, universe.WindowTo), survivorSpan);
        Assert.Equal((universe.WindowFrom, new DateOnly(2023, 8, 31)), departedSpan);
    }

    // ---------------------------------------------------------------- 9/10. Identity hygiene

    [Fact]
    public void No_ticker_based_identity_exists_anywhere_in_the_universe_model()
    {
        foreach (var type in new[] { typeof(global::AI.Investment.Domain.Universe.Universe), typeof(UniverseMembership), typeof(UniverseId) })
        {
            Assert.DoesNotContain(
                type.GetProperties().Select(p => p.Name),
                n => n.Contains("Ticker", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("Symbol", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Cik_is_not_a_security_identifier_kind()
    {
        // The D2 decision, asserted: CIK identifies a legal entity, so it belongs to Company and
        // must never arrive here as a security-level identifier kind.
        var kinds = Enum.GetNames<SecurityIdentifierKind>();

        Assert.DoesNotContain(kinds, k => k.Contains("Cik", StringComparison.OrdinalIgnoreCase));
    }

    private static global::AI.Investment.Domain.Universe.Universe Seal(
        string fingerprint = SealedFingerprint,
        DateTime? sealedAtUtc = null) =>
        global::AI.Investment.Domain.Universe.Universe.Seal(
            UniverseId.Create(fingerprint),
            new DateOnly(2021, 9, 1),
            new DateOnly(2026, 8, 31),
            Cuts,
            sealedAtUtc ?? SealedAt,
            ContentHash.Compute("manifest"u8),
            "Assets, ranked 2020, systematic sample",
            "A member is in the universe at a cut if it met the rule at that cut.",
            Now);

    private static UniverseMembership Member(IEnumerable<DateOnly> cuts) =>
        UniverseMembership.Record(
            Guid.NewGuid(), UniverseId.Create(SealedFingerprint), SecurityId.New(), cuts, Now);
}
