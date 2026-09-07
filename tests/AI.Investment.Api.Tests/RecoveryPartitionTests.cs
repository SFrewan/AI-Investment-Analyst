using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The rules the full recovery must not bend, pinned before it runs.
/// </summary>
/// <remarks>
/// The expected outcome of the recovery is not "every company recovered" - it is a truthful
/// partition. These facts hold the partition to being total, disjoint and honest about the
/// difference between a company that had no ticker and a request that never arrived.
/// </remarks>
public sealed class RecoveryPartitionTests
{
    [Fact]
    public void Only_a_recovered_identity_becomes_acquirable()
    {
        Assert.True(RecoveryPartition.IsAcquirable(RecoveryPartition.Recovered));

        // Nothing else does. A company with no listed security has nothing to price; the other
        // two are not identified at all.
        Assert.False(RecoveryPartition.IsAcquirable(RecoveryPartition.NoListedSecurity));
        Assert.False(RecoveryPartition.IsAcquirable(RecoveryPartition.Unresolved));
        Assert.False(RecoveryPartition.IsAcquirable(RecoveryPartition.TransportFailed));
    }

    /// <summary>
    /// A request that never arrived is not evidence, and must never be filed as an absence.
    /// </summary>
    [Fact]
    public void A_transport_failure_is_never_converted_into_a_statement_about_the_filing()
    {
        Assert.False(RecoveryPartition.IsEvidence(RecoveryPartition.TransportFailed));

        Assert.True(RecoveryPartition.IsEvidence(RecoveryPartition.Recovered));
        Assert.True(RecoveryPartition.IsEvidence(RecoveryPartition.NoListedSecurity));
        Assert.True(RecoveryPartition.IsEvidence(RecoveryPartition.Unresolved));

        // And the four are distinct strings, so no two can silently collapse into one bucket.
        Assert.Equal(4, RecoveryPartition.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_partition_is_total_and_nothing_is_counted_twice()
    {
        Assert.True(RecoveryPartition.Covers(131, 90, 12, 25, 4));

        // One member lost.
        Assert.False(RecoveryPartition.Covers(131, 90, 12, 25, 3));

        // One member counted twice.
        Assert.False(RecoveryPartition.Covers(131, 90, 12, 25, 5));

        // A recovery that recovered nothing still covers its membership.
        Assert.True(RecoveryPartition.Covers(131, 0, 0, 131, 0));
    }

    /// <summary>
    /// The arithmetic the whole authorisation rests on, checked against the floor that did not move.
    /// </summary>
    /// <remarks>
    /// The acquisition-ready panel today is 269 members carrying 5 dropouts - 1.9%, a gate 2
    /// failure. Every plausible recovery yield lifts it far clear of the five per cent floor, and
    /// the floor is referenced from <see cref="AcquisitionPlanning.SurvivorshipFloor"/> rather than
    /// restated so it cannot drift between the gate and this check.
    /// </remarks>
    [Fact]
    public void The_panel_as_it_stands_fails_gate_two_and_a_recovery_lifts_it_clear()
    {
        Assert.Equal(0.05m, AcquisitionPlanning.SurvivorshipFloor);

        // Today: 269 acquirable, 5 of them dropouts.
        Assert.False(RecoveryPartition.ClearsGate2(269, 5));
        Assert.InRange(RecoveryPartition.PanelDropoutShare(269, 5), 0.018m, 0.019m);

        // The pessimistic end of the probe interval, 48% of 107.
        Assert.True(RecoveryPartition.ClearsGate2(320, 56));
        Assert.InRange(RecoveryPartition.PanelDropoutShare(320, 56), 0.17m, 0.18m);

        // The point estimate, 70%.
        Assert.True(RecoveryPartition.ClearsGate2(344, 80));

        // And a recovery of nothing at all leaves it exactly where it is: failing.
        Assert.False(RecoveryPartition.ClearsGate2(269, 5));

        // An empty panel is reported as zero rather than dividing by zero.
        Assert.Equal(0m, RecoveryPartition.PanelDropoutShare(0, 0));
    }

    /// <summary>
    /// The sealed universe's own figure is untouched by any of this.
    /// </summary>
    [Fact]
    public void The_sealed_universe_keeps_its_own_survivorship_figure()
    {
        // 115 of 400 members stopped appearing before the window closed. Recovery changes which
        // members can be priced; it cannot change what the universe is.
        Assert.True(RecoveryPartition.ClearsGate2(400, 115));
        Assert.InRange(RecoveryPartition.PanelDropoutShare(400, 115), 0.287m, 0.288m);
    }
}
