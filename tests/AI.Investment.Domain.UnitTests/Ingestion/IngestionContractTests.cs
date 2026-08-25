using System.Text;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Ingestion;

public sealed class ContentHashTests
{
    /// <summary>
    /// The known SHA-256 of the empty input. A hash implementation that is merely self-consistent
    /// would pass every test that only compares its own output against itself.
    /// </summary>
    [Fact]
    public void The_hash_matches_the_published_SHA256_of_empty_input() =>
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ContentHash.Compute([]).Value);

    [Fact]
    public void The_hash_matches_the_published_SHA256_of_abc() =>
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ContentHash.Compute(Encoding.UTF8.GetBytes("abc")).Value);

    /// <summary>
    /// Content addressing only deduplicates if identical bytes produce an identical address.
    /// </summary>
    [Fact]
    public void Identical_payloads_hash_to_the_same_value()
    {
        var first = ContentHash.Compute(Encoding.UTF8.GetBytes("{\"a\":1}"));
        var second = ContentHash.Compute(Encoding.UTF8.GetBytes("{\"a\":1}"));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Different_payloads_hash_to_different_values() =>
        Assert.NotEqual(
            ContentHash.Compute(Encoding.UTF8.GetBytes("{\"a\":1}")),
            ContentHash.Compute(Encoding.UTF8.GetBytes("{\"a\":2}")));

    [Fact]
    public void A_stored_hash_round_trips_through_Create()
    {
        var computed = ContentHash.Compute(Encoding.UTF8.GetBytes("payload"));

        Assert.Equal(computed, ContentHash.Create(computed.Value));
    }

    [Fact]
    public void An_upper_case_hash_is_normalised() =>
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ContentHash.Create("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855").Value);

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("g3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    public void A_value_that_is_not_a_sha256_digest_is_rejected(string value) =>
        Assert.Throws<DomainValidationException>(() => ContentHash.Create(value));

    [Fact]
    public void Abbreviated_is_the_leading_twelve_characters() =>
        Assert.Equal("e3b0c44298fc", ContentHash.Compute([]).Abbreviated);
}

public sealed class IngestionSubjectTests
{
    [Fact]
    public void A_specific_subject_carries_an_identifier()
    {
        var subject = IngestionSubject.Create("Company", "AAPL");

        Assert.True(subject.IsSpecific);
        Assert.Equal("Company:AAPL", subject.ToString());
    }

    /// <summary>
    /// A sweep and a specific request must not be indistinguishable, so the absence of an
    /// identifier is modelled rather than papered over with a placeholder.
    /// </summary>
    [Fact]
    public void A_sweep_carries_no_identifier()
    {
        var subject = IngestionSubject.Sweep("RegulatoryFiling");

        Assert.False(subject.IsSpecific);
        Assert.Null(subject.Identifier);
        Assert.Equal("RegulatoryFiling", subject.ToString());
    }

    [Fact]
    public void A_blank_identifier_is_the_same_as_none() =>
        Assert.False(IngestionSubject.Create("Company", "   ").IsSpecific);

    [Fact]
    public void A_kind_is_required() =>
        Assert.Throws<DomainValidationException>(() => IngestionSubject.Create("  "));

    /// <summary>
    /// The subject is two strings so that the data plane is not silently narrowed to equities.
    /// </summary>
    [Theory]
    [InlineData("Product", "SKU-1188")]
    [InlineData("Supplier", "duns:150483782")]
    [InlineData("CurrencyPair", "EURUSD")]
    [InlineData("ShippingRoute", "CNSHA-NLRTM")]
    public void Non_equity_subjects_are_expressible(string kind, string identifier) =>
        Assert.Equal($"{kind}:{identifier}", IngestionSubject.Create(kind, identifier).ToString());
}

public sealed class IngestionRequestTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    private static IngestionRequest Request(
        string sourceId = "sec-edgar",
        DataCategory category = DataCategory.RegulatoryFilings,
        string subjectId = "AAPL",
        DateRange? window = null) =>
        IngestionRequest.Create(
            SourceId.Create(sourceId),
            category,
            Region.UnitedStates,
            IngestionSubject.Create("Company", subjectId),
            CorrelationId.New(),
            Now,
            window);

    /// <summary>
    /// The whole purpose of the fingerprint: a retry is the same request. Including the timestamp
    /// or the correlation id would make every retry unique, which is exactly the bug an
    /// idempotency key exists to prevent.
    /// </summary>
    [Fact]
    public void The_fingerprint_ignores_when_and_why_the_request_was_made()
    {
        var first = Request();
        var second = IngestionRequest.Create(
            SourceId.Create("sec-edgar"),
            DataCategory.RegulatoryFilings,
            Region.UnitedStates,
            IngestionSubject.Create("Company", "AAPL"),
            CorrelationId.New(),
            Now.AddHours(9));

        Assert.Equal(first.Fingerprint(), second.Fingerprint());
    }

    [Fact]
    public void The_fingerprint_distinguishes_the_source() =>
        Assert.NotEqual(Request().Fingerprint(), Request(sourceId: "fred").Fingerprint());

    [Fact]
    public void The_fingerprint_distinguishes_the_category() =>
        Assert.NotEqual(
            Request().Fingerprint(),
            Request(category: DataCategory.CompanyProfile).Fingerprint());

    [Fact]
    public void The_fingerprint_distinguishes_the_subject() =>
        Assert.NotEqual(Request().Fingerprint(), Request(subjectId: "MSFT").Fingerprint());

    [Fact]
    public void The_fingerprint_distinguishes_the_window()
    {
        var q1 = DateRange.Create(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc));
        var q2 = DateRange.Create(
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc));

        Assert.NotEqual(Request(window: q1).Fingerprint(), Request(window: q2).Fingerprint());
        Assert.NotEqual(Request(window: q1).Fingerprint(), Request().Fingerprint());
    }

    [Fact]
    public void The_fingerprint_fits_an_idempotency_key() =>
        Assert.Equal(ContentHash.HexLength, Request().Fingerprint().Length);

    [Fact]
    public void A_window_is_optional() => Assert.Null(Request().Window);

    [Fact]
    public void An_unrequestable_category_is_rejected()
    {
        Assert.Throws<DomainValidationException>(() => Request(category: DataCategory.Unknown));
        Assert.Throws<DomainValidationException>(() => Request(category: (DataCategory)9999));
    }
}

