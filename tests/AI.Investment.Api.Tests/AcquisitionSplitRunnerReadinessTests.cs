using System.Reflection;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The split runner cannot run, and could not acquire a price if it did.
/// </summary>
/// <remarks>
/// <para>
/// These are ungated on purpose. "No split request can be dispatched yet" is a claim the project
/// depends on between approvals, and a claim that only holds while somebody remembers it is not a
/// control. Checking it on every build makes flipping the switch a visible, deliberate act that
/// fails these tests until the partition is cut against an approved authorisation.
/// </para>
/// <para>
/// No provider, no store, no network, no file on disk is touched here - only the shape of the two
/// runner types.
/// </para>
/// </remarks>
public sealed class AcquisitionSplitRunnerReadinessTests
{
    private const string SplitSourceValue = "eodhd-splits";
    private const string PriceSourceValue = "eodhd-eod";

    /// <summary>
    /// The partition is cut and covers exactly the outstanding work.
    /// </summary>
    /// <remarks>
    /// The boundaries came from the live ledger; this holds them to the shape that was approved -
    /// a single-symbol recovery batch for the request whose unit was spent on a failure, then one
    /// of seventy and a remainder of two, seventy-three in total. A fourth batch, or a batch that
    /// grew, means the partition was re-cut without the authorisation being re-cut with it.
    /// </remarks>
    [Fact]
    public void The_split_partition_covers_the_outstanding_work_and_authorises_none_of_it()
    {
        var partition = AcquisitionSplitBatchTests.Partition;

        Assert.Equal(3, partition.Length);
        Assert.Equal([1, 70, 2], partition.Select(b => b.Count).ToList());
        Assert.Equal(73, partition.Sum(b => b.Count));
        Assert.Equal([1, 2, 3], partition.Select(b => b.Index).ToList());

        // Ordinal, non-overlapping and ascending: batch n ends before batch n+1 begins.
        for (var i = 1; i < partition.Length; i++)
        {
            Assert.True(
                string.CompareOrdinal(partition[i - 1].Last, partition[i].First) < 0,
                Universe.Inv($"batch {partition[i - 1].Index} ends at `{partition[i - 1].Last}` and batch {partition[i].Index} begins at `{partition[i].First}`"));
        }

        // The first batch charges nothing: what earlier authorisations in the chain spent is carried
        // in this declaration as evidence, and none of it is spending against this ceiling.
        Assert.Equal(0, partition[0].ExpectedPriorConsumption);

        // Every later batch expects exactly what the batches before it spent. Said twice on purpose,
        // once against the running total of the counts and once against the outstanding column, so
        // that the two projections are pinned to each other and neither can be nudged alone.
        var spent = 0;

        foreach (var batch in partition)
        {
            Assert.Equal(spent, batch.ExpectedPriorConsumption);
            Assert.Equal(73 - batch.ExpectedOutstandingBefore, batch.ExpectedPriorConsumption);

            spent += batch.Count;
        }

        Assert.Equal(73, spent);
    }

