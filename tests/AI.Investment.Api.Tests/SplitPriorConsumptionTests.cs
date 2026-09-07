using System.Text.Json;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Prior consumption is charged to the authorisation that was actually charged, and to no other.
/// </summary>
/// <remarks>
/// <para>
/// Ungated and hermetic: every case here builds its own artefacts in a temporary directory and
/// deletes them. No provider, no store, no network, and nothing under <c>artifacts/</c> is read or
/// written. The live reconciliation lives in <see cref="SplitAccountingReconciliationTests"/>,
/// which is gated, so a fresh clone can still run this.
/// </para>
/// <para>
/// <strong>What went wrong, stated once.</strong> The split runner totalled every
/// <c>acquisition-splits-*.json</c> artefact to work out what had already been spent. That was
/// correct while one authorisation covered the whole phase. Once the phase was superseded, the 210
/// units the predecessor spent were still being counted against the successor's ceiling - so a
/// successor batch that correctly expects nothing spent would have been refused. These tests hold
/// the boundary that fixes it.
/// </para>
/// </remarks>
public sealed class SplitPriorConsumptionTests
{
    private const string Successor = "eodhd-sample400-splits-remainder-2021-09-to-2026-08";
    private const string Predecessor = "eodhd-sample400-splits-2021-09-to-2026-08";
    private const string Stranger = "some-other-authorisation-nobody-approved";

    /// <summary>The declaration the partition currently names. Follows the head of the chain.</summary>
    private const string ActiveDeclaration =
        "acquisition-eodhd-splits-final-2021-09-to-2026-08.json";

    private const string PredecessorDeclaration = "acquisition-eodhd-splits-sample400.json";

    /// <summary>An artefact naming the predecessor is not the successor's spending.</summary>
    /// <remarks>
    /// The two totals are asked for separately from the same directory, which is the property that
    /// matters: the predecessor's 210 and the successor's 1 never become 211.
    /// </remarks>
    [Fact]
    public async Task Predecessor_and_successor_artefacts_are_never_combined()
    {
        using var directory = new Scratch();

        await directory.WriteArtefactAsync("acquisition-splits-01-attempt-01.json", Predecessor, 70);
        await directory.WriteArtefactAsync("acquisition-splits-02-attempt-01.json", Predecessor, 70);
        await directory.WriteArtefactAsync("acquisition-splits-03-attempt-01.json", Predecessor, 70);
        await directory.WriteArtefactAsync("acquisition-splits-04-attempt-01.json", Successor, 1);

        var underPredecessor = await AcquisitionSplitBatchTests
            .ConsumedUnderAsync(directory.Path, Predecessor);

        var underSuccessor = await AcquisitionSplitBatchTests
            .ConsumedUnderAsync(directory.Path, Successor);

        Assert.Equal(210, underPredecessor);
        Assert.Equal(1, underSuccessor);
        Assert.NotEqual(underPredecessor + underSuccessor, underSuccessor);
    }

    /// <summary>Before its first batch, a fresh successor has spent nothing at all.</summary>
    [Fact]
    public async Task A_fresh_successor_has_spent_nothing_however_much_its_predecessor_spent()
    {
        using var directory = new Scratch();

        await directory.WriteArtefactAsync("acquisition-splits-01-attempt-01.json", Predecessor, 70);
        await directory.WriteArtefactAsync("acquisition-splits-02-attempt-01.json", Predecessor, 70);
        await directory.WriteArtefactAsync("acquisition-splits-03-attempt-01.json", Predecessor, 70);

        Assert.Equal(0, await AcquisitionSplitBatchTests.ConsumedUnderAsync(directory.Path, Successor));
        Assert.Equal(210, await AcquisitionSplitBatchTests.ConsumedUnderAsync(directory.Path, Predecessor));
    }

    /// <summary>And once it has spent something, that is what it has spent.</summary>
    [Fact]
    public async Task A_successor_artefact_of_one_unit_makes_the_successors_prior_consumption_one()
    {
        using var directory = new Scratch();

        await directory.WriteArtefactAsync("acquisition-splits-01-attempt-01.json", Predecessor, 210);
        await directory.WriteArtefactAsync("acquisition-splits-01-attempt-02.json", Successor, 1);

        Assert.Equal(1, await AcquisitionSplitBatchTests.ConsumedUnderAsync(directory.Path, Successor));
    }

    /// <summary>An artefact naming an authorisation nobody is running is not counted.</summary>
    [Fact]
    public async Task An_artefact_naming_an_unknown_authorisation_is_not_counted()
    {
        using var directory = new Scratch();

        await directory.WriteArtefactAsync("acquisition-splits-09-attempt-01.json", Stranger, 999);

        Assert.Equal(0, await AcquisitionSplitBatchTests.ConsumedUnderAsync(directory.Path, Successor));
        Assert.Equal(0, await AcquisitionSplitBatchTests.ConsumedUnderAsync(directory.Path, Predecessor));
    }

