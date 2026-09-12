using System.Net;
using System.Text;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Ingestion;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Investment.Integration.Tests.Ingestion;

/// <summary>
/// The kill switch and the policy engine, over a real filing-document acquisition.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The real <see cref="PolicyEngine"/>, not a stub.</strong> The claim under test is that
/// the engine refuses, and a permissive stub swapped for a refusing one would only prove the stub
/// refused. The engine here is the production type, evaluating the production
/// <c>ActionProposal</c> the gateway builds, with only the context's kill-switch and capability
/// state chosen by the test.
/// </para>
/// <para>
/// <strong>The kill switch outranks the authorisation.</strong> An authorisation says a request is
/// permitted to leave; the kill switch says nothing leaves. These prove the second wins, and that it
/// wins with zero HTTP calls - the refusal happens inside the seam, before the connector.
/// </para>
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class FilingDocumentSeamTests : IAsyncLifetime
{
    private const string Cik = "0000892482";
    private const string Accession = "0000897101-04-002197";
    private const string Document = "rimage044983_8k.htm";

    private readonly PostgresFixture _fixture;

    public FilingDocumentSeamTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>An engaged kill switch stops a fully admitted, fully capable request.</summary>
    /// <remarks>
    /// Everything else is open: the source admits the category, the connector declares it, the
    /// transport is primed with a valid document. The only thing standing in the way is the switch,
    /// so the refusal can have no other cause.
    /// </remarks>
    [SkippableFact]
    public async Task An_engaged_kill_switch_refuses_a_filing_document_with_no_request_made()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var harness = await SecHarness.CreateAsync(
            _fixture,
            admitFilingDocuments: true,
            policyEngine: new PolicyEngine(),
            policyContext: new ConfigurableContext(KillSwitchState.Engaged));

        harness.Handler.Respond(HttpStatusCode.OK, Encoding.UTF8.GetBytes("<html>doc</html>"), "text/html");

        var run = await harness.Fetcher.FetchAsync(Cik, Accession, Document);

        Assert.Equal(IngestionOutcome.Refused, run.Outcome);
        Assert.Equal(0, harness.Handler.Calls);
        Assert.Empty(await harness.ArchivedHashesAsync());

        // Recorded, and the reason names the switch rather than an absence.
        Assert.Equal(1, await harness.Context.IngestionRuns.CountAsync());
        Assert.Contains("kill switch", run.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An unknown kill-switch state is treated exactly like an engaged one.</summary>
    /// <remarks>
    /// A control that fails open is not a control. If the platform cannot determine whether it has
    /// been told to stop, it stops - and that has to hold for the newest acquisition path as much as
    /// for the oldest.
    /// </remarks>
    [SkippableFact]
    public async Task An_unknown_kill_switch_state_refuses_a_filing_document()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var harness = await SecHarness.CreateAsync(
            _fixture,
            admitFilingDocuments: true,
            policyEngine: new PolicyEngine(),
            policyContext: new ConfigurableContext(KillSwitchState.Unknown));

        harness.Handler.Respond(HttpStatusCode.OK, Encoding.UTF8.GetBytes("<html>doc</html>"), "text/html");

        var run = await harness.Fetcher.FetchAsync(Cik, Accession, Document);

        Assert.Equal(IngestionOutcome.Refused, run.Outcome);
        Assert.Equal(0, harness.Handler.Calls);
        Assert.Empty(await harness.ArchivedHashesAsync());
    }

    /// <summary>A disabled ingestion capability refuses, with the switch disengaged.</summary>
    /// <remarks>
    /// The other half of the policy gate. The kill switch is off here, so this proves the engine's
    /// capability check is reached and applied rather than short-circuited by the switch - two
    /// distinct refusals, not one mechanism tested twice.
    /// </remarks>
    [SkippableFact]
    public async Task A_disabled_ingestion_capability_refuses_a_filing_document()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var harness = await SecHarness.CreateAsync(
            _fixture,
            admitFilingDocuments: true,
            policyEngine: new PolicyEngine(),
            policyContext: new ConfigurableContext(KillSwitchState.Disengaged, ingestionEnabled: false));

        harness.Handler.Respond(HttpStatusCode.OK, Encoding.UTF8.GetBytes("<html>doc</html>"), "text/html");

        var run = await harness.Fetcher.FetchAsync(Cik, Accession, Document);

        Assert.Equal(IngestionOutcome.Refused, run.Outcome);
        Assert.Equal(0, harness.Handler.Calls);
        Assert.Empty(await harness.ArchivedHashesAsync());
    }

    /// <summary>
    /// With the switch disengaged and the capability enabled, the same request proceeds.
    /// </summary>
    /// <remarks>
    /// The control. Without it the three refusals above would be consistent with the real engine
    /// refusing everything, which would prove nothing about the kill switch in particular.
    /// </remarks>
    [SkippableFact]
    public async Task The_same_request_proceeds_when_policy_permits_it()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var bytes = Encoding.UTF8.GetBytes("<html>doc</html>");

        await using var harness = await SecHarness.CreateAsync(
            _fixture,
            admitFilingDocuments: true,
            policyEngine: new PolicyEngine(),
            policyContext: new ConfigurableContext(KillSwitchState.Disengaged));

        harness.Handler.Respond(HttpStatusCode.OK, bytes, "text/html");

        var run = await harness.Fetcher.FetchAsync(Cik, Accession, Document);

        Assert.Equal(IngestionOutcome.Succeeded, run.Outcome);
        Assert.Equal(1, harness.Handler.Calls);
        Assert.Equal(bytes, await harness.Archive.RetrieveAsync(run.Artifacts.Single()));
    }
}
