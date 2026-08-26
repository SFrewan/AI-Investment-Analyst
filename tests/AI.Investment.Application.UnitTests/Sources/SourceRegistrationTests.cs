using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Application.Sources.ActivateSource;
using AI.Investment.Application.Sources.RegisterKnownSources;
using AI.Investment.Application.UnitTests.Fakes;
using AI.Investment.Application.UnitTests.Ingestion;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Sources;
using Xunit;

namespace AI.Investment.Application.UnitTests.Sources;

/// <summary>
/// Seeding the registry from the definitions an installation ships.
/// </summary>
/// <remarks>
/// Without this the registry starts empty and every ingestion run refuses - correctly, but
/// uselessly. The claims worth asserting are that seeding registers sources <em>inactive</em>, that
/// it leaves existing entries alone, and that a refusal of one source does not take the others
/// with it.
/// </remarks>
public sealed class RegisterKnownSourcesHandlerTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    private sealed class StubDefinition : ISourceDefinition
    {
        private readonly LicensingTerms _licensing;

        public StubDefinition(string id, LicensingTerms? licensing = null)
        {
            SourceId = SourceId.Create(id);
            _licensing = licensing ?? LicensingTerms.OpenData();
        }

        public SourceId SourceId { get; }

        public DataSource Definition(DateTime nowUtc) =>
            DataSource.Register(
                SourceId,
                $"Source {SourceId}",
                SourceType.RegulatoryAuthority,
                SourceAuthority.Primary,
                Region.UnitedStates,
                [DataCategory.RegulatoryFilings],
                UpdateCadence.EventDriven,
                _licensing,
                VerificationPolicy.Authoritative,
                nowUtc);
    }

    private sealed record Harness(
        RegisterKnownSourcesHandler Handler,
        InMemorySourceRegistry Registry,
        CountingUnitOfWork UnitOfWork,
        StubActionGateway Actions);

    private static Harness Build(
        IEnumerable<ISourceDefinition> definitions,
        ActionOutcomeStatus policy = ActionOutcomeStatus.Executed,
        DataSource? alreadyRegistered = null)
    {
        var registry = new InMemorySourceRegistry();

        if (alreadyRegistered is not null)
        {
            registry.Add(alreadyRegistered);
        }

        var unitOfWork = new CountingUnitOfWork();
        var actions = new StubActionGateway(policy);

        return new Harness(
            new RegisterKnownSourcesHandler(definitions, registry, unitOfWork, actions, new FixedClock(Now)),
            registry,
            unitOfWork,
            actions);
    }

    /// <summary>Shipping a connector is not deciding to use it.</summary>
    [Fact]
    public async Task A_seeded_source_is_registered_inactive()
    {
        var harness = Build([new StubDefinition("sec-edgar")]);

        var results = await harness.Handler.HandleAsync();

        Assert.Single(results);
        Assert.Equal(SourceRegistrationOutcome.Registered, results[0].Outcome);

        var stored = await harness.Registry.GetByIdAsync(SourceId.Create("sec-edgar"));
        Assert.NotNull(stored);
        Assert.False(stored!.IsActive);
    }

    /// <summary>
    /// An existing entry may have been re-licensed or deactivated by an operator. Overwriting it
    /// on every start-up would quietly undo that.
    /// </summary>
    [Fact]
    public async Task An_existing_source_is_left_exactly_as_it_is()
    {
        var existing = new StubDefinition("sec-edgar").Definition(Now.AddYears(-1));
        existing.Activate(Now.AddYears(-1));

        var harness = Build([new StubDefinition("sec-edgar")], alreadyRegistered: existing);

        var results = await harness.Handler.HandleAsync();

        Assert.Equal(SourceRegistrationOutcome.AlreadyRegistered, results[0].Outcome);
        Assert.Equal(0, harness.UnitOfWork.SaveCount);

        var stored = await harness.Registry.GetByIdAsync(SourceId.Create("sec-edgar"));
        Assert.True(stored!.IsActive);
    }

    /// <summary>
    /// One proposal per source, so a refusal of one does not silently take the others with it -
    /// and so the audit trail records which source was admitted, not that "seeding ran".
    /// </summary>
    [Fact]
    public async Task Each_source_is_proposed_separately()
    {
        var harness = Build([new StubDefinition("source-a"), new StubDefinition("source-b")]);

        var results = await harness.Handler.HandleAsync();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(SourceRegistrationOutcome.Registered, r.Outcome));
        Assert.Equal(2, harness.Actions.EffectInvocations);
    }

    [Fact]
    public async Task A_policy_denial_leaves_the_registry_untouched()
    {
        var harness = Build([new StubDefinition("sec-edgar")], ActionOutcomeStatus.Denied);

        var results = await harness.Handler.HandleAsync();

        Assert.Equal(SourceRegistrationOutcome.Refused, results[0].Outcome);
        Assert.Null(await harness.Registry.GetByIdAsync(SourceId.Create("sec-edgar")));
    }

    [Fact]
    public async Task Registration_is_proposed_under_reference_data_management()
    {
        var harness = Build([new StubDefinition("sec-edgar")]);

        await harness.Handler.HandleAsync();

        var proposal = harness.Actions.LastProposal;

        Assert.NotNull(proposal);
        Assert.Equal(Capability.ReferenceDataManagement, proposal!.Capability);
        Assert.Equal("source.register:sec-edgar", proposal.IdempotencyKey);
    }

    [Fact]
    public async Task Nothing_to_seed_is_not_an_error() =>
        Assert.Empty(await Build([]).Handler.HandleAsync());
}

