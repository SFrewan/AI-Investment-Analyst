using System.Text.Json;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The pre-registered Gate 6 rule, pinned before any price exists to fit it to.
/// </summary>
/// <remarks>
/// <para>
/// Sealing here means the rule's values are asserted in a test that must be edited deliberately,
/// not that a hash is compared. A hash would prove the file had not changed; it would not stop
/// someone changing the file and the hash together after seeing a disappointing coverage figure.
/// A named constant in a test that says <c>4</c> and <c>0</c> makes the edit visible in a diff and
/// forces it to be argued for.
/// </para>
/// <para>
/// The rule exists in <c>declarations/coverage-gate6.json</c> so an operator can read it without
/// reading C#, and these facts hold the two copies to each other.
/// </para>
/// </remarks>
public sealed class CoverageRuleTests
{
    /// <summary>Zero faults. Not a percentage.</summary>
    private const int FaultThreshold = 0;

    /// <summary>Consecutive expected sessions an interior gap may span and stay legitimate.</summary>
    private const int InteriorGapTolerance = 4;

    /// <summary>Sessions either end of a cohort boundary that a series may miss legitimately.</summary>
    private const int CohortTolerance = 10;

    /// <summary>The move that refuses a series as an unexplained discontinuity.</summary>
    private const decimal UnexplainedMoveRatio = 0.5m;

    private const string SealedUniverse = "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    [Fact]
    public void The_threshold_is_zero_faults_and_not_a_percentage()
    {
        var rule = Rule();

        Assert.Equal("zero-faults", rule.GetProperty("Threshold").GetProperty("Kind").GetString());
        Assert.Equal(FaultThreshold, rule.GetProperty("Threshold").GetProperty("Value").GetInt32());
        Assert.Equal(SealedUniverse, rule.GetProperty("AppliesToUniverse").GetString());
    }

    /// <summary>
    /// The clause the whole rule turns on.
    /// </summary>
    /// <remarks>
    /// Any coverage test that penalises a short series penalises exactly the companies that were
    /// acquired, delisted or failed - which are the members this universe was built from frames to
    /// contain. A rule that quietly excluded them would rebuild the survivorship bias inside the
    /// gate that is supposed to detect it.
    /// </remarks>
    [Fact]
    public void A_short_series_is_never_a_fault()
    {
        var legitimate = Strings(Rule().GetProperty("Legitimate"));

        Assert.Contains(legitimate, s => s.Contains("short series is never a fault", StringComparison.Ordinal));

        // And no fault clause may be satisfied by shortness alone.
        Assert.DoesNotContain(
            Strings(Rule().GetProperty("Fault")),
            s => s.Contains("short series", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_interior_gap_is_legitimate_to_four_sessions_and_a_fault_beyond()
    {
        // Classification as arithmetic, so the boundary is checked rather than described.
        Assert.True(IsLegitimateGap(1));
        Assert.True(IsLegitimateGap(InteriorGapTolerance));
        Assert.False(IsLegitimateGap(InteriorGapTolerance + 1));
        Assert.False(IsLegitimateGap(30));

        var text = string.Join(' ', Strings(Rule().GetProperty("Legitimate")));

        Assert.Contains("4 or fewer consecutive", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_series_that_ends_at_its_cohort_boundary_is_evidence_rather_than_a_gap()
    {
        // Ends on the boundary, and a little before it: both legitimate.
        Assert.True(EndsLegitimately(sessionsBeforeLastCohort: 0));
        Assert.True(EndsLegitimately(sessionsBeforeLastCohort: CohortTolerance));

        // Ends long before the member stopped appearing: that is a hole, not a delisting.
        Assert.False(EndsLegitimately(sessionsBeforeLastCohort: CohortTolerance + 1));
    }

    [Fact]
    public void Zero_observations_for_an_acquirable_symbol_is_a_fault()
    {
        Assert.Contains(
            Strings(Rule().GetProperty("Fault")),
            s => s.Contains("Zero observations", StringComparison.Ordinal));
    }

    /// <summary>
    /// The fifty-per-cent refusal is a recorded coverage event, never a silent drop.
    /// </summary>
    /// <remarks>
    /// A special dividend or a spin-off moves a price far enough to refuse the series, and the
    /// companies that pay out large and restructure are disproportionately the distressed and the
    /// acquired. Dropping those series quietly would select against the same members as the
    /// identity gap - the failure this whole line of work exists to stop.
    /// </remarks>
    [Fact]
    public void The_unexplained_move_refusal_is_recorded_with_the_member_and_the_reason()
    {
        var move = Rule().GetProperty("UnexplainedMove");

        Assert.Equal(UnexplainedMoveRatio, move.GetProperty("ThresholdRatio").GetDecimal());
        Assert.True(move.GetProperty("MustNotBeSilent").GetBoolean());

        var recording = move.GetProperty("Recording").GetString() ?? string.Empty;

        foreach (var required in new[] { "CIK", "symbol", "move", "reason", "fault" })
        {
            Assert.Contains(required, recording, StringComparison.OrdinalIgnoreCase);
        }

        // It is a fault, so a refused series cannot pass the gate by being refused.
        Assert.Contains(
            Strings(Rule().GetProperty("Fault")),
            s => s.Contains("unexplained discontinuity", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Price return, said in the evidence base rather than left for a reader to infer.
    /// </summary>
    [Fact]
    public void The_measure_is_recorded_as_price_return_and_not_total_return()
    {
        var semantics = Rule().GetProperty("ReturnSemantics");

        Assert.Equal("price return, not total return", semantics.GetProperty("Measure").GetString());

        var dividends = semantics.GetProperty("Dividends").GetString() ?? string.Empty;

        Assert.StartsWith("diagnosis-only", dividends, StringComparison.Ordinal);

        // Diagnosis-only means exactly this: it explains, it does not adjust and it does not
        // change what an outcome means.
        Assert.Contains("does not adjust prices", dividends, StringComparison.Ordinal);
        Assert.Contains("does not change outcome resolution", dividends, StringComparison.Ordinal);

        // The known bias is stated rather than discovered later.
        Assert.Contains(
            "ex-date",
            semantics.GetProperty("KnownBias").GetString() ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public void What_the_rule_cannot_see_is_written_down_rather_than_assumed_away()
    {
        var scope = string.Join(' ', Strings(Rule().GetProperty("NotInScope")));

        Assert.Contains("half-day", scope, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Symbol changes cannot be detected", scope, StringComparison.Ordinal);
    }

    // ---- the classification, as arithmetic -------------------------------------------------------

    private static bool IsLegitimateGap(int consecutiveMissedSessions) =>
        consecutiveMissedSessions <= InteriorGapTolerance;

    private static bool EndsLegitimately(int sessionsBeforeLastCohort) =>
        sessionsBeforeLastCohort <= CohortTolerance;

    private static JsonElement Rule()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Universe.RepositoryPath("declarations", "coverage-gate6.json")));

        return document.RootElement.Clone();
    }

    private static List<string> Strings(JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
}
