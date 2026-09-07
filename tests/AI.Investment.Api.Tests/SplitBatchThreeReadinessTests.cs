using System.Globalization;
using System.Text.Json;
using AI.Investment.Domain.Sources;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The corporate-actions acquisition is finished, and nothing here can start another. Reads only.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No provider, no store, no network.</strong> Every fact reads the declarations, the
/// artefacts already on disk, the partition, and in one case the runner's own source text.
/// </para>
/// <para>
/// <strong>What this class used to be.</strong> It was written as the readiness gate before the
/// last batch ran, and its first fact asserted that exactly one batch declared a prior consumption
/// matching what the authorisation had spent - the batch that would run next. That invariant was
/// true only while a next batch existed. When batch 3 completed, the spend reached the ceiling and
/// no batch matched it any more, so the fact failed - correctly reporting the end of the work it
/// was written to police. It has been rebased to prove the finished state instead of a next step,
/// which is a change of contract and is recorded here as one rather than made quietly.
/// </para>
/// <para>
/// <strong>What lives here and what does not.</strong> These facts are derivable from files alone.
/// The live-ledger figures - what the store has actually satisfied, and what it still owes - belong
/// to <see cref="SplitAccountingReconciliationTests"/>, which has the database fixture for them.
/// The one live figure used here is read from the final batch's own artefact, where the production
/// runner recorded the outstanding count it measured before dispatching.
/// </para>
/// </remarks>
public sealed class SplitBatchThreeReadinessTests
{
    private const string SealedFingerprint =
        "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private const string ActiveDeclaration =
        "acquisition-eodhd-splits-final-2021-09-to-2026-08.json";

    private const string ActiveAuthorizationId =
        "eodhd-sample400-splits-final-2021-09-to-2026-08";

    /// <summary>The whole of the work this authorisation was sized for, now done.</summary>
    private const int Ceiling = 73;

    private const int Batches = 3;

    /// <summary>The artefact the final batch left, and the attempt number it carries.</summary>
    private const string FinalArtefact = "acquisition-splits-03-attempt-02.json";

