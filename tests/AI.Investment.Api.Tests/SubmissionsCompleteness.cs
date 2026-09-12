using System.Globalization;
using System.Text.Json;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Whether an archived EDGAR submissions document can be shown to cover the window asked of it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The distinction this exists to protect.</strong> "No relevant local event found" and
/// "the history is not proven complete" are opposite claims that look identical in a result table.
/// The first says the filings were searched and nothing was there. The second says the search could
/// not see far enough back to say. Reporting the second as the first would be reporting an absence
/// of evidence as evidence of absence - which is the exact error the whole Gate 6 investigation has
/// been avoiding since it found that the six members hold no financial observations at all.
/// </para>
/// <para>
/// <strong>Why this can be pure.</strong> <c>SecEdgarProvider</c> fetches bytes and archives them;
/// it makes one request and returns one document. Everything here happens afterwards, over that
/// document, and needs no connector, no network and no store. So the rule is a function from
/// (window start, the dates the document carries) to a verdict, and it lives beside
/// <see cref="AI.Investment.Domain.Coverage.CoverageEvaluation"/> and
/// <c>ObservationDeduplication</c> for the same reason those do: a decision worth making is worth
/// making somewhere it can be tested without a provider.
/// </para>
/// <para>
/// <strong>What it deliberately does not do.</strong> It follows no reference, issues no second
/// request and assumes no numeric cap on how many filings a submissions document carries inline.
/// The SEC's inline limit is not established anywhere in this repository, so nothing here depends
/// on knowing it: the predicate compares dates, which the document states, rather than counts,
/// which it does not explain.
/// </para>
/// </remarks>
internal static class SubmissionsCompleteness
{
    /// <summary>The history reaches at least as far back as the window asked for.</summary>
    public const string Covered = "covered";

    /// <summary>The history does not reach far enough back to say anything about the window.</summary>
    public const string NotProven = "not proven";

    /// <summary>
    /// The label a member gets when its submissions history cannot be shown to cover its window.
    /// </summary>
    public const string InsufficientEvidence = "Insufficient local evidence";

    /// <summary>The label for a proven-complete search that found nothing.</summary>
    public const string NoRelevantEvent = "No relevant local event found";

    /// <summary>The label for a proven-complete search that found something dated nearby.</summary>
    public const string SupportsCoincidence =
        "Local evidence supports coincidence; causation unconfirmed";

    /// <summary>
    /// Whether the dates a submissions document carries reach back to the window asked of it.
    /// </summary>
    /// <param name="earliestWindowStart">
    /// The earliest date any of this member's filing-selection windows begins at. A document that
    /// starts later than this cannot be searched for the earliest breach, whatever it contains.
    /// </param>
    /// <param name="recentFilingDates">
    /// Every <c>filingDate</c> the document carries inline under <c>filings.recent</c>. Order does
    /// not matter; the oldest is taken.
    /// </param>
    /// <param name="holdsOlderFilings">
    /// Whether the document refers to further files under <c>filings.files</c>. Recorded and
    /// reported, and deliberately <strong>not</strong> used to relax the verdict - see the remarks.
    /// </param>
    /// <remarks>
    /// <para>
    /// The rule is one comparison: the oldest inline filing date must be at or before the earliest
    /// window start. If it is, every filing in the window is inside the document and an absence
    /// there is a real absence. If it is not, the document begins after the question does.
    /// </para>
    /// <para>
    /// <strong>Why <paramref name="holdsOlderFilings"/> does not soften a refusal.</strong> It is
    /// tempting to argue that a document referring to no further files must be a complete history,
    /// so a late start would mean the company simply filed nothing earlier. That may well be true,
    /// but it rests on an assumption about EDGAR's document format that nothing in this repository
    /// establishes - and the whole point of this class is to stop an unproven assumption being
    /// reported as a finding. The flag is carried into the verdict so a reviewer can see it and
    /// decide; it changes no outcome here.
    /// </para>
    /// </remarks>
    public static Verdict Evaluate(
        DateOnly earliestWindowStart,
        IEnumerable<DateOnly> recentFilingDates,
        bool holdsOlderFilings = false)
    {
        ArgumentNullException.ThrowIfNull(recentFilingDates);

        var dates = recentFilingDates.ToList();

        if (dates.Count == 0)
        {
            // No dated filing at all. Nothing can be shown about the window, and an empty document
            // is precisely the case most likely to be mistaken for "nothing happened".
            return new Verdict(
                false,
                NotProven,
                null,
                earliestWindowStart,
                holdsOlderFilings,
                0,
                "the document carries no dated filing inline, so nothing about the window can be "
                + "established from it");
        }

        var oldest = dates.Min();
        var covers = oldest <= earliestWindowStart;

        return new Verdict(
            covers,
            covers ? Covered : NotProven,
            oldest,
            earliestWindowStart,
            holdsOlderFilings,
            dates.Count,
            covers
                ? Inv($"the oldest inline filing is dated {oldest:yyyy-MM-dd}, at or before the earliest window start {earliestWindowStart:yyyy-MM-dd}, so every filing in the window is inside this document")
                : Inv($"the oldest inline filing is dated {oldest:yyyy-MM-dd}, which is after the earliest window start {earliestWindowStart:yyyy-MM-dd}; the document begins after the question does and cannot answer it"));
    }

