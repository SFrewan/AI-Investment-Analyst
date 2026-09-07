using System.Globalization;
using System.Text;
using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Ingestion.Providers;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The SEC runner, proven end to end against fakes: nothing reaches a provider, nothing is spent.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Hermetic by construction.</strong> The acquisition, the ledger, the archive and the clock
/// are all fakes; the connector is a stub that throws if anything reaches it. No network, no
/// database, no file written outside the temp directory, and every authorisation exercised here is
/// synthetic - written to a temp path from a digest computed with the production helper, and deleted
/// afterwards. The installed declaration is loaded read-only in one fact, to prove it is untouched.
/// </para>
/// <para>
/// <strong>What these facts are for.</strong> The runner's whole value is an ordering: every
/// deterministic refusal happens before a unit is charged, and the charge happens once,
/// immediately before the one dispatch. Each test below pins one step of that ordering by driving
/// the runner into it and reading the counter on the way out.
/// </para>
/// </remarks>
public sealed class SecFilingsBatchRunnerTests
{
    private const string Evidence = "phase-b-synthetic-evidence-base@0000";
    private const string Contact = "someone@example.com";
    private const string Lgiq = "0001335112";
    private const string Qumu = "0000892482";

    private const string InstalledDeclaration =
        "acquisition-sec-edgar-gate6-six-members-2021-09-to-2026-08.json";

    private const string SealedFingerprint =
        "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private const string ApprovedDigest =
        "499f211f9c931c78d7faf1beb76eef3205fec12cb28db06eeeda461964567682";

    private static readonly string[] Ciks =
    [
        "0000892482", "0001335112", "0001404123", "0001426332", "0001775625", "0001784851",
    ];

    private static readonly string[] SharedForms = ["8-K", "25", "25-NSE", "S-4", "DEFM14A"];

    private static readonly string[] SecondaryForms = ["424B*", "S-1", "S-3"];

    private static readonly DateTime Now = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly WindowStart = new(2022, 12, 11);

    /// <summary>A submissions document reaching back before the window, with one relevant filing.</summary>
    private const string Covering = """
        {
          "cik": "892482",
          "name": "Qumu Corporation",
          "filings": {
            "recent": {
              "accessionNumber": ["0000892482-22-000045", "0000892482-21-000010"],
              "filingDate": ["2022-12-19", "2021-03-01"],
              "reportDate": ["2022-12-19", "2021-02-28"],
              "acceptanceDateTime": ["2022-12-19T17:02:11.000Z", "2021-03-01T11:00:00.000Z"],
              "form": ["8-K", "10-K"],
              "primaryDocument": ["qumu-8k.htm", "qumu-10k.htm"],
              "primaryDocDescription": ["8-K", "10-K"]
            },
            "files": []
          }
        }
        """;

    /// <summary>The same document with nothing inside the selection window.</summary>
    private const string CoveringButEmpty = """
        {
          "cik": "892482",
          "name": "Qumu Corporation",
          "filings": {
            "recent": {
              "accessionNumber": ["0000892482-21-000010"],
              "filingDate": ["2021-03-01"],
              "acceptanceDateTime": ["2021-03-01T11:00:00.000Z"],
              "form": ["10-K"]
            },
            "files": []
          }
        }
        """;

    /// <summary>A document that begins after the question does.</summary>
    private const string NotCovering = """
        {
          "cik": "892482",
          "name": "Qumu Corporation",
          "filings": {
            "recent": {
              "accessionNumber": ["0000892482-24-000002"],
              "filingDate": ["2024-04-04"],
              "acceptanceDateTime": ["2024-04-04T11:00:00.000Z"],
              "form": ["8-K"]
            },
            "files": [{"name": "CIK0000892482-submissions-001.json"}]
          }
        }
        """;

    /// <summary>An offering document inside the window, in the secondary scope only.</summary>
    private const string Offering = """
        {
          "cik": "1335112",
          "name": "LOGIQ, INC.",
          "filings": {
            "recent": {
              "accessionNumber": ["0001335112-22-000091", "0001335112-20-000001"],
              "filingDate": ["2022-12-19", "2020-01-06"],
              "acceptanceDateTime": ["2022-12-19T21:05:00.000Z", "2020-01-06T09:00:00.000Z"],
              "form": ["424B5", "10-K"]
            },
            "files": []
          }
        }
        """;

    // ================= 1-7. every deterministic refusal costs nothing =================

    [Fact]
    public Task An_unauthorised_batch_dispatches_nothing_and_consumes_nothing() =>
        Refuses(
            SecFilingsBatchRunner.BatchAuthorisedRule,
            c => c with { Batch = c.Batch with { Authorised = false } });

    [Fact]
    public Task A_member_the_sealed_universe_does_not_hold_dispatches_nothing() =>
        Refuses(
            SecFilingsBatchRunner.MemberIdentityRule,
            c => c with { SealedCiks = new HashSet<string>(StringComparer.Ordinal) });

    [Fact]
    public Task A_member_the_authorisation_does_not_name_dispatches_nothing() =>
        Refuses(
            SecFilingsBatchRunner.MemberIdentityRule,
            c => c with
            {
                Batch = c.Batch with { Cik = "0000320193", Symbol = "AAPL.US" },
                SealedCiks = new HashSet<string>(["0000320193"], StringComparer.Ordinal),
            });

    [Fact]
    public Task A_batch_naming_another_declaration_dispatches_nothing() =>
        Refuses(
            SecFilingsBatchRunner.SourceCategoryRule,
            c => c with
            {
                Batch = c.Batch with { Declaration = "acquisition-eodhd-splits-final-2021-09-to-2026-08.json" },
            });