    /// <summary>
    /// The ceiling is spent to the unit, nothing remains, and no batch is eligible to run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four claims, each said in the terms the production accounting uses rather than as a literal
    /// restated from a report. The spend is recomputed from the artefacts that name this
    /// authorisation; the ceiling and the arithmetic come from the loaded declaration; and
    /// eligibility is asked exactly as the runner asks it - a batch may run only when the artefacts
    /// attribute to this authorisation precisely what the batch declares was spent before it, and
    /// only when enough units remain to pay for it. Neither is true of any batch now.
    /// </para>
    /// <para>
    /// The outstanding figure is the one thing here that came from the live ledger, and it is read
    /// from where the runner recorded it: the final batch measured what the store still owed, spent
    /// exactly that, and every request succeeded. Nothing is owed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_authorisation_is_spent_and_no_batch_remains()
    {
        var active = AcquisitionAuthorization.Load(
            Universe.RepositoryPath("declarations", ActiveDeclaration),
            SealedFingerprint);

        var universe = Universe.RepositoryPath("artifacts", "universe");

        var spent = await AcquisitionSplitBatchTests.ConsumedUnderAsync(
            universe, active.AuthorizationId);

        // Consumed to the ceiling, and the ceiling is what it always was.
        Assert.Equal(ActiveAuthorizationId, active.AuthorizationId);
        Assert.Equal(Ceiling, active.DispatchCeiling);
        Assert.Equal(Ceiling, spent);
        Assert.Equal(active.PlannedRequests - active.AlreadySatisfied, active.DispatchCeiling);

        // Charged through the production counter rather than asserted as arithmetic, because the
        // counter is what a run would use - and it refuses to be over-spent rather than clamping,
        // so reaching exactly zero here is also proof that no overrun occurred.
        active.RecordPriorConsumption(spent);

        Assert.Equal(Ceiling, active.Consumed);
        Assert.Equal(0, active.Remaining);

        // No batch declares a prior consumption equal to what has been spent, so the runner's pin
        // admits none of them. This is the assertion that replaced "exactly one batch does".
        Assert.DoesNotContain(
            AcquisitionSplitBatchTests.Partition,
            batch => batch.ExpectedPriorConsumption == spent);

        // And even if one did, there is nothing left to pay for it.
        Assert.All(
            AcquisitionSplitBatchTests.Partition,
            batch => Assert.True(
                batch.Count > active.Remaining,
                Universe.Inv($"batch {batch.Index} needs {batch.Count} unit(s) and {active.Remaining} remain; a spent authorisation must not admit one")));

        // The work itself is done. The final batch measured what the ledger owed, spent exactly
        // that, and every request succeeded - so the outstanding count it started from is closed.
        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(universe, FinalArtefact)));

        var root = document.RootElement;
        var before = root.GetProperty("OutstandingSplitsBefore").GetInt32();
        var dispatched = root.GetProperty("Dispatched").GetInt32();
        var succeeded = root.GetProperty("Succeeded").GetInt32();

        Assert.Equal(ActiveAuthorizationId, root.GetProperty(AcquisitionSplitBatchTests.AuthorizationProperty).GetString());
        Assert.Equal(before, dispatched);
        Assert.Equal(dispatched, succeeded);
        Assert.Equal(0, before - succeeded);

        // Every batch in the partition is accounted for by a run that charged this authorisation.
        Assert.Equal(Ceiling, AcquisitionSplitBatchTests.Partition.Sum(b => b.Count));
        Assert.Equal(spent, AcquisitionSplitBatchTests.Partition.Sum(b => b.Count));
    }

    /// <summary>
    /// The partition is closed: three batches, no fourth, and no successor authorisation.
    /// </summary>
    /// <remarks>
    /// The two ways this could quietly restart are a fourth batch appearing in the partition and a
    /// declaration appearing that supersedes the spent one. Both would look ordinary in a diff and
    /// neither would fail anything else here, so both are named. A successor is not forbidden
    /// forever - it is forbidden without the deliberate act of deleting this assertion.
    /// </remarks>
    [Fact]
    public async Task The_partition_is_closed_and_no_successor_authorisation_exists()
    {
        var partition = AcquisitionSplitBatchTests.Partition;

        Assert.Equal(Batches, partition.Length);
        Assert.Equal([1, 2, 3], partition.Select(b => b.Index).ToList());
        Assert.DoesNotContain(partition, b => b.Index > Batches);
        Assert.All(partition, b => Assert.Equal(ActiveDeclaration, b.Declaration));

        // And none of them is approved. This is the resting state now rather than a pause between
        // approvals - the ceiling is spent and the ledger owes nothing, so there is no next batch
        // for an approval to open. Asserted here as well as in the readiness tests because it has
        // stopped being a phase of the approval cycle and become a property of the finished work.
        // The unit checks below stay flag-independent on purpose: a spent authorisation has to be
        // safe even if some future edit sets a flag by mistake.
        Assert.DoesNotContain(partition, b => b.Authorised);

        var declarations = Universe.RepositoryPath("declarations");

        foreach (var path in Directory
            .EnumerateFiles(declarations, "acquisition-*.json")
            .Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

            if (!document.RootElement.TryGetProperty("SupersedesAuthorizationId", out var supersedes))
            {
                continue;
            }

            Assert.False(
                string.Equals(supersedes.GetString(), ActiveAuthorizationId, StringComparison.Ordinal),
                Universe.Inv($"`{Path.GetFileName(path)}` supersedes `{ActiveAuthorizationId}`, which is spent and needs no successor: the corporate-actions work is complete"));
        }
    }

    /// <summary>
    /// No two attempts anywhere in the split record share a correlation identifier.
    /// </summary>
    /// <remarks>
    /// This began as a forward-looking check that the identifiers the next attempt <em>would</em>
    /// carry were unused. There is no next attempt now, so the useful half is the one that keeps
    /// its meaning for good: reconstruct the identifier of every attempt every split run recorded,
    /// and require them all distinct. An idempotency collision anywhere in the history - past or
    /// introduced later by a renumbered batch or a reused attempt - fails here.
    /// </remarks>
    [Fact]
    public async Task Every_recorded_attempt_identity_is_unique()
    {
        var universe = Universe.RepositoryPath("artifacts", "universe");
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var total = 0;

        foreach (var path in Directory
            .EnumerateFiles(universe, AcquisitionSplitBatchTests.ArtefactPattern)
            .Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var parts = name.Split('-');
            var batch = 0;
            var attempt = 0;

            var named = parts.Length == 5
                && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out batch)
                && int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out attempt);

            Assert.True(
                named,
                Universe.Inv($"`{name}` does not name a batch and attempt, so its identifiers cannot be reconstructed"));

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

            foreach (var entry in document.RootElement.GetProperty("Attempts").EnumerateArray())
            {
                var symbol = entry.GetProperty("Symbol").GetString();

                if (symbol is null)
                {
                    continue;
                }

                var identity = AcquisitionCorrelation.Text(
                    batch, attempt, symbol, DataCategory.CorporateActions);

                Assert.False(
                    seen.ContainsKey(identity),
                    Universe.Inv($"`{identity}` is recorded in both `{seen.GetValueOrDefault(identity)}` and `{name}`"));

                seen[identity] = name;
                total++;
            }
        }

        // And the record is not empty, so "all distinct" cannot pass by finding nothing.
        Assert.Equal(total, seen.Count);
        Assert.True(total >= Ceiling, Universe.Inv($"{total} attempt(s) recorded, fewer than the {Ceiling} this authorisation alone spent"));

        // The final batch's two identifiers are the ones the readiness gate predicted before it ran.
        Assert.Contains("batch-3-a2-ZWS-US-CorporateActions", seen.Keys, StringComparer.Ordinal);
        Assert.Contains("batch-3-a2-ZYME-US-CorporateActions", seen.Keys, StringComparer.Ordinal);
    }

    /// <summary>
    /// The runner reaches its authorisation gate before it reaches anything that could dispatch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An unauthorised batch has to be refused <em>before</em> a provider exists, not merely before
    /// a request is built - otherwise "nothing was dispatched" depends on where the failure happened
    /// to land. The runner's ordering is the guarantee, so the ordering is what is checked: the
    /// authorisation assertion appears in the source ahead of the first service scope, the first
    /// resolved service, the acquisition port and the call that dispatches.
    /// </para>
    /// <para>
    /// Reading source text is a blunt instrument and deliberately so. There is no way to prove a
    /// statement ordering by reflection, and the alternative - running the runner against an
    /// unauthorised batch to watch it refuse - is a test that spends real money when it is wrong.
    /// If a rename breaks this, the right response is to re-establish the ordering by eye and then
    /// fix the token, not to delete the test.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_runner_checks_authorisation_before_anything_can_reach_a_provider()
    {
        var source = await File.ReadAllTextAsync(Universe.RepositoryPath(
            "tests", "AI.Investment.Api.Tests", "AcquisitionSplitBatchTests.cs"));

        var gate = source.IndexOf("batch!.Authorised", StringComparison.Ordinal);

        Assert.True(
            gate >= 0,
            "the split runner no longer tests whether the batch is authorised, or tests it by "
            + "another name. Nothing here can vouch for an ordering it cannot find.");

        foreach (var token in new[]
        {
            "CreateScope",
            "GetRequiredService",
            "IDataAcquisition",
            "AcquireAsync",
        })
        {
            var first = source.IndexOf(token, StringComparison.Ordinal);

            Assert.True(
                first < 0 || gate < first,
                Universe.Inv($"`{token}` first appears at offset {first}, ahead of the authorisation gate at {gate}. An unauthorised batch could reach it."));
        }
    }
}
