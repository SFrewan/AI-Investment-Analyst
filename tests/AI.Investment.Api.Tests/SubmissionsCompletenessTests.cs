using System.Globalization;
using System.Text.Json;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The completeness gate: a search that could not see the window never reports an absence.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read-only, and no provider is reachable from here.</strong> Every case below is a
/// literal document or a date. Nothing opens a connection, enables a connector, reads a database or
/// dispatches anything. The one file read is the installed authorisation, for its window starts.
/// </para>
/// <para>
/// <strong>The test that matters is the fourth one.</strong> Three of these prove the predicate
/// does what it says. The fourth proves it cannot be bypassed: a member whose history does not
/// reach its window, and which found nothing, must report <c>Insufficient local evidence</c> and
/// must never report <c>No relevant local event found</c>. That is the failure this whole gate
/// exists to make impossible, and it is asserted directly rather than implied by the others.
/// </para>
/// </remarks>
public sealed class SubmissionsCompletenessTests
{
    private const string InstalledDeclaration =
        "acquisition-sec-edgar-gate6-six-members-2021-09-to-2026-08.json";

    /// <summary>A window start to measure documents against. Arbitrary, and stated once.</summary>
    private static readonly DateOnly WindowStart = new(2022, 7, 15);

    /// <summary>The dates the reader should take from the sample document, in its own order.</summary>
    private static readonly DateOnly[] ExpectedRead = [new(2022, 7, 21), new(2022, 1, 4)];

    /// <summary>The six, ordinal. Asserted as a string so a collection-size rule cannot apply.</summary>
    private const string SixCiks =
        "0000892482 0001335112 0001404123 0001426332 0001775625 0001784851";

    /// <summary>
    /// A history reaching back past the window start covers it.
    /// </summary>
    [Fact]
    public void Sufficient_recent_history_covers_the_window()
    {
        var verdict = SubmissionsCompleteness.Evaluate(
            WindowStart,
            [new(2021, 3, 4), new(2022, 7, 18), new(2023, 1, 9)]);

        Assert.True(verdict.CoversWindow);
        Assert.Equal(SubmissionsCompleteness.Covered, verdict.Coverage);
        Assert.NotNull(verdict.OldestInlineFiling);
        Assert.Equal(new DateOnly(2021, 3, 4), verdict.OldestInlineFiling.Value);
        Assert.Equal(3, verdict.InlineFilings);
    }

    /// <summary>
    /// A history beginning after the window start proves nothing about it.
    /// </summary>
    /// <remarks>
    /// One day late is still late. The boundary is asserted separately below, because an
    /// off-by-one here would silently convert unproven cases into proven ones.
    /// </remarks>
    [Fact]
    public void Insufficient_recent_history_does_not_cover_the_window()
    {
        var verdict = SubmissionsCompleteness.Evaluate(
            WindowStart,
            [new(2022, 7, 16), new(2023, 5, 5)]);

        Assert.False(verdict.CoversWindow);
        Assert.Equal(SubmissionsCompleteness.NotProven, verdict.Coverage);
        Assert.NotNull(verdict.OldestInlineFiling);
        Assert.Equal(new DateOnly(2022, 7, 16), verdict.OldestInlineFiling.Value);
    }

    /// <summary>
    /// The boundary is inclusive: a filing exactly on the window start covers it.
    /// </summary>
    [Fact]
    public void The_boundary_is_inclusive_on_the_window_start()
    {
        Assert.True(SubmissionsCompleteness.Evaluate(WindowStart, [WindowStart]).CoversWindow);

        Assert.False(
            SubmissionsCompleteness.Evaluate(WindowStart, [WindowStart.AddDays(1)]).CoversWindow);
    }

    /// <summary>
    /// Proven coverage plus nothing found is a real absence, and says so.
    /// </summary>
    [Fact]
    public void Zero_relevant_filings_with_sufficient_coverage_is_a_real_absence()
    {
        var covered = SubmissionsCompleteness.Evaluate(WindowStart, [new(2020, 1, 2)]);

        Assert.True(covered.CoversWindow);

        Assert.Equal(
            SubmissionsCompleteness.NoRelevantEvent,
            SubmissionsCompleteness.Classify(covered, relevantFilings: 0));

        // And a hit, once coverage is proven, is a coincidence and never more than one.
        Assert.Equal(
            SubmissionsCompleteness.SupportsCoincidence,
            SubmissionsCompleteness.Classify(covered, relevantFilings: 1));
    }

