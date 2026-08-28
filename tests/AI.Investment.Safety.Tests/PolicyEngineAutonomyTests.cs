using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Autonomy;
using AI.Investment.Domain.Enums;
using Xunit;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// The two rules continuous operation added to the gate, and the property that binds them: autonomy
/// narrows an outcome and can never widen one.
/// </summary>
public sealed class PolicyEngineAutonomyTests
{
    private readonly PolicyEngine _engine = new();

    // ---- Rule 5: an unattended action must carry a resolved grant --------------------------

    /// <summary>
    /// The structural check that makes "a null resolution means attended" safe. Without it, work
    /// driven by a cycle could reach the gate with nothing attached and be treated as though a
    /// person were watching.
    /// </summary>
    [Fact]
    public void A_cycle_driven_proposal_with_no_resolved_grant_is_denied()
    {
        var decision = _engine.Evaluate(
            Phase6Fixtures.Unattended(),
            Phase6Fixtures.Context(autonomy: null),
            Phase6Fixtures.Now);

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
        Assert.Contains(PolicyEngine.AutonomyResolvedPolicy, decision.EvaluatedPolicies, StringComparer.Ordinal);
        Assert.Contains("no resolved autonomy grant", decision.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Not configurable. The most permissive capability policy imaginable does not switch it off.
    /// </summary>
    [Fact]
    public void The_structural_check_survives_the_most_permissive_configuration()
    {
        var permissive = PolicyContext.Create(
            Phase6Fixtures.Environment,
            KillSwitchState.Disengaged,
            [
                CapabilityPolicy.Create(
                    Capability.SimulatedExecution,
                    enabled: true,
                    RiskTier.Critical,
                    allowIrreversibleAutoExecute: true,
                    allowAiProposers: true),
            ]);

        var decision = _engine.Evaluate(Phase6Fixtures.Unattended(), permissive, Phase6Fixtures.Now);

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
    }

    [Fact]
    public void An_attended_proposal_needs_no_resolution_and_behaves_as_it_always_did()
    {
        var decision = _engine.Evaluate(
            Phase6Fixtures.Attended(),
            Phase6Fixtures.Context(autonomy: null),
            Phase6Fixtures.Now);

        Assert.Equal(PolicyOutcome.Execute, decision.Outcome);
        Assert.Contains(PolicyEngine.AutonomyResolvedPolicy, decision.EvaluatedPolicies, StringComparer.Ordinal);
    }

    // ---- Rule 10: the autonomy ceiling ------------------------------------------------------

    [Theory]
    [InlineData(AutonomyMode.AutoExecuteBounded, PolicyOutcome.Execute)]
    [InlineData(AutonomyMode.ContinuousBounded, PolicyOutcome.Execute)]
    [InlineData(AutonomyMode.PrepareForApproval, PolicyOutcome.RequireApproval)]
    [InlineData(AutonomyMode.Advise, PolicyOutcome.RequireApproval)]
    [InlineData(AutonomyMode.ResearchOnly, PolicyOutcome.RequireApproval)]
    public void The_resolved_mode_decides_whether_an_otherwise_permitted_action_executes(
        AutonomyMode mode,
        PolicyOutcome expected)
    {
        var decision = _engine.Evaluate(
            Phase6Fixtures.Unattended(),
            Phase6Fixtures.Context(Phase6Fixtures.Resolution(mode)),
            Phase6Fixtures.Now);

        Assert.Equal(expected, decision.Outcome);
        Assert.Contains(PolicyEngine.AutonomyCeilingPolicy, decision.EvaluatedPolicies, StringComparer.Ordinal);
    }

    /// <summary>
    /// "RequireApproval at minimum, Deny on the execution path." An approval queue must not be a way
    /// to obtain a permission nobody granted.
    /// </summary>
    [Fact]
    public void An_unresolved_grant_denies_on_the_execution_path_and_escalates_elsewhere()
    {
        var execution = _engine.Evaluate(
            Phase6Fixtures.Unattended(capability: Capability.SimulatedExecution),
            Phase6Fixtures.Context(Phase6Fixtures.Resolution(AutonomyMode.Off)),
            Phase6Fixtures.Now);

        Assert.Equal(PolicyOutcome.Deny, execution.Outcome);
        Assert.Contains("must not be used to obtain a permission", execution.Reason, StringComparison.Ordinal);

        var research = _engine.Evaluate(
            Phase6Fixtures.Unattended(
                capability: Capability.Analysis,
                actionType: "analysis.run"),
            Phase6Fixtures.Context(
                Phase6Fixtures.Resolution(AutonomyMode.Off, Capability.Analysis, "analysis.run")),
            Phase6Fixtures.Now);

        Assert.Equal(PolicyOutcome.RequireApproval, research.Outcome);
    }

    /// <summary>
    /// The property the whole design rests on: a resolution narrows and never widens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The baseline is the same action taken <em>attended</em> - same capability, same
    /// reversibility, same exposure, no cycle behind it - because that is what "the autonomy
    /// dimension does not apply" actually means. Comparing against the unattended proposal with no
    /// resolution would compare against a structural denial, which every mode beats trivially and
    /// which would therefore assert nothing.
    /// </para>
    /// <para>
    /// Stated this way the claim is the strong one: no grant, at any level, lets an unattended
    /// action do more than a person doing the same thing by hand would be permitted to do.
    /// </para>
    /// </remarks>
    [Fact]
    public void Autonomy_never_widens_an_outcome()
    {
        var modes = Enum.GetValues<AutonomyMode>();
        var reversibilities = Enum.GetValues<ReversibilityClass>();
        var capabilities = new[]
        {
            Capability.SimulatedExecution,
            Capability.Analysis,
            Capability.OpportunityManagement,
            Capability.PolicyAdministration,
        };

        var evaluated = 0;

        foreach (var capability in capabilities)
        {
            foreach (var reversibility in reversibilities)
            {
                var attended = Phase6Fixtures.Attended(
                    capability: capability,
                    actionType: "unattended.action",
                    reversibility: reversibility);

                var baseline = _engine
                    .Evaluate(attended, Phase6Fixtures.Context(autonomy: null), Phase6Fixtures.Now)
                    .Outcome;

                var unattended = Phase6Fixtures.Unattended(
                    capability: capability,
                    actionType: "unattended.action",
                    reversibility: reversibility);

                foreach (var mode in modes)
                {
                    // Built through the real resolver, so the modes exercised here are the ones it
                    // can actually produce rather than values a test invented.
                    var resolution = Phase6Fixtures.Resolution(mode);

                    var outcome = _engine
                        .Evaluate(unattended, Phase6Fixtures.Context(resolution), Phase6Fixtures.Now)
                        .Outcome;

                    Assert.True(
                        outcome <= baseline,
                        $"{capability}/{reversibility}/{mode} produced {outcome}, which is more " +
                        $"permissive than the {baseline} the same action gets when a person takes it.");

                    evaluated++;
                }
            }
        }

        Assert.Equal(capabilities.Length * reversibilities.Length * modes.Length, evaluated);
    }

    /// <summary>
    /// The kill switch outranks autonomy, as it outranks everything.
    /// </summary>
    [Theory]
    [InlineData(KillSwitchState.Engaged)]
    [InlineData(KillSwitchState.Unknown)]
    public void No_grant_survives_the_kill_switch(KillSwitchState state)
    {
        var decision = _engine.Evaluate(
            Phase6Fixtures.Unattended(),
            Phase6Fixtures.Context(Phase6Fixtures.Resolution(AutonomyMode.ContinuousBounded), state),
            Phase6Fixtures.Now);

        Assert.Equal(PolicyOutcome.Deny, decision.Outcome);
    }
}
