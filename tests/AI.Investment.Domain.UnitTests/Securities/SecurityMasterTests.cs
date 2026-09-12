using AI.Investment.Domain.Companies;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Securities;
using AI.Investment.Domain.Sources;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Securities;

/// <summary>
/// The D1 reference model: what a security is, where it trades, and what that meant on a date.
/// </summary>
/// <remarks>
/// These tests are about semantics, not coverage. Each one states a property the model would be
/// worthless without - identity that cannot be a symbol, a status that cannot escape its venue, a
/// history that cannot be rewritten, and a reconstruction that cannot see the future.
/// </remarks>
public sealed class SecurityMasterTests
{
    private static readonly DateTime Now = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);
    private static readonly SourceId Source = SourceId.Create("sec-edgar");
    private static readonly VenueId Nasdaq = VenueId.Create("XNAS");
    private static readonly VenueId Nyse = VenueId.Create("XNYS");

    // ---------------------------------------------------------------- A. Security identity

    [Fact]
    public void A_security_is_identified_by_a_surrogate_and_carries_no_symbol_at_all()
    {
        var security = Security.Create(SecurityId.New(), CompanyId.New(), Now);

        // The whole point: there is nowhere on this type to put a ticker. If a property ever
        // appears that could hold one, this stops compiling and the review happens.
        var names = typeof(Security).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain("Ticker", names);
        Assert.DoesNotContain("Symbol", names);
        Assert.DoesNotContain("Exchange", names);
        Assert.NotEqual(Guid.Empty, security.Id.Value);
    }

    [Fact]
    public void A_security_identity_cannot_be_changed_after_creation()
    {
        // No mutator exists for either half of identity. Asserted structurally rather than by
        // calling something, because the guarantee is the absence of the operation.
        var mutators = typeof(Security).GetMethods()
            .Where(m => m.DeclaringType == typeof(Security))
            .Select(m => m.Name)
            .ToList();

        Assert.DoesNotContain("ChangeId", mutators);
        Assert.DoesNotContain("ChangeCompany", mutators);
        Assert.DoesNotContain("ChangeListing", mutators);

        Assert.False(typeof(Security).GetProperty(nameof(Security.CompanyId))!.CanWrite
            && typeof(Security).GetProperty(nameof(Security.CompanyId))!.SetMethod!.IsPublic);
    }

    [Fact]
    public void A_security_must_name_the_company_that_issued_it()
    {
        var thrown = Assert.Throws<DomainValidationException>(
            () => Security.Create(SecurityId.New(), default, Now));

        Assert.Contains("issued", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_security_id_refuses_the_empty_guid()
    {
        Assert.Throws<ArgumentException>(() => SecurityId.Create(Guid.Empty));
    }

    // ---------------------------------------------------------------- B. Venue

    [Fact]
    public void A_venue_code_is_normalised_so_two_spellings_cannot_become_two_venues()
    {
        Assert.Equal(VenueId.Create("XNAS"), VenueId.Create(" xnas "));
    }

    [Fact]
    public void A_venue_covers_only_the_interval_it_states()
    {
        var venue = Venue.Create(
            Nasdaq,
            "Nasdaq",
            new DateOnly(2000, 1, 1),
            Now,
            countryCode: "us",
            validTo: new DateOnly(2020, 12, 31));

        Assert.Equal("US", venue.CountryCode);
        Assert.False(venue.CoversDate(new DateOnly(1999, 12, 31)));
        Assert.True(venue.CoversDate(new DateOnly(2000, 1, 1)));
        Assert.True(venue.CoversDate(new DateOnly(2020, 12, 31)));
        Assert.False(venue.CoversDate(new DateOnly(2021, 1, 1)));
    }

    [Fact]
    public void An_open_ended_venue_has_no_observed_end_which_is_not_a_claim_that_it_runs_forever()
    {
        var venue = Venue.Create(Nasdaq, "Nasdaq", new DateOnly(2000, 1, 1), Now);

        Assert.Null(venue.ValidTo);
        Assert.True(venue.CoversDate(new DateOnly(2999, 1, 1)));
    }

    [Fact]
    public void A_venue_cannot_stop_being_valid_before_it_starts()
    {
        Assert.Throws<DomainRuleViolationException>(() => Venue.Create(
            Nasdaq,
            "Nasdaq",
            new DateOnly(2020, 1, 1),
            Now,
            validTo: new DateOnly(2019, 12, 31)));
    }

    // ---------------------------------------------------------------- C/D. Listing and status

    [Fact]
    public void A_listing_joins_one_security_to_one_venue_and_holds_no_status_of_its_own()
    {
        var listing = Listing.Open(SecurityId.New(), Nasdaq, Now);

        Assert.DoesNotContain(
            "Status",
            typeof(Listing).GetProperties().Select(p => p.Name));

        Assert.Empty(listing.Events);
    }

    [Fact]
    public void A_listing_refuses_an_event_belonging_to_a_different_pairing()
    {
        var securityId = SecurityId.New();
        var listing = Listing.Open(securityId, Nasdaq, Now);

        var elsewhere = Event(securityId, Nyse, ListingStatus.Listed, new DateOnly(2021, 1, 4));

        var thrown = Assert.Throws<DomainRuleViolationException>(() => listing.Append(elsewhere));

        Assert.Contains("cannot be appended", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Removal_from_one_venue_says_nothing_about_another()
    {
        var securityId = SecurityId.New();

        var onNasdaq = Listing.Open(securityId, Nasdaq, Now);
        var onNyse = Listing.Open(securityId, Nyse, Now);

        onNasdaq.Append(Event(securityId, Nasdaq, ListingStatus.Listed, new DateOnly(2021, 1, 4)));
        onNasdaq.Append(Event(securityId, Nasdaq, ListingStatus.Removed, new DateOnly(2023, 6, 1)));
        onNyse.Append(Event(securityId, Nyse, ListingStatus.Listed, new DateOnly(2021, 1, 4)));

        var asOf = new DateOnly(2024, 1, 1);

        Assert.Equal(ListingStatus.Removed, onNasdaq.StatusAsAt(asOf, Now));

        // The security is still listed elsewhere. Nothing in the model can turn the removal above
        // into a market-wide statement, which is the SHPW lesson made structural.
        Assert.Equal(ListingStatus.Listed, onNyse.StatusAsAt(asOf, Now));
    }

    [Fact]
    public void An_unobserved_date_is_unknown_and_is_neither_listed_nor_removed()
    {
        var securityId = SecurityId.New();
        var listing = Listing.Open(securityId, Nasdaq, Now);

        listing.Append(Event(securityId, Nasdaq, ListingStatus.Listed, new DateOnly(2021, 1, 4)));

        Assert.Equal(ListingStatus.Unknown, listing.StatusAsAt(new DateOnly(2020, 12, 31), Now));
    }

    [Fact]
    public void A_suspension_is_not_a_removal_and_a_resumption_restores_the_listing()
    {
        var securityId = SecurityId.New();
        var listing = Listing.Open(securityId, Nasdaq, Now);

        listing.Append(Event(securityId, Nasdaq, ListingStatus.Listed, new DateOnly(2021, 1, 4)));
        listing.Append(Event(securityId, Nasdaq, ListingStatus.Suspended, new DateOnly(2022, 3, 1)));
        listing.Append(Event(securityId, Nasdaq, ListingStatus.Listed, new DateOnly(2022, 3, 15)));

        Assert.Equal(ListingStatus.Listed, listing.StatusAsAt(new DateOnly(2022, 2, 28), Now));
        Assert.Equal(ListingStatus.Suspended, listing.StatusAsAt(new DateOnly(2022, 3, 10), Now));
        Assert.Equal(ListingStatus.Listed, listing.StatusAsAt(new DateOnly(2022, 4, 1), Now));
    }

    [Fact]
    public void The_status_enum_defines_a_member_for_its_default_value()
    {
        Assert.Equal(ListingStatus.Unknown, default(ListingStatus));
        Assert.Equal(0, (int)ListingStatus.Unknown);
    }

    // ---------------------------------------------------------------- E/F. Event rules

    [Fact]
    public void A_listing_event_exposes_no_way_to_change_what_it_said()
    {
        var settable = typeof(ListingEvent).GetProperties()
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(settable);

        Assert.DoesNotContain(
            typeof(ListingEvent).GetMethods().Where(m => m.DeclaringType == typeof(ListingEvent)),
            m => m.Name.StartsWith("Set", StringComparison.Ordinal)
                || m.Name.StartsWith("Change", StringComparison.Ordinal)
                || m.Name.StartsWith("Update", StringComparison.Ordinal));
    }

    [Fact]
    public void A_listing_event_must_state_a_status_because_unknown_is_the_absence_of_evidence()
    {
        var thrown = Assert.Throws<DomainValidationException>(() => ListingEvent.Record(
            Guid.NewGuid(),
            SecurityId.New(),
            Nasdaq,
            ListingStatus.Unknown,
            new DateOnly(2021, 1, 4),
            "admitted",
            Source,
            Now,
            Now));

        Assert.Contains("absence of evidence", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_listing_event_must_carry_the_reason_the_source_gave()
    {
        Assert.Throws<DomainValidationException>(() => ListingEvent.Record(
            Guid.NewGuid(),
            SecurityId.New(),
            Nasdaq,
            ListingStatus.Removed,
            new DateOnly(2023, 6, 1),
            "   ",
            Source,
            Now,
            Now));
    }

    [Fact]
    public void A_listing_event_cannot_be_recorded_before_it_became_knowable()
    {
        var published = new DateTime(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

        var thrown = Assert.Throws<DomainRuleViolationException>(() => ListingEvent.Record(
            Guid.NewGuid(),
            SecurityId.New(),
            Nasdaq,
            ListingStatus.Listed,
            new DateOnly(2021, 1, 4),
            "admitted",
            Source,
            published,
            published.AddSeconds(-1)));

        Assert.Contains("before it became knowable", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_effective_date_is_taken_from_the_source_and_is_never_derived_from_an_instant()
    {
        // A future effective date is legitimate: a removal is announced before it takes effect.
        // Nothing clamps it to the publication instant, because that would be inventing a date.
        var announced = ListingEvent.Record(
            Guid.NewGuid(),
            SecurityId.New(),
            Nasdaq,
            ListingStatus.Removed,
            new DateOnly(2027, 1, 1),
            "form-25",
            Source,
            Now,
            Now);

        Assert.Equal(new DateOnly(2027, 1, 1), announced.EffectiveDate);
        Assert.True(announced.EffectiveDate.ToDateTime(TimeOnly.MinValue) > Now);
    }

    [Fact]
    public void A_listing_event_may_cite_archived_bytes_and_may_equally_cite_none()
    {
        var hash = ContentHash.Compute("payload"u8);

        var cited = ListingEvent.Record(
            Guid.NewGuid(), SecurityId.New(), Nasdaq, ListingStatus.Listed,
            new DateOnly(2021, 1, 4), "admitted", Source, Now, Now, hash);

        var uncited = ListingEvent.Record(
            Guid.NewGuid(), SecurityId.New(), Nasdaq, ListingStatus.Listed,
            new DateOnly(2021, 1, 4), "admitted", Source, Now, Now);

        Assert.Equal(hash, cited.EvidenceContentHash);
        Assert.Null(uncited.EvidenceContentHash);
    }

    // ---------------------------------------------------------------- G. Identifier assertions

    [Fact]
    public void An_identifier_is_an_assertion_carrying_an_interval_and_a_source()
    {
        var assertion = SecurityIdentifier.Assert(
            SecurityIdentifierKind.Ticker,
            " ABC ",
            new DateOnly(2021, 1, 1),
            new DateOnly(2022, 12, 31),
            Source);

        Assert.Equal("ABC", assertion.Value);
        Assert.Equal(Source, assertion.SourceId);
        Assert.False(assertion.CoversDate(new DateOnly(2020, 12, 31)));
        Assert.True(assertion.CoversDate(new DateOnly(2022, 12, 31)));
        Assert.False(assertion.CoversDate(new DateOnly(2023, 1, 1)));
    }

    [Fact]
    public void An_identifier_assertion_cannot_end_before_it_begins()
    {
        Assert.Throws<DomainRuleViolationException>(() => SecurityIdentifier.Assert(
            SecurityIdentifierKind.Isin,
            "US0000000000",
            new DateOnly(2022, 1, 1),
            new DateOnly(2021, 12, 31),
            Source));
    }

    [Fact]
    public void An_identifier_assertion_must_name_its_kind()
    {
        Assert.Throws<DomainValidationException>(() => SecurityIdentifier.Assert(
            SecurityIdentifierKind.Unknown, "ABC", new DateOnly(2021, 1, 1), null, Source));
    }

    [Fact]
    public void One_ticker_may_belong_to_two_securities_at_different_times()
    {
        // The reason a ticker is never a key, stated as a test: the same symbol, two issuers,
        // two non-overlapping intervals. A unique ticker column could not represent this.
        var first = Security.Create(SecurityId.New(), CompanyId.New(), Now);
        var second = Security.Create(SecurityId.New(), CompanyId.New(), Now);

        first.AssertIdentifier(SecurityIdentifier.Assert(
            SecurityIdentifierKind.Ticker, "ABC", new DateOnly(2015, 1, 1), new DateOnly(2019, 6, 30), Source));

        second.AssertIdentifier(SecurityIdentifier.Assert(
            SecurityIdentifierKind.Ticker, "ABC", new DateOnly(2020, 1, 1), null, Source));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Single(first.IdentifiersAsAt(SecurityIdentifierKind.Ticker, new DateOnly(2018, 1, 1)));
        Assert.Empty(first.IdentifiersAsAt(SecurityIdentifierKind.Ticker, new DateOnly(2021, 1, 1)));
        Assert.Single(second.IdentifiersAsAt(SecurityIdentifierKind.Ticker, new DateOnly(2021, 1, 1)));
    }

    [Fact]
    public void Two_sources_disagreeing_on_a_date_are_both_returned_rather_than_resolved_here()
    {
        var security = Security.Create(SecurityId.New(), CompanyId.New(), Now);
        var other = SourceId.Create("eodhd-eod");

        security.AssertIdentifier(SecurityIdentifier.Assert(
            SecurityIdentifierKind.Ticker, "ABC", new DateOnly(2021, 1, 1), null, Source));

        security.AssertIdentifier(SecurityIdentifier.Assert(
            SecurityIdentifierKind.Ticker, "ABC.US", new DateOnly(2021, 1, 1), null, other));

        Assert.Equal(2, security.IdentifiersAsAt(SecurityIdentifierKind.Ticker, new DateOnly(2022, 1, 1)).Count);
    }

    // ---------------------------------------------------------------- H/I. Ordering and PIT

    [Fact]
    public void A_reconstruction_uses_only_what_had_been_published_by_the_stated_instant()
    {
        var securityId = SecurityId.New();
        var listing = Listing.Open(securityId, Nasdaq, Now);

        var earlyKnowledge = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var lateKnowledge = new DateTime(2023, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        listing.Append(Event(
            securityId, Nasdaq, ListingStatus.Listed, new DateOnly(2021, 1, 4), earlyKnowledge));

        // Published later, but effective earlier - a backdated statement. A reader standing at
        // 2023-01-01 could not have known it, and must not see it.
        listing.Append(Event(
            securityId, Nasdaq, ListingStatus.Removed, new DateOnly(2022, 6, 1), lateKnowledge));

        var asOf = new DateOnly(2022, 12, 31);

        Assert.Equal(ListingStatus.Listed, listing.StatusAsAt(asOf, earlyKnowledge));
        Assert.Equal(ListingStatus.Removed, listing.StatusAsAt(asOf, lateKnowledge));
    }

    [Fact]
    public void A_later_correction_to_the_same_effective_date_wins_once_it_is_knowable()
    {
        var securityId = SecurityId.New();
        var listing = Listing.Open(securityId, Nasdaq, Now);

        var first = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var correction = new DateTime(2023, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        listing.Append(Event(
            securityId, Nasdaq, ListingStatus.Removed, new DateOnly(2022, 6, 1), first));

        listing.Append(Event(
            securityId, Nasdaq, ListingStatus.Suspended, new DateOnly(2022, 6, 1), correction));

        var asOf = new DateOnly(2022, 12, 31);

        Assert.Equal(ListingStatus.Removed, listing.StatusAsAt(asOf, first));
        Assert.Equal(ListingStatus.Suspended, listing.StatusAsAt(asOf, correction));
    }

    [Fact]
    public void Insertion_order_does_not_change_the_reconstructed_answer()
    {
        var securityId = SecurityId.New();

        var forwards = Listing.Open(securityId, Nasdaq, Now);
        var backwards = Listing.Open(securityId, Nasdaq, Now);

        var listed = Event(securityId, Nasdaq, ListingStatus.Listed, new DateOnly(2021, 1, 4));
        var suspended = Event(securityId, Nasdaq, ListingStatus.Suspended, new DateOnly(2022, 3, 1));
        var removed = Event(securityId, Nasdaq, ListingStatus.Removed, new DateOnly(2023, 6, 1));

        forwards.Append(listed);
        forwards.Append(suspended);
        forwards.Append(removed);

        backwards.Append(removed);
        backwards.Append(listed);
        backwards.Append(suspended);

        foreach (var asOf in new[]
        {
            new DateOnly(2021, 6, 1), new DateOnly(2022, 6, 1), new DateOnly(2024, 1, 1),
        })
        {
            Assert.Equal(forwards.StatusAsAt(asOf, Now), backwards.StatusAsAt(asOf, Now));
        }
    }

    [Fact]
    public void A_status_read_refuses_a_non_utc_knowledge_instant()
    {
        var listing = Listing.Open(SecurityId.New(), Nasdaq, Now);

        Assert.ThrowsAny<Exception>(() => listing.StatusAsAt(
            new DateOnly(2022, 1, 1),
            new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Local)));
    }

    // ---------------------------------------------------------------- K. Company compatibility

    [Fact]
    public void The_company_aggregate_owns_legal_identity_and_nothing_tradable()
    {
        // D1 added the reference model beside Company; D4 then narrowed Company onto it. What this
        // asserted before was that the narrowing had NOT happened yet - the sequencing guard. Now
        // that it has, the same place asserts the result: no tradable identity on the legal entity.
        Assert.Null(typeof(Company).GetMethod("ChangeListing"));
        Assert.Null(typeof(Company).GetProperty("Ticker"));
        Assert.Null(typeof(Company).GetProperty("Exchange"));
        Assert.NotNull(typeof(Company).GetProperty(nameof(Company.Cik)));

        // And Security is not a child of Company: it references the id, and holds no navigation.
        Assert.DoesNotContain(
            typeof(Security).GetProperties().Select(p => p.PropertyType),
            t => t == typeof(Company));
    }

    private static ListingEvent Event(
        SecurityId securityId,
        VenueId venueId,
        ListingStatus status,
        DateOnly effectiveDate,
        DateTime? publishedAtUtc = null)
    {
        var published = publishedAtUtc ?? Now;

        return ListingEvent.Record(
            Guid.NewGuid(),
            securityId,
            venueId,
            status,
            effectiveDate,
            status.ToString().ToLowerInvariant(),
            Source,
            published,
            published);
    }
}