    /// <summary>
    /// The label a member's result carries, given its coverage and what the search found.
    /// </summary>
    /// <param name="coverage">The verdict from <see cref="Evaluate"/>.</param>
    /// <param name="relevantFilings">Filings found inside the selection windows.</param>
    /// <remarks>
    /// <strong>Coverage is checked first, and that ordering is the entire rule.</strong> A search
    /// that could not see the window returns <see cref="InsufficientEvidence"/> whatever it found
    /// or did not find - because a count of zero taken from a document that does not reach the
    /// window is not a count of zero, it is no count at all. Only once coverage is established does
    /// the number of filings mean anything.
    /// </remarks>
    public static string Classify(Verdict coverage, int relevantFilings)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentOutOfRangeException.ThrowIfNegative(relevantFilings);

        if (!coverage.CoversWindow)
        {
            return InsufficientEvidence;
        }

        return relevantFilings == 0 ? NoRelevantEvent : SupportsCoincidence;
    }

    /// <summary>Every <c>filingDate</c> the document carries inline, in the order it states them.</summary>
    /// <remarks>
    /// Total by design: a document of an unexpected shape yields no dates rather than an exception,
    /// and <see cref="Evaluate"/> then refuses to prove anything from it. A malformed document and
    /// a document that does not reach the window are the same answer here - neither can be
    /// searched - and both are safer than a parser that throws inside a reporting stage.
    /// </remarks>
    public static List<DateOnly> ReadRecentFilingDates(JsonElement root)
    {
        var dates = new List<DateOnly>();

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("filings", out var filings)
            || filings.ValueKind != JsonValueKind.Object
            || !filings.TryGetProperty("recent", out var recent)
            || recent.ValueKind != JsonValueKind.Object
            || !recent.TryGetProperty("filingDate", out var filed)
            || filed.ValueKind != JsonValueKind.Array)
        {
            return dates;
        }

        foreach (var element in filed.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String
                && DateOnly.TryParseExact(
                    element.GetString(),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date))
            {
                dates.Add(date);
            }
        }

        return dates;
    }

    /// <summary>Whether the document refers to further filing files it does not carry inline.</summary>
    /// <remarks>
    /// Read, reported, and never followed. The connector issues one request and this changes
    /// nothing about that.
    /// </remarks>
    public static bool HoldsOlderFilings(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("filings", out var filings)
        && filings.ValueKind == JsonValueKind.Object
        && filings.TryGetProperty("files", out var files)
        && files.ValueKind == JsonValueKind.Array
        && files.GetArrayLength() > 0;

    private static string Inv(FormattableString text) =>
        FormattableString.Invariant(text);

    /// <param name="CoversWindow">Whether the window can be searched in this document at all.</param>
    /// <param name="Coverage">
    /// <see cref="Covered"/> or <see cref="NotProven"/>. Named rather than boolean in reports, so a
    /// table never shows a bare false that a reader can mistake for "nothing found".
    /// </param>
    /// <param name="OldestInlineFiling">The oldest date the document carries, or null when it carries none.</param>
    /// <param name="EarliestWindowStart">The date the window needs the document to reach.</param>
    /// <param name="HoldsOlderFilings">The document refers to files it does not carry. Reported, not acted on.</param>
    /// <param name="InlineFilings">How many dated filings the document carries inline.</param>
    /// <param name="Reason">Why, in the words an operator reads.</param>
    internal sealed record Verdict(
        bool CoversWindow,
        string Coverage,
        DateOnly? OldestInlineFiling,
        DateOnly EarliestWindowStart,
        bool HoldsOlderFilings,
        int InlineFilings,
        string Reason);
}