public sealed class ActivateSourceHandlerTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly SourceId Id = SourceId.Create("sec-edgar");

    private static DataSource Registered(LicensingTerms? licensing = null) =>
        DataSource.Register(
            Id,
            "SEC EDGAR",
            SourceType.RegulatoryAuthority,
            SourceAuthority.Primary,
            Region.UnitedStates,
            [DataCategory.RegulatoryFilings],
            UpdateCadence.EventDriven,
            licensing ?? LicensingTerms.OpenData(),
            VerificationPolicy.Authoritative,
            Now.AddDays(-1));

    private static ActivateSourceHandler Build(
        DataSource? source,
        out InMemorySourceRegistry registry,
        ActionOutcomeStatus policy = ActionOutcomeStatus.Executed)
    {
        registry = new InMemorySourceRegistry();

        if (source is not null)
        {
            registry.Add(source);
        }

        return new ActivateSourceHandler(
            registry,
            new CountingUnitOfWork(),
            new StubActionGateway(policy),
            new FixedClock(Now));
    }

    [Fact]
    public async Task A_registered_source_can_be_activated()
    {
        var handler = Build(Registered(), out var registry);

        var result = await handler.HandleAsync(Id);

        Assert.Equal(ActivateSourceStatus.Activated, result.Status);
        Assert.True((await registry.GetByIdAsync(Id))!.IsActive);
    }

    [Fact]
    public async Task An_unregistered_source_cannot_be_activated()
    {
        var handler = Build(source: null, out _);

        Assert.Equal(ActivateSourceStatus.NotFound, (await handler.HandleAsync(Id)).Status);
    }

    [Fact]
    public async Task Activating_an_active_source_is_a_no_op()
    {
        var source = Registered();
        source.Activate(Now);

        var handler = Build(source, out _);

        Assert.Equal(ActivateSourceStatus.AlreadyActive, (await handler.HandleAsync(Id)).Status);
    }

    /// <summary>
    /// The domain refuses, not the handler - so it would still refuse if some future caller
    /// bypassed this path entirely.
    /// </summary>
    [Fact]
    public async Task A_source_whose_licence_permits_nothing_cannot_be_activated()
    {
        var handler = Build(Registered(LicensingTerms.Unknown), out _);

        await Assert.ThrowsAsync<Domain.Exceptions.DomainRuleViolationException>(
            () => handler.HandleAsync(Id));
    }

    [Fact]
    public async Task A_policy_denial_leaves_the_source_inactive()
    {
        var handler = Build(Registered(), out var registry, ActionOutcomeStatus.Denied);

        var result = await handler.HandleAsync(Id);

        Assert.Equal(ActivateSourceStatus.Denied, result.Status);
        Assert.False((await registry.GetByIdAsync(Id))!.IsActive);
    }
}