    /// <summary>
    /// A coverage failure is refused before dispatch, and it is refused as scope rather than budget.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The authorisation here names all six members, so the identity gate above admits this one -
    /// and then <c>Covers</c> refuses it, because the declaration's scope window is a day short of
    /// the one the runner asks for. That is the case worth pinning: an authorisation the runner
    /// would otherwise treat as its own, whose scope is not what the runner was written against.
    /// </para>
    /// <para>
    /// A member the authorisation does not name is refused one gate earlier, under
    /// <c>runner.member-identity@1</c>, and that ordering is deliberate: an unnamed subject is an
    /// identity problem, not a coverage problem, and reporting it as coverage would suggest the
    /// window could be widened to admit it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_authorisation_whose_scope_window_is_not_the_runners_dispatches_nothing()
    {
        await WithRunner(async (runner, acquisition, authorization) =>
        {
            // The identity gate is satisfied - this authorisation names the member.
            Assert.Contains(Qumu, authorization.Symbols);

            var outcome = await runner.RunAsync(Context(authorization));

            Assert.Equal(SecFilingsBatchRunner.RefusedStatus, outcome.Status);
            Assert.Equal(SecFilingsBatchRunner.AuthorisationCoversRule, outcome.RuleId);
            Assert.Contains("window", outcome.Reason!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, acquisition.Calls);
            Assert.Equal(0, authorization.Consumed);
        },
        scopeTo: new DateOnly(2026, 8, 30));
    }

    /// <summary>
    /// A request carrying a window is refused before consumption, by the preflight's own rule.
    /// </summary>
    /// <remarks>
    /// The runner's builder takes no window parameter, so the only way to produce one is to hand the
    /// preflight a request built elsewhere. This drives the same gate the preflight would apply, and
    /// asserts the counter never moves - which is the whole difference from the pre-Phase-A ordering,
    /// where the capability gate sat one step after the charge.
    /// </remarks>
    [Fact]
    public void A_request_carrying_a_window_is_refused_by_the_gate_that_now_runs_before_consumption()
    {
        var windowed = IngestionRequest.Create(
            SourceId.Create(SecFilingsBatchRunner.Source),
            DataCategory.RegulatoryFilings,
            Region.UnitedStates,
            IngestionSubject.Create(SecFilingsBatchRunner.SubjectKind, Qumu),
            AI.Investment.Domain.Common.CorrelationId.Create("phase-b-windowed"),
            Now,
            AI.Investment.Domain.ValueObjects.DateRange.Create(
                new DateTime(2021, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc)));

        var verdict = IngestionPreflight.Evaluate(ActiveSource(), new StubProvider(), windowed);

        Assert.False(verdict.IsAdmitted);
        Assert.Equal(ProviderCapabilityCheck.WindowSupportedRule, verdict.RuleId);
    }

    [Fact]
    public Task A_disabled_or_unavailable_provider_dispatches_nothing_and_consumes_nothing() =>
        Refuses(IngestionGateway.ProviderAvailableRule, c => c with { Provider = null });

    [Fact]
    public Task An_unregistered_source_dispatches_nothing_and_consumes_nothing() =>
        Refuses(IngestionGateway.SourceRegisteredRule, c => c with { Source = null });

    [Fact]
    public Task An_inactive_source_dispatches_nothing_and_consumes_nothing() =>
        Refuses(SourceAdmission.SourceActiveRule, c => c with { Source = InactiveSource() });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    public Task A_missing_or_malformed_contact_dispatches_nothing_and_consumes_nothing(string? contact) =>
        Refuses(SecFilingsBatchRunner.DeploymentIdentityRule, c => c with { ContactAddress = contact });

    /// <summary>The refusal names the variable and never the value.</summary>
    [Fact]
    public async Task A_contact_refusal_names_the_variable_and_never_the_value()
    {
        const string secret = "someone.private@example.org";

        await WithRunner(async (runner, acquisition, authorization) =>
        {
            // Present but malformed in a way only the annotation catches, so the message is produced.
            var outcome = await runner.RunAsync(Context(authorization) with { ContactAddress = "broken" });

            Assert.Equal(SecFilingsBatchRunner.DeploymentIdentityRule, outcome.RuleId);
            Assert.Contains(SecFilingsBatchRunner.ContactVariable, outcome.Reason!, StringComparison.Ordinal);
            Assert.DoesNotContain("broken", outcome.Reason!, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, outcome.Reason!, StringComparison.Ordinal);
            Assert.Equal(0, acquisition.Calls);
            Assert.Equal(0, authorization.Consumed);
        });
    }

    [Fact]
    public Task An_accounting_state_that_has_moved_since_the_approval_dispatches_nothing() =>
        Refuses(
            SecFilingsBatchRunner.PriorConsumptionRule,
            c => c with { Batch = c.Batch with { ExpectedPriorConsumption = 3 } });

    // ================= 8-10. the consumption boundary =================