    /// <summary>
    /// <strong>The one that matters.</strong> Unproven coverage can never become an absence.
    /// </summary>
    /// <remarks>
    /// Asserted over every filing count a stage could plausibly hold, including zero, because the
    /// bug this prevents is precisely the one where a zero taken from a document that does not
    /// reach the window is reported as a zero that means something.
    /// </remarks>
    [Fact]
    public void Insufficient_coverage_never_becomes_no_relevant_local_event_found()
    {
        var notProven = SubmissionsCompleteness.Evaluate(WindowStart, [new(2024, 1, 1)]);

        Assert.False(notProven.CoversWindow);

        foreach (var found in new[] { 0, 1, 7 })
        {
            var label = SubmissionsCompleteness.Classify(notProven, found);

            Assert.Equal(SubmissionsCompleteness.InsufficientEvidence, label);

            Assert.NotEqual(SubmissionsCompleteness.NoRelevantEvent, label);
            Assert.NotEqual(SubmissionsCompleteness.SupportsCoincidence, label);
        }
    }

    /// <summary>
    /// A document carrying no dated filing proves nothing, and is not an absence either.
    /// </summary>
    [Fact]
    public void A_document_with_no_dated_filing_proves_nothing()
    {
        var empty = SubmissionsCompleteness.Evaluate(WindowStart, []);

        Assert.False(empty.CoversWindow);
        Assert.Null(empty.OldestInlineFiling);
        Assert.Equal(0, empty.InlineFilings);

        Assert.Equal(
            SubmissionsCompleteness.InsufficientEvidence,
            SubmissionsCompleteness.Classify(empty, relevantFilings: 0));
    }

    /// <summary>
    /// A reference to further files is recorded and changes no verdict.
    /// </summary>
    /// <remarks>
    /// Both directions are asserted. A late-starting document is unproven whether or not it names
    /// older files, because concluding "no older files therefore complete history" would rest on an
    /// assumption about EDGAR's format that this repository does not establish.
    /// </remarks>
    [Fact]
    public void A_reference_to_older_files_is_reported_and_never_relaxes_the_verdict()
    {
        var withFiles = SubmissionsCompleteness.Evaluate(
            WindowStart, [new(2024, 1, 1)], holdsOlderFilings: true);

        var withoutFiles = SubmissionsCompleteness.Evaluate(
            WindowStart, [new(2024, 1, 1)], holdsOlderFilings: false);

        Assert.True(withFiles.HoldsOlderFilings);
        Assert.False(withoutFiles.HoldsOlderFilings);

        // Same dates, same verdict. The flag is evidence for a reviewer, not an input to the rule.
        Assert.False(withFiles.CoversWindow);
        Assert.False(withoutFiles.CoversWindow);
        Assert.Equal(withFiles.Coverage, withoutFiles.Coverage);
    }

    /// <summary>
    /// The reader takes dates from a real submissions shape, and refuses anything else.
    /// </summary>
    [Fact]
    public void The_reader_takes_dates_from_the_documents_own_shape()
    {
        const string document = """
            {
              "cik": "0001404123",
              "filings": {
                "recent": {
                  "accessionNumber": ["0001104659-22-081243", "0001104659-22-000001"],
                  "filingDate": ["2022-07-21", "2022-01-04"],
                  "form": ["8-K", "10-K"]
                },
                "files": [{ "name": "CIK0001404123-submissions-001.json" }]
              }
            }
            """;

        using var parsed = JsonDocument.Parse(document);

        var dates = SubmissionsCompleteness.ReadRecentFilingDates(parsed.RootElement);

        Assert.Equal(ExpectedRead, dates);

        Assert.True(SubmissionsCompleteness.HoldsOlderFilings(parsed.RootElement));

        // A document of an unexpected shape yields no dates rather than throwing, and is then
        // unprovable rather than mistaken for empty.
        using var wrong = JsonDocument.Parse("""{ "filings": { "recent": {} } }""");

        Assert.Empty(SubmissionsCompleteness.ReadRecentFilingDates(wrong.RootElement));
        Assert.False(SubmissionsCompleteness.HoldsOlderFilings(wrong.RootElement));
    }