    /// <summary>Every batch names the split declaration, and none names the price one.</summary>
    [Fact]
    public void Every_split_batch_names_the_split_declaration()
    {
        Assert.All(
            AcquisitionSplitBatchTests.Partition,
            batch => Assert.Equal(
                "acquisition-eodhd-splits-final-2021-09-to-2026-08.json",
                batch.Declaration));

        // No superseded declaration can be named. The price authorisation covers both endpoints, so
        // a split batch pointed at it would be in scope and would spend a budget nobody approved for
        // splits; each superseded split authorisation is spent against the work it was sized for and
        // cannot pay for the 73 the ledger now owes. All three are historical records, and a batch
        // that named one would be charging against a closed account. The list grows with the chain:
        // an authorisation that is superseded is added here in the same edit that supersedes it.
        foreach (var superseded in new[]
        {
            "acquisition-eodhd-sample400.json",
            "acquisition-eodhd-splits-sample400.json",
            "acquisition-eodhd-splits-remainder-2021-09-to-2026-08.json",
        })
        {
            Assert.DoesNotContain(
                AcquisitionSplitBatchTests.Partition,
                batch => string.Equals(batch.Declaration, superseded, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// No batch is authorised, and at most one ever may be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All three batches have run - <c>MYO.US</c>, then the seventy from <c>SAMG.US</c> to
    /// <c>ZS.US</c>, then <c>ZWS.US</c> and <c>ZYME.US</c> - and each was returned to unauthorised
    /// once its approval was spent. They stay in the partition because it records the whole of the
    /// work this authorisation was sized for, and the record does not shrink as the work is done.
    /// </para>
    /// <para>
    /// This is now the resting state rather than a pause between approvals: the authorisation is
    /// spent to its ceiling and the ledger owes nothing, so there is no next batch for an approval
    /// to open. The "at most one" cap is kept anyway, because it is a property of how approvals
    /// work rather than of this particular partition being finished.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_split_batch_is_authorised()
    {
        var authorised = AcquisitionSplitBatchTests.Partition
            .Where(b => b.Authorised)
            .Select(b => b.Index)
            .ToList();

        Assert.True(
            authorised.Count <= 1,
            Universe.Inv($"batches {string.Join(", ", authorised)} are all marked authorised; one approval authorises one batch"));

        Assert.DoesNotContain(AcquisitionSplitBatchTests.Partition, b => b.Authorised);

        Assert.Empty(authorised);
    }

    /// <summary>Every batch is a real batch, within the hard cap, naming a declaration.</summary>
    [Fact]
    public void Every_split_batch_is_within_the_cap_and_names_a_declaration()
    {
        Assert.All(AcquisitionSplitBatchTests.Partition, batch =>
        {
            Assert.InRange(batch.Count, 1, 70);
            Assert.True(batch.Index >= 1, Universe.Inv($"split batch index {batch.Index} is not a batch number"));
            Assert.False(string.IsNullOrWhiteSpace(batch.Declaration));
        });
    }

    /// <summary>
    /// The split runner names one source, and it is not a price, dividend or filing source.
    /// </summary>
    /// <remarks>
    /// A structural check rather than a promise. The runner builds every request from one factory
    /// that hard-codes this source and <c>CorporateActions</c>; if a second source constant ever
    /// appeared on the type, this fails and the "no price loop, no dividend loop, no SEC loop"
    /// guarantee stops being something a reader has to verify by eye.
    /// </remarks>
    [Fact]
    public void The_split_runner_names_only_the_corporate_actions_source()
    {
        var sources = SourceConstants(typeof(AcquisitionSplitBatchTests));

        Assert.Equal([SplitSourceValue], sources);
    }

    /// <summary>And the price runner is the mirror image, which is why there are two of them.</summary>
    [Fact]
    public void The_price_runner_names_only_the_price_source_it_dispatches()
    {
        var sources = SourceConstants(typeof(AcquisitionBatchTests));

        // The price runner also names the splits source, but only to report that it dispatched
        // none of them - it has no corporate-actions loop. What matters is that neither runner
        // names a dividend, benchmark or filing source at all.
        Assert.Contains(PriceSourceValue, sources);
        Assert.DoesNotContain(sources, s => s.Contains("dividend", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sources, s => s.Contains("sec", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Neither runner can name a benchmark instrument.</summary>
    [Fact]
    public void Neither_runner_names_a_benchmark()
    {
        foreach (var type in new[] { typeof(AcquisitionBatchTests), typeof(AcquisitionSplitBatchTests) })
        {
            var literals = StringConstants(type);

            Assert.DoesNotContain(literals, s => string.Equals(s, "SPY.US", StringComparison.Ordinal));
            Assert.DoesNotContain(literals, s => s.Contains("benchmark", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// The split runner has its own gate and its own index variable, and neither has a default.
    /// </summary>
    /// <remarks>
    /// Separate names matter: sharing the price runner's variables would mean one approval could
    /// start either, and an operator who set the wrong one would find out by spending money.
    /// </remarks>
    [Fact]
    public void The_split_runner_has_its_own_gate_and_index_variables()
    {
        var literals = StringConstants(typeof(AcquisitionSplitBatchTests));

        Assert.Contains("AIINV_SPLIT_BATCH", literals);
        Assert.Contains("AIINV_SPLIT_BATCH_INDEX", literals);
        Assert.DoesNotContain("AIINV_ACQUISITION_BATCH", literals);
        Assert.DoesNotContain("AIINV_BATCH_INDEX", literals);
    }

    /// <summary>The containment threshold and the hard cap are the price runner's, unchanged.</summary>
    [Fact]
    public void The_split_runner_keeps_the_same_containment_and_cap()
    {
        Assert.Equal(10, IntConstant(typeof(AcquisitionSplitBatchTests), "MaxConsecutiveFailures"));
        Assert.Equal(70, IntConstant(typeof(AcquisitionSplitBatchTests), "MaxBatchSize"));

        Assert.Equal(
            IntConstant(typeof(AcquisitionBatchTests), "MaxConsecutiveFailures"),
            IntConstant(typeof(AcquisitionSplitBatchTests), "MaxConsecutiveFailures"));
    }

    // ---- reading the shape ---------------------------------------------------------------------

    private static List<string> StringConstants(Type type) =>
        type.GetFields(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string?)f.GetRawConstantValue() ?? string.Empty)
            .ToList();

    /// <summary>Every constant on the type that looks like a provider source identifier.</summary>
    private static List<string> SourceConstants(Type type) =>
        StringConstants(type)
            .Where(v => v.StartsWith("eodhd-", StringComparison.Ordinal) ||
                v.StartsWith("sec-", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static int IntConstant(Type type, string name)
    {
        var field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);

        Assert.True(field is not null, Universe.Inv($"{type.Name} has no constant named {name}"));

        return (int)(field!.GetRawConstantValue() ?? 0);
    }
}