public sealed class IngestionRunTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    private static IngestionRequest Request() =>
        IngestionRequest.Create(
            SourceId.Create("sec-edgar"),
            DataCategory.RegulatoryFilings,
            Region.UnitedStates,
            IngestionSubject.Create("Company", "AAPL"),
            CorrelationId.New(),
            Now);

    private static ContentHash Hash(string payload) =>
        ContentHash.Compute(Encoding.UTF8.GetBytes(payload));

    [Fact]
    public void A_new_run_is_in_progress()
    {
        var run = IngestionRun.Start(Request(), Now);

        Assert.Equal(IngestionOutcome.InProgress, run.Outcome);
        Assert.False(run.IsComplete);
        Assert.Null(run.CompletedAtUtc);
        Assert.Empty(run.Artifacts);
    }

    [Fact]
    public void A_successful_run_records_its_artifacts()
    {
        var run = IngestionRun.Start(Request(), Now);

        run.RecordArtifact(Hash("first"));
        run.RecordArtifact(Hash("second"));
        run.MarkSucceeded(Now.AddMinutes(2));

        Assert.Equal(IngestionOutcome.Succeeded, run.Outcome);
        Assert.Equal(2, run.Artifacts.Count);
        Assert.Equal(Now.AddMinutes(2), run.CompletedAtUtc);
        Assert.Null(run.Reason);
    }

    /// <summary>
    /// The archive is content-addressed, so the same payload twice is one artifact. Counting it
    /// twice would overstate what was retrieved.
    /// </summary>
    [Fact]
    public void The_same_payload_recorded_twice_is_one_artifact()
    {
        var run = IngestionRun.Start(Request(), Now);

        run.RecordArtifact(Hash("same"));
        run.RecordArtifact(Hash("same"));

        Assert.Single(run.Artifacts);
    }

    [Fact]
    public void A_failure_must_say_why()
    {
        var run = IngestionRun.Start(Request(), Now);

        Assert.Throws<DomainValidationException>(() => run.MarkFailed("  ", Now.AddMinutes(1)));
    }

    [Fact]
    public void A_failed_run_carries_its_reason()
    {
        var run = IngestionRun.Start(Request(), Now);

        run.MarkFailed("provider returned 503 three times", Now.AddMinutes(1));

        Assert.Equal(IngestionOutcome.Failed, run.Outcome);
        Assert.Equal("provider returned 503 three times", run.Reason);
    }

    /// <summary>
    /// A partial result silently treated as complete is how gaps enter a history without anyone
    /// noticing, so it is a distinct outcome rather than a successful run with fewer rows.
    /// </summary>
    [Fact]
    public void A_partial_result_is_not_a_success()
    {
        var run = IngestionRun.Start(Request(), Now);

        run.RecordArtifact(Hash("page 1"));
        run.MarkPartiallySucceeded("page 2 of 3 timed out", Now.AddMinutes(5));

        Assert.Equal(IngestionOutcome.PartiallySucceeded, run.Outcome);
        Assert.NotEqual(IngestionOutcome.Succeeded, run.Outcome);
    }

    [Fact]
    public void A_run_describes_one_attempt_and_is_not_revised()
    {
        var run = IngestionRun.Start(Request(), Now);
        run.MarkSucceeded(Now.AddMinutes(1));

        Assert.Throws<DomainRuleViolationException>(() => run.MarkFailed("second thoughts", Now.AddMinutes(2)));
        Assert.Throws<DomainRuleViolationException>(() => run.RecordArtifact(Hash("late")));
    }

    [Fact]
    public void A_run_cannot_complete_before_it_started()
    {
        var run = IngestionRun.Start(Request(), Now);

        Assert.Throws<DomainRuleViolationException>(() => run.MarkSucceeded(Now.AddMinutes(-1)));
    }

    /// <summary>
    /// The most interesting thing that can happen - the platform declining to ingest something it
    /// was configured to ingest - must leave a trace, or the operator sees only missing data.
    /// </summary>
    [Fact]
    public void A_refusal_is_recorded_as_a_completed_run()
    {
        var refusal = SourceAdmissionResult.Refused(
            SourceAdmission.SourceActiveRule,
            "not active");

        var run = IngestionRun.Refuse(Request(), refusal, Now);

        Assert.Equal(IngestionOutcome.Refused, run.Outcome);
        Assert.True(run.IsComplete);
        Assert.Equal(SourceAdmission.SourceActiveRule, run.RefusalRuleId);
        Assert.Equal("not active", run.Reason);
        Assert.Equal(Now, run.CompletedAtUtc);
    }

    [Fact]
    public void An_admitted_source_cannot_be_recorded_as_a_refusal() =>
        Assert.Throws<DomainRuleViolationException>(() =>
            IngestionRun.Refuse(Request(), SourceAdmissionResult.Admitted, Now));

    [Fact]
    public void A_run_exposes_the_source_it_drew_from() =>
        Assert.Equal(SourceId.Create("sec-edgar"), IngestionRun.Start(Request(), Now).SourceId);
}