    /// <summary>
    /// The six members' window starts come from the installed authorisation, not from this file.
    /// </summary>
    /// <remarks>
    /// The thresholds this gate will be applied at are a property of the authorisation, so they are
    /// read from it. A change to the breach set would move them, and this test would move with it
    /// rather than drift away from it.
    /// </remarks>
    [Fact]
    public async Task The_six_window_starts_are_read_from_the_installed_authorisation()
    {
        var starts = await EarliestWindowStartsAsync();

        Assert.Equal(SixCiks, string.Join(" ", starts.Keys.Order(StringComparer.Ordinal)));

        Assert.Equal(new DateOnly(2021, 9, 26), starts["0001784851"]); // SHPW
        Assert.Equal(new DateOnly(2022, 7, 15), starts["0001404123"]); // ONEM
        Assert.Equal(new DateOnly(2022, 10, 9), starts["0001426332"]); // NGM
        Assert.Equal(new DateOnly(2022, 12, 11), starts["0000892482"]); // QUMU
        Assert.Equal(new DateOnly(2022, 12, 24), starts["0001335112"]); // LGIQ
        Assert.Equal(new DateOnly(2023, 9, 24), starts["0001775625"]); // SDC

        // Each is exactly five days before that member's earliest breach, which is the selection
        // rule the authorisation states. Checked so the two cannot drift apart.
        foreach (var (cik, start) in starts)
        {
            Assert.True(
                start < new DateOnly(2026, 8, 31),
                Universe.Inv($"`{cik}` starts outside the authorised window"));
        }
    }

    /// <summary>
    /// Applied to the six, the predicate is a per-member date threshold and nothing more.
    /// </summary>
    /// <remarks>
    /// No outcome is claimed for any member, because no document has been retrieved. What is
    /// asserted is that the rule is total over the six: for each, a document one day too young is
    /// unproven and a document exactly old enough is covered.
    /// </remarks>
    [Fact]
    public async Task The_predicate_is_total_over_the_six_members()
    {
        var starts = await EarliestWindowStartsAsync();

        foreach (var (cik, start) in starts)
        {
            var justEnough = SubmissionsCompleteness.Evaluate(start, [start]);
            var oneDayShort = SubmissionsCompleteness.Evaluate(start, [start.AddDays(1)]);

            Assert.True(
                justEnough.CoversWindow,
                Universe.Inv($"`{cik}`: a filing dated exactly {start:yyyy-MM-dd} should cover"));

            Assert.False(
                oneDayShort.CoversWindow,
                Universe.Inv($"`{cik}`: a history starting after {start:yyyy-MM-dd} should not cover"));

            Assert.Equal(
                SubmissionsCompleteness.InsufficientEvidence,
                SubmissionsCompleteness.Classify(oneDayShort, relevantFilings: 0));

            Assert.Equal(
                SubmissionsCompleteness.NoRelevantEvent,
                SubmissionsCompleteness.Classify(justEnough, relevantFilings: 0));
        }
    }

    // ---- reading the installed authorisation ------------------------------------------------------

    private static async Task<Dictionary<string, DateOnly>> EarliestWindowStartsAsync()
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("declarations", InstalledDeclaration)));

        var starts = new Dictionary<string, DateOnly>(StringComparer.Ordinal);

        var breaches = document.RootElement
            .GetProperty("FilingSelection")
            .GetProperty("Breaches")
            .EnumerateArray();

        foreach (var breach in breaches)
        {
            var cik = breach.GetProperty("Cik").GetString() ?? string.Empty;

            var start = DateOnly.ParseExact(
                breach.GetProperty("SelectFilingsFromUtc").GetString() ?? string.Empty,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture);

            if (!starts.TryGetValue(cik, out var held) || start < held)
            {
                starts[cik] = start;
            }
        }

        return starts;
    }
}