    /// <summary>
    /// An artefact that names no authorisation stops the calculation rather than vanishing from it.
    /// </summary>
    /// <remarks>
    /// The important distinction. "Charged to something else" and "does not say what it charged" are
    /// different facts, and only the first is safe to skip. Silently ignoring the second would make
    /// a malformed record look exactly like an absent one.
    /// </remarks>
    [Fact]
    public async Task An_artefact_that_names_no_authorisation_is_refused()
    {
        using var directory = new Scratch();

        await directory.WriteArtefactAsync("acquisition-splits-01-attempt-01.json", null, 70);

        var failure = await Record.ExceptionAsync(() =>
            AcquisitionSplitBatchTests.ConsumedUnderAsync(directory.Path, Successor));

        Assert.NotNull(failure);
        Assert.Contains("does not name the authorisation", failure!.Message, StringComparison.Ordinal);
    }

    /// <summary>The authorisations a directory's artefacts name, for reporting rather than charging.</summary>
    [Fact]
    public async Task The_authorisations_an_artefact_set_names_are_reported_distinctly()
    {
        using var directory = new Scratch();

        await directory.WriteArtefactAsync("acquisition-splits-01-attempt-01.json", Predecessor, 70);
        await directory.WriteArtefactAsync("acquisition-splits-02-attempt-01.json", Predecessor, 70);
        await directory.WriteArtefactAsync("acquisition-splits-04-attempt-01.json", Successor, 1);

        Assert.Equal(
            [Predecessor, Successor],
            await AcquisitionSplitBatchTests.AuthorizationsInAsync(directory.Path));
    }

    /// <summary>
    /// The runner's pin still refuses when the active authorisation's own accounting moves.
    /// </summary>
    /// <remarks>
    /// Scoping the total to one authorisation makes it correct; it does not make it slack. Batch 1
    /// of the current partition expects zero spent under the successor, so a successor artefact
    /// appearing before it runs makes the totals disagree - and the runner compares them for exact
    /// equality before it dispatches anything.
    /// </remarks>
    [Fact]
    public async Task The_pin_still_refuses_when_the_active_authorisations_accounting_moves()
    {
        var batchOne = AcquisitionSplitBatchTests.Partition[0];

        using var directory = new Scratch();

        await directory.WriteArtefactAsync("acquisition-splits-01-attempt-01.json", Predecessor, 210);

        // Nothing spent under the successor yet: the pin agrees and the batch could proceed.
        Assert.Equal(
            batchOne.ExpectedPriorConsumption,
            await AcquisitionSplitBatchTests.ConsumedUnderAsync(directory.Path, Successor));

        await directory.WriteArtefactAsync("acquisition-splits-01-attempt-02.json", Successor, 1);

        // One unit spent under it, and the same pin now disagrees.
        Assert.NotEqual(
            batchOne.ExpectedPriorConsumption,
            await AcquisitionSplitBatchTests.ConsumedUnderAsync(directory.Path, Successor));
    }

    /// <summary>Batch 1 of the partition expects nothing spent, which is what makes it batch 1.</summary>
    /// <remarks>
    /// The declaration file name and the authorisation identifier inside it are deliberately not
    /// the same string - a file is named for what it is about, an authorisation for what it
    /// authorises - so this checks the file the batch names rather than pattern-matching one
    /// against the other.
    /// </remarks>
    [Fact]
    public void The_first_batch_expects_nothing_spent_under_the_authorisation_it_names()
    {
        var batchOne = AcquisitionSplitBatchTests.Partition[0];

        Assert.Equal(0, batchOne.ExpectedPriorConsumption);
        Assert.Equal(ActiveDeclaration, batchOne.Declaration);
        Assert.NotEqual(PredecessorDeclaration, batchOne.Declaration);
    }

    // ---- a directory that cleans up after itself -----------------------------------------------

    private sealed class Scratch : IDisposable
    {
        public Scratch()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"split-prior-{Guid.NewGuid():N}");

            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        /// <summary>An artefact shaped like the ones a split batch writes, and nothing more.</summary>
        public async Task WriteArtefactAsync(string name, string? authorization, int consumed)
        {
            var body = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["BatchIndex"] = 1,
                [AcquisitionSplitBatchTests.ConsumedProperty] = consumed,
            };

            if (authorization is not null)
            {
                body[AcquisitionSplitBatchTests.AuthorizationProperty] = authorization;
            }

            await File.WriteAllTextAsync(
                System.IO.Path.Combine(Path, name),
                JsonSerializer.Serialize(body, Universe.Json));
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