    /// <summary>
    /// A valid request reaches the boundary, consumes exactly one, and dispatches exactly once.
    /// </summary>
    /// <remarks>
    /// Three facts in one because they are one ordering: the counter is zero on the way in, one on
    /// the way out, and the provider was called exactly once with exactly the request the runner
    /// built. A fourth is asserted with them - the dispatched request carries no window - because
    /// that is the property most easily lost by a later edit.
    /// </remarks>
    [Fact]
    public async Task A_valid_request_consumes_exactly_one_and_dispatches_exactly_once()
    {
        await WithRunner(async (runner, acquisition, authorization) =>
        {
            acquisition.Respond = request => Succeeded(request, Covering);

            Assert.Equal(0, authorization.Consumed);

            var outcome = await runner.RunAsync(Context(authorization));

            Assert.Equal(SecFilingsBatchRunner.DispatchedStatus, outcome.Status);
            Assert.Equal(1, acquisition.Calls);
            Assert.Equal(1, outcome.ConsumedThisRun);
            Assert.Equal(1, authorization.Consumed);
            Assert.Equal(5, authorization.Remaining);
            Assert.True(outcome.Dispatched);

            // The request that actually went out.
            Assert.Null(acquisition.Last!.Window);
            Assert.Equal(Qumu, acquisition.Last.Subject.Identifier);
            Assert.Equal(SecFilingsBatchRunner.SubjectKind, acquisition.Last.Subject.Kind);
            Assert.Equal(DataCategory.RegulatoryFilings, acquisition.Last.Category);
            Assert.Equal(SecFilingsBatchRunner.Source, acquisition.Last.SourceId.Value);
        });
    }

    /// <summary>A failure after consumption spends the unit and does not credit it back.</summary>
    /// <remarks>
    /// Charged at intent. No successor authorisation is created, nothing is retried, and the outcome
    /// records the exception type and nothing else - a provider's message is one of the likelier
    /// places for a credential to surface.
    /// </remarks>
    [Fact]
    public async Task A_failure_after_consumption_spends_the_unit_and_creates_no_successor()
    {
        await WithRunner(async (runner, acquisition, authorization) =>
        {
            acquisition.Throw = new TimeoutException("this message must not be recorded");

            var outcome = await runner.RunAsync(Context(authorization));

            Assert.Equal(SecFilingsBatchRunner.ThrewStatus, outcome.Status);
            Assert.Equal(nameof(TimeoutException), outcome.Diagnostic);
            Assert.Equal(1, outcome.ConsumedThisRun);
            Assert.Equal(5, authorization.Remaining);
            Assert.DoesNotContain("must not be recorded", outcome.Reason!, StringComparison.Ordinal);
        });
    }

    // ================= 11-14. identity, idempotency, isolation =================

