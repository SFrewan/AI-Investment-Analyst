using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;

namespace AI.Investment.Integration.Tests;

/// <summary>
/// The authorising decision an integration test needs before the guarded context will accept a
/// domain write.
/// </summary>
/// <remarks>
/// Extracted rather than copied when a second test class needed it. Two hand-rolled versions of
/// "a decision that authorises a write" would drift, and a test-only drift in the shape of an
/// authorisation is precisely the kind that makes a passing suite meaningless.
/// </remarks>
internal static class SeamTestDecisions
{
    /// <summary>A proposal under the capability the registry is written beneath.</summary>
    internal static ActionProposal NewProposal(DateTime nowUtc) =>
        ActionProposal.Create(
            CorrelationId.New(),
            Capability.ReferenceDataManagement,
            ActionType.Create("test.action"),
            ActionTarget.Create("Test"),
            new TestParameters(),
            ActionEconomics.NoFinancialEffect(),
            ProposedBy.Service("integration-test", "1.0"),
            Guid.NewGuid().ToString("n"),
            nowUtc);

    /// <summary>A decision that permits it, so the write guard opens.</summary>
    internal static PolicyDecision ExecuteDecision(DateTime nowUtc) =>
        PolicyDecision.Execute(NewProposal(nowUtc), "permitted for the test", ["test@1"], nowUtc);

    private sealed record TestParameters : IActionParameters
    {
        public string Describe() => "integration test parameters";
    }
}