    [Fact]
    public void The_correlation_for_each_member_and_attempt_is_deterministic_and_distinct()
    {
        var identifiers = new List<string>();

        foreach (var batch in SecEdgarSixMemberPartition.Batches)
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var once = SecFilingsBatchRunner.CorrelationFor(batch.Index, attempt, batch.Cik);
                var twice = SecFilingsBatchRunner.CorrelationFor(batch.Index, attempt, batch.Cik);

                Assert.Equal(once, twice);

                identifiers.Add(once);
            }
        }

        Assert.Equal(
            string.Join(",", identifiers),
            string.Join(",", identifiers.Distinct(StringComparer.Ordinal)));

        // Named so an operator can read a batch and an attempt out of it.
        Assert.StartsWith("batch-1-a1-", identifiers[0], StringComparison.Ordinal);
        Assert.EndsWith("-RegulatoryFilings", identifiers[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A rerun of a recorded attempt cannot double-consume, by either of two independent routes.
    /// </summary>
    [Fact]
    public async Task A_rerun_of_a_recorded_attempt_cannot_double_consume()
    {
        // Route one: the ledger already holds a successful run for this exact request.
        await WithRunner(async (runner, acquisition, authorization) =>
        {
            var outcome = await runner.RunAsync(Context(authorization));

            Assert.Equal(SecFilingsBatchRunner.SuppressedStatus, outcome.Status);
            Assert.Equal(0, outcome.ConsumedThisRun);
            Assert.Equal(0, acquisition.Calls);
            Assert.Equal(6, authorization.Remaining);
        },
        alreadyCompleted: true);

        // Route two: an earlier attempt already claimed this correlation, so the seam would refuse
        // it as a duplicate - after the unit was spent. Refused here instead, unspent.
        await WithRunner(async (runner, acquisition, authorization) =>
        {
            var burned = new HashSet<string>(
                [SecFilingsBatchRunner.CorrelationFor(1, 1, Qumu)],
                StringComparer.Ordinal);

            var outcome = await runner.RunAsync(Context(authorization) with { BurnedCorrelations = burned });

            Assert.Equal(SecFilingsBatchRunner.AttemptAlreadyClaimedRule, outcome.RuleId);
            Assert.Equal(0, outcome.ConsumedThisRun);
            Assert.Equal(0, acquisition.Calls);
        });
    }

    /// <summary>
    /// Six single-member batches stay isolated: one failure leaves the other five payable.
    /// </summary>
    [Fact]
    public async Task The_six_batches_are_isolated_and_one_failure_leaves_five_payable()
    {
        await WithRunner(async (runner, acquisition, authorization) =>
        {
            acquisition.Throw = new TimeoutException("boom");

            var first = await runner.RunAsync(Context(authorization));

            Assert.Equal(1, first.ConsumedThisRun);
            Assert.Equal(5, authorization.Remaining);

            // The next member is still payable under the same approved authorisation.
            acquisition.Throw = null;
            acquisition.Respond = request => Succeeded(request, Covering);

            var second = await runner.RunAsync(Context(authorization) with
            {
                Batch = SecEdgarSixMemberPartition.Batches[1] with { Authorised = true },
                AttemptNumber = 1,
            });

            Assert.Equal(SecFilingsBatchRunner.DispatchedStatus, second.Status);
            Assert.Equal(1, second.ConsumedThisRun);
            Assert.Equal(4, authorization.Remaining);
        });
    }

    /// <summary>
    /// The runner cannot write a successor authorisation, because it cannot write anything.
    /// </summary>
    /// <remarks>
    /// A source-text assertion, deliberately blunt: reflection can be asked what a type has, not
    /// what a file lacks. The runner opens no file, computes no digest and names no supersession, so
    /// there is no path by which a failure could quietly mint itself more budget. The attempt
    /// artefact is produced as a string and written by the caller.
    /// </remarks>
    [Fact]
    public async Task The_runner_can_neither_write_a_file_nor_mint_a_successor_authorisation()
    {
        var source = await File.ReadAllTextAsync(Universe.RepositoryPath(
            "tests", "AI.Investment.Api.Tests", "SecFilingsBatchRunner.cs"));

        foreach (var token in new[]
        {
            "File.",
            "Directory.",
            "Supersedes",
            "DigestFor",
            "RecordPriorConsumption",
            "WriteAllText",
            "HttpClient",
        })
        {
            Assert.False(
                source.Contains(token, StringComparison.Ordinal),
                Universe.Inv($"`{token}` appears in the runner. It must not be able to write, to mint a successor authorisation, or to reach a transport."));
        }
    }

    // ================= 15-16. the two windows =================

    /// <summary>
    /// The authorisation's scope dates are used for <c>Covers</c> and never sent to the provider.
    /// </summary>
    [Fact]
    public async Task The_scope_window_is_used_only_for_covers_and_never_sent_to_the_provider()
    {
        Assert.Equal(new DateOnly(2021, 9, 1), SecFilingsBatchRunner.WindowFrom);
        Assert.Equal(new DateOnly(2026, 8, 31), SecFilingsBatchRunner.WindowTo);

        await WithRunner(async (runner, acquisition, authorization) =>
        {
            acquisition.Respond = request => Succeeded(request, Covering);

            // Covers is satisfied only by the full scope window.
            Assert.True(authorization
                .Covers(SecFilingsBatchRunner.Source, Qumu, SecFilingsBatchRunner.WindowFrom, SecFilingsBatchRunner.WindowTo)
                .Allowed);

            Assert.False(authorization
                .Covers(SecFilingsBatchRunner.Source, Qumu, SecFilingsBatchRunner.WindowFrom, new DateOnly(2026, 8, 30))
                .Allowed);

            await runner.RunAsync(Context(authorization));

            // And the request that went out carries none of it.
            Assert.Null(acquisition.Last!.Window);

            // The fingerprint the ledger keys on therefore has no period in it either.
            Assert.Equal(runner.BuildRequest(Qumu, "any-correlation").Fingerprint(), acquisition.Last.Fingerprint());
        });
    }

    // ================= 17-18. completeness =================

    [Fact]
    public async Task Insufficient_coverage_reports_insufficient_local_evidence()
    {
        await WithRunner(async (runner, acquisition, authorization) =>
        {
            acquisition.Respond = request => Succeeded(request, NotCovering);

            var outcome = await runner.RunAsync(Context(authorization));

            Assert.Equal(SubmissionsCompleteness.NotProven, outcome.Reading!.Coverage);
            Assert.Equal(SubmissionsCompleteness.InsufficientEvidence, outcome.Reading.Verdict);

            // Never the other label, whatever was or was not found.
            Assert.NotEqual(SubmissionsCompleteness.NoRelevantEvent, outcome.Reading.Verdict);

            // The reference to further files is reported and never followed.
            Assert.True(outcome.Reading.HoldsOlderFilings);
            Assert.Equal(1, acquisition.Calls);
        });
    }

    [Fact]
    public async Task Sufficient_coverage_with_no_relevant_filing_reports_a_real_absence()
    {
        await WithRunner(async (runner, acquisition, authorization) =>
        {
            acquisition.Respond = request => Succeeded(request, CoveringButEmpty);

            var outcome = await runner.RunAsync(Context(authorization));

            Assert.Equal(SubmissionsCompleteness.Covered, outcome.Reading!.Coverage);
            Assert.Equal(0, outcome.Reading.RelevantFilings);
            Assert.Equal(SubmissionsCompleteness.NoRelevantEvent, outcome.Reading.Verdict);
        });
    }

    [Fact]
    public async Task Sufficient_coverage_with_a_relevant_filing_supports_coincidence_only()
    {
        await WithRunner(async (runner, acquisition, authorization) =>
        {
            acquisition.Respond = request => Succeeded(request, Covering);

            var outcome = await runner.RunAsync(Context(authorization));

            Assert.Equal(SubmissionsCompleteness.Covered, outcome.Reading!.Coverage);
            Assert.Equal(1, outcome.Reading.RelevantFilings);
            Assert.Equal("0000892482-22-000045", Assert.Single(outcome.Reading.RelevantAccessions));

            // Coincidence, never causation.
            Assert.Equal(SubmissionsCompleteness.SupportsCoincidence, outcome.Reading.Verdict);
        });
    }

    // ================= 19-23. what the acquisition produced =================

    [Fact]
    public async Task The_raw_payload_is_archived_and_read_back_byte_for_byte()
    {
        await WithRunner(async (runner, acquisition, authorization, archive) =>
        {
            acquisition.Respond = request => Succeeded(request, Covering);

            var outcome = await runner.RunAsync(Context(authorization));

            var hash = outcome.Reading!.ArchivedHash;

            Assert.False(string.IsNullOrWhiteSpace(hash));

            var stored = await archive.RetrieveAsync(ContentHash.Create(hash));

            Assert.NotNull(stored);
            Assert.Equal(Covering, Encoding.UTF8.GetString(stored!));
            Assert.Equal(ContentHash.Compute(Encoding.UTF8.GetBytes(Covering)).Value, hash);
        });
    }

    /// <summary>
    /// The filings normaliser turns the archived document into observations, temporal rule intact.
    /// </summary>
    /// <remarks>
    /// Driven directly rather than through the fake acquisition, because the fake does not run the
    /// pipeline - the point here is that the bytes the runner archives are bytes this normaliser can
    /// read, and that what it produces satisfies the domain's ordering rule and keeps the identity.
    /// </remarks>
    [Fact]
    public async Task The_archived_document_normalises_into_filing_observations_that_keep_their_identity()
    {
        var payload = Encoding.UTF8.GetBytes(Covering);

        var result = await new AI.Investment.Infrastructure.Normalization.SecEdgarFilingsNormalizer()
            .NormalizeAsync(new NormalizationInput(
                SecEdgarProvider.Id,
                DataCategory.RegulatoryFilings,
                IngestionSubject.Create(SecFilingsBatchRunner.SubjectKind, Qumu),
                ContentHash.Compute(payload),
                payload,
                Now));

        Assert.False(result.IsQuarantined);
        Assert.NotEmpty(result.Observations);

        // 21. The temporal invariant, on every observation.
        Assert.All(result.Observations, o =>
        {
            Assert.True(o.Provenance.AsOfUtc <= o.Provenance.PublishedAtUtc);
            Assert.True(o.Provenance.PublishedAtUtc <= o.Provenance.RetrievedAtUtc);
            Assert.Equal(Now, o.Provenance.RetrievedAtUtc);
        });

        // 23. Accession identity is preserved, and the subject stays the CIK the request named.
        Assert.Contains(result.Observations, o =>
            string.Equals(o.Provenance.SourceRecordId, "0000892482-22-000045", StringComparison.Ordinal));

        Assert.All(result.Observations, o => Assert.Equal(Qumu, o.Subject.Identifier));
    }

    /// <summary>22. A report date later than acceptance is an attribute, never the period.</summary>
    [Fact]
    public async Task A_report_date_later_than_acceptance_is_an_attribute_and_not_the_period()
    {
        const string future = """
            {
              "name": "Qumu Corporation",
              "filings": {"recent": {
                "accessionNumber": ["0000892482-22-000045"],
                "filingDate": ["2022-12-27"],
                "reportDate": ["2023-01-09"],
                "acceptanceDateTime": ["2022-12-27T17:02:11.000Z"],
                "form": ["25-NSE"]
              }}
            }
            """;

        var payload = Encoding.UTF8.GetBytes(future);

        var result = await new AI.Investment.Infrastructure.Normalization.SecEdgarFilingsNormalizer()
            .NormalizeAsync(new NormalizationInput(
                SecEdgarProvider.Id,
                DataCategory.RegulatoryFilings,
                IngestionSubject.Create(SecFilingsBatchRunner.SubjectKind, Qumu),
                ContentHash.Compute(payload),
                payload,
                Now));

        var accepted = new DateTime(2022, 12, 27, 17, 2, 11, DateTimeKind.Utc);
        var stated = new DateTime(2023, 1, 9, 0, 0, 0, DateTimeKind.Utc);

        Assert.All(result.Observations, o =>
        {
            Assert.Equal(accepted, o.Provenance.AsOfUtc);
            Assert.NotEqual(stated, o.Provenance.AsOfUtc);
        });

        var reportDate = result.Observations.Single(o =>
            o.Attribute == AI.Investment.Infrastructure.Normalization.SecEdgarFilingsNormalizer.ReportDateAttribute);

        Assert.Equal(stated, reportDate.Value.AsTimestamp());
    }

    // ================= 24-25. LGIQ =================

    [Fact]
    public void Only_LGIQ_carries_the_secondary_form_scope()
    {
        var context = Context(null!);

        var lgiq = SecFilingsBatchRunner.FormsInScopeFor(context, Lgiq);

        Assert.Contains("424B*", lgiq);
        Assert.Contains("S-1", lgiq);
        Assert.Contains("S-3", lgiq);

        foreach (var other in Ciks.Where(c => !string.Equals(c, Lgiq, StringComparison.Ordinal)))
        {
            var forms = SecFilingsBatchRunner.FormsInScopeFor(context, other);

            Assert.DoesNotContain("424B*", forms);
            Assert.DoesNotContain("S-1", forms);
            Assert.DoesNotContain("S-3", forms);
            Assert.Equal(string.Join(",", SharedForms), string.Join(",", forms));
        }
    }

    /// <summary>
    /// The same offering document is relevant for LGIQ and not for another member.
    /// </summary>
    /// <remarks>
    /// The strongest form of the rule: not that the list differs, but that the <em>finding</em>
    /// differs. A 424B5 inside the window is a relevant filing for the member whose declaration
    /// grants that scope, and no filing at all for one whose declaration does not.
    /// </remarks>
    [Fact]
    public async Task The_secondary_scope_changes_what_LGIQ_finds_and_nothing_for_the_other_five()
    {
        await WithRunner(async (runner, acquisition, authorization) =>
        {
            acquisition.Respond = request => Succeeded(request, Offering);

            var forLgiq = await runner.RunAsync(Context(authorization) with
            {
                Batch = Batch() with { Cik = Lgiq, Symbol = "LGIQ.US" },
            });

            Assert.Equal(1, forLgiq.Reading!.RelevantFilings);
            Assert.Equal(SubmissionsCompleteness.SupportsCoincidence, forLgiq.Reading.Verdict);
        });

        await WithRunner(async (runner, acquisition, authorization) =>
        {
            acquisition.Respond = request => Succeeded(request, Offering);

            // The same document, read for a member with no secondary scope.
            var forOther = await runner.RunAsync(Context(authorization));

            Assert.Equal(0, forOther.Reading!.RelevantFilings);
            Assert.Equal(SubmissionsCompleteness.NoRelevantEvent, forOther.Reading.Verdict);
        });
    }

    [Theory]
    [InlineData("424B5", true)]
    [InlineData("424B1", true)]
    [InlineData("424", false)]
    [InlineData("S-1", true)]
    [InlineData("S-1/A", false)]
    [InlineData("10-K", false)]
    public void The_form_wildcard_is_a_prefix_and_nothing_more(string form, bool expected) =>
        Assert.Equal(expected, SecFilingsBatchRunner.FormMatches(form, SecondaryForms));

    // ================= 26-29. ceiling, EODHD, the rate limiter, the disabled provider =================

    [Fact]
    public async Task The_dispatch_ceiling_cannot_be_exceeded()
    {
        await WithRunner(async (runner, acquisition, authorization) =>
        {
            acquisition.Respond = request => Succeeded(request, Covering);

            // Spend the whole ceiling through the production counter.
            for (var i = 0; i < 6; i++)
            {
                Assert.True(authorization.TryConsume(
                    SecFilingsBatchRunner.Source, Ciks[i],
                    SecFilingsBatchRunner.WindowFrom, SecFilingsBatchRunner.WindowTo).Allowed);
            }

            Assert.Equal(6, authorization.Consumed);
            Assert.Equal(0, authorization.Remaining);

            var outcome = await runner.RunAsync(Context(authorization) with
            {
                Batch = Batch() with { ExpectedPriorConsumption = 6 },
            });

            Assert.Equal(SecFilingsBatchRunner.DispatchCeilingRule, outcome.RuleId);
            Assert.Equal(0, acquisition.Calls);
            Assert.Equal(6, authorization.Consumed);
        });
    }

    /// <summary>28. The rate limiter was not moved, peeked at, or duplicated.</summary>
    /// <remarks>
    /// Phase A recorded it as the one gate that could not be hoisted: it reserves when it passes, so
    /// evaluating it early would spend a slot the gateway then spends again. Phase B does not
    /// redesign it. Three assertions: it is still deferred rather than preflighted, the preflight
    /// still declares exactly two deferred rules, and the runner never names a limiter at all.
    /// </remarks>
    [Fact]
    public async Task The_rate_limiter_is_not_moved_into_the_preflight_or_into_the_runner()
    {
        Assert.Contains(IngestionGateway.WithinRateLimitRule, IngestionPreflight.DeferredRules);
        Assert.DoesNotContain(IngestionGateway.WithinRateLimitRule, IngestionPreflight.PreflightedRules);

        Assert.Equal(
            $"{IngestionGateway.PolicyPermittedRule},{IngestionGateway.WithinRateLimitRule}",
            string.Join(",", IngestionPreflight.DeferredRules.Order(StringComparer.Ordinal)));

        var source = await File.ReadAllTextAsync(Universe.RepositoryPath(
            "tests", "AI.Investment.Api.Tests", "SecFilingsBatchRunner.cs"));

        Assert.DoesNotContain("IProviderRateLimiter", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryAcquireAsync", source, StringComparison.Ordinal);
    }

    /// <summary>29. Nothing can dispatch while the connector is off, at the shipped configuration.</summary>
    [Fact]
    public async Task No_runner_path_can_dispatch_while_the_connector_is_disabled()
    {
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("src", "AI.Investment.Api", "appsettings.json")));

        Assert.False(settings.RootElement
            .GetProperty("Providers").GetProperty("SecEdgar").GetProperty("Enabled").GetBoolean());

        // A disabled connector is never registered, so it arrives at the preflight as a null
        // provider - refused, before consumption, under the gateway's own rule.
        await WithRunner(async (runner, acquisition, authorization) =>
        {
            var outcome = await runner.RunAsync(Context(authorization) with { Provider = null });

            Assert.Equal(IngestionGateway.ProviderAvailableRule, outcome.RuleId);
            Assert.Equal(0, acquisition.Calls);
            Assert.Equal(0, authorization.Consumed);
        });

        // And no batch is approved, which is the gate ahead of all of it.
        Assert.DoesNotContain(SecEdgarSixMemberPartition.Batches, b => b.Authorised);
    }

    /// <summary>The installed authorisation is read-only here and still spends nothing.</summary>
    [Fact]
    public void The_installed_authorisation_is_untouched_by_every_fact_in_this_file()
    {
        var installed = AcquisitionAuthorization.Load(
            Universe.RepositoryPath("declarations", InstalledDeclaration),
            SealedFingerprint);

        Assert.Equal(ApprovedDigest, installed.Digest);
        Assert.Equal(0, installed.Consumed);
        Assert.Equal(6, installed.Remaining);
    }

    /// <summary>The attempt artefact carries the accounting, and the artefact is a string.</summary>
    [Fact]
    public async Task The_attempt_artefact_records_the_accounting_and_the_absent_request_window()
    {
        await WithRunner(async (runner, acquisition, authorization) =>
        {
            acquisition.Respond = request => Succeeded(request, Covering);

            var outcome = await runner.RunAsync(Context(authorization));

            using var artefact = JsonDocument.Parse(outcome.ToArtefact(Evidence, authorization));

            var root = artefact.RootElement;

            Assert.Equal(authorization.AuthorizationId, root.GetProperty("Authorization").GetString());
            Assert.Equal(authorization.Digest, root.GetProperty("AuthorizationDigest").GetString());
            Assert.Equal(1, root.GetProperty("AuthorizationConsumedThisRun").GetInt32());
            Assert.Equal(5, root.GetProperty("AuthorizationRemaining").GetInt32());
            Assert.Equal("2021-09-01", root.GetProperty("WindowFromUtc").GetString());
            Assert.Contains(
                "supportsWindow=false",
                root.GetProperty("RequestWindow").GetString()!,
                StringComparison.Ordinal);
        });
    }

    // ================= helpers =================

    private static async Task Refuses(string expectedRule, Func<SecRunContext, SecRunContext> arrange)
    {
        await WithRunner(async (runner, acquisition, authorization) =>
        {
            var outcome = await runner.RunAsync(arrange(Context(authorization)));

            Assert.Equal(SecFilingsBatchRunner.RefusedStatus, outcome.Status);
            Assert.Equal(expectedRule, outcome.RuleId);
            Assert.False(string.IsNullOrWhiteSpace(outcome.Reason));

            // The two facts every refusal must satisfy.
            Assert.Equal(0, acquisition.Calls);
            Assert.Equal(0, authorization.Consumed);
            Assert.Equal(6, authorization.Remaining);
        });
    }

    private static Task WithRunner(
        Func<SecFilingsBatchRunner, FakeAcquisition, AcquisitionAuthorization, Task> body,
        bool alreadyCompleted = false,
        string[]? symbols = null,
        DateOnly? scopeTo = null) =>
        WithRunner((runner, acquisition, authorization, _) => body(runner, acquisition, authorization),
            alreadyCompleted, symbols, scopeTo);

    private static async Task WithRunner(
        Func<SecFilingsBatchRunner, FakeAcquisition, AcquisitionAuthorization, FakeArchive, Task> body,
        bool alreadyCompleted = false,
        string[]? symbols = null,
        DateOnly? scopeTo = null)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            string.Create(CultureInfo.InvariantCulture, $"phase-b-{Guid.NewGuid():N}.json"));

        try
        {
            await File.WriteAllTextAsync(path, SyntheticDeclaration(symbols ?? Ciks, scopeTo));

            var authorization = AcquisitionAuthorization.Load(path, Evidence);
            var archive = new FakeArchive();
            var acquisition = new FakeAcquisition(archive);
            var runStore = new FakeRunStore { Completed = alreadyCompleted };

            var runner = new SecFilingsBatchRunner(acquisition, runStore, archive, new FixedClock());

            await body(runner, acquisition, authorization, archive);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static SecEdgarSixMemberPartition.SecFilingBatchDefinition Batch() =>
        SecEdgarSixMemberPartition.Batches[0] with { Authorised = true };

    private static SecRunContext Context(AcquisitionAuthorization authorization) =>
        new(
            Batch(),
            authorization,
            new HashSet<string>(Ciks, StringComparer.Ordinal),
            ActiveSource(),
            new StubProvider(),
            Contact,
            AttemptNumber: 1,
            WindowStart,
            [(WindowStart, new DateOnly(2022, 12, 27))],
            SharedForms,
            SecondaryForms,
            Lgiq,
            new HashSet<string>(StringComparer.Ordinal));

    private static string SyntheticDeclaration(string[] symbols, DateOnly? scopeTo)
    {
        string[] sources = [SecFilingsBatchRunner.Source];

        var from = SecFilingsBatchRunner.WindowFrom;
        var to = scopeTo ?? SecFilingsBatchRunner.WindowTo;

        var digest = AcquisitionAuthorization.DigestFor(
            AcquisitionAuthorization.Schema,
            Evidence,
            "sec",
            sources,
            from,
            to,
            planned: symbols.Length,
            satisfied: 0,
            ceiling: symbols.Length,
            symbols: symbols,
            supersedesAuthorizationId: null,
            alreadyConsumed: 0);

        return JsonSerializer.Serialize(new
        {
            Schema = AcquisitionAuthorization.Schema,
            AuthorizationId = "phase-b-synthetic",
            EvidenceBaseFingerprint = Evidence,
            Vendor = "sec",
            Sources = sources,
            WindowFromUtc = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            WindowToUtc = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            PlannedRequests = symbols.Length,
            AlreadySatisfied = 0,
            DispatchCeiling = symbols.Length,
            Symbols = symbols,
            AuthorizationDigest = digest,
        });
    }

    private static AcquisitionResult Succeeded(IngestionRequest request, string payload)
    {
        var run = IngestionRun.Start(request, Now);

        run.RecordArtifact(ContentHash.Compute(Encoding.UTF8.GetBytes(payload)));
        run.MarkSucceeded(Now);

        return new AcquisitionResult(run, new NormalizationSummary(1, 7, 0));
    }

    private static DataSource ActiveSource()
    {
        var source = BuildSource();

        source.Activate(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        return source;
    }

    private static DataSource InactiveSource() => BuildSource();

    private static DataSource BuildSource() =>
        DataSource.Register(
            SourceId.Create(SecFilingsBatchRunner.Source),
            "U.S. Securities and Exchange Commission - EDGAR",
            SourceType.RegulatoryAuthority,
            SourceAuthority.Primary,
            Region.UnitedStates,
            [DataCategory.RegulatoryFilings, DataCategory.CompanyProfile],
            UpdateCadence.EventDriven,
            LicensingTerms.Create(
                storageAllowed: true,
                redistributionAllowed: true,
                automatedProcessingAllowed: true,
                attributionRequired: true,
                notes: SecEdgarSource.LicensingNotes,
                retention: RetentionLimit.Unlimited),
            VerificationPolicy.Authoritative,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "Built in memory. Registration records assessment, not permission.");

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Now;
    }

    /// <summary>A connector that declares what EDGAR declares and can fetch nothing.</summary>
    private sealed class StubProvider : IDataProvider
    {
        public SourceId SourceId => SecEdgarProvider.Id;

        public ProviderCapabilities Capabilities { get; } = ProviderCapabilities.Create(
            [DataCategory.RegulatoryFilings, DataCategory.CompanyProfile],
            [Region.UnitedStates],
            ["Company"],
            supportsWindow: false,
            maxWindowDuration: null,
            quota: ProviderQuota.PerSecond(10));

        public Task<ProviderResponse> FetchAsync(
            IngestionRequest request,
            string? continuationToken = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Nothing in these tests may reach a provider. Reaching this line means a gate was skipped.");
    }

    /// <summary>
    /// Stands in for the whole production acquisition path, and archives what it claims to fetch.
    /// </summary>
    private sealed class FakeAcquisition : IDataAcquisition
    {
        private readonly FakeArchive _archive;

        public FakeAcquisition(FakeArchive archive) => _archive = archive;

        public int Calls { get; private set; }

        public IngestionRequest? Last { get; private set; }

        public Func<IngestionRequest, AcquisitionResult>? Respond { get; set; }

        public Exception? Throw { get; set; }

        public async Task<AcquisitionResult> AcquireAsync(
            IngestionRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Last = request;

            if (Throw is not null)
            {
                throw Throw;
            }

            if (Respond is null)
            {
                throw new InvalidOperationException("no response was configured for this fake");
            }

            var result = Respond(request);

            foreach (var hash in result.Run.Artifacts)
            {
                _archive.Remember(hash);
            }

            await Task.CompletedTask.ConfigureAwait(false);

            return result;
        }
    }

    /// <summary>A content-addressed archive in memory, holding what the fixtures declare.</summary>
    private sealed class FakeArchive : IRawResponseArchive
    {
        private static readonly string[] Fixtures = [Covering, CoveringButEmpty, NotCovering, Offering];

        private readonly Dictionary<string, byte[]> _stored = new(StringComparer.Ordinal);

        /// <summary>Stores whichever fixture hashes to this address, as the real archive would.</summary>
        public void Remember(ContentHash hash)
        {
            foreach (var fixture in Fixtures)
            {
                var bytes = Encoding.UTF8.GetBytes(fixture);

                if (string.Equals(ContentHash.Compute(bytes).Value, hash.Value, StringComparison.Ordinal))
                {
                    _stored[hash.Value] = bytes;

                    return;
                }
            }
        }

        public Task<ContentHash> StoreAsync(
            SourceId sourceId,
            ReadOnlyMemory<byte> payload,
            string mediaType,
            DateTime retrievedAtUtc,
            CancellationToken cancellationToken = default)
        {
            var hash = ContentHash.Compute(payload.Span);

            _stored[hash.Value] = payload.ToArray();

            return Task.FromResult(hash);
        }

        public Task<byte[]?> RetrieveAsync(ContentHash hash, CancellationToken cancellationToken = default) =>
            Task.FromResult(_stored.TryGetValue(hash.Value, out var bytes) ? bytes : null);

        public Task<bool> ExistsAsync(ContentHash hash, CancellationToken cancellationToken = default) =>
            Task.FromResult(_stored.ContainsKey(hash.Value));

        public Task<ArchivedPayload?> DescribeAsync(ContentHash hash, CancellationToken cancellationToken = default) =>
            Task.FromResult<ArchivedPayload?>(
                _stored.TryGetValue(hash.Value, out var bytes)
                    ? new ArchivedPayload(SecEdgarProvider.Id, "application/json", Now, bytes.Length)
                    : null);

        public IAsyncEnumerable<ContentHash> EnumerateAsync(CancellationToken cancellationToken = default) =>
            Nothing();

        public Task DeleteAsync(ContentHash hash, CancellationToken cancellationToken = default)
        {
            _stored.Remove(hash.Value);

            return Task.CompletedTask;
        }

        private static async IAsyncEnumerable<ContentHash> Nothing()
        {
            await Task.CompletedTask.ConfigureAwait(false);

            yield break;
        }
    }

    /// <summary>A ledger that answers the one question the runner asks it.</summary>
    private sealed class FakeRunStore : IIngestionRunStore
    {
        public bool Completed { get; init; }

        public Task RecordAsync(IngestionRun run, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IngestionRun?> GetLatestForSourceAsync(
            SourceId sourceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IngestionRun?>(null);

        public Task<IngestionRun?> GetLatestSuccessfulForSourceAsync(
            SourceId sourceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IngestionRun?>(null);

        public Task<bool> HasCompletedAsync(
            string requestFingerprint,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Completed);

        public Task<IReadOnlyList<IngestionRun>> GetRecentAsync(
            DateTime sinceUtc,
            int take,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<IngestionRun>>([]);
    }
}
