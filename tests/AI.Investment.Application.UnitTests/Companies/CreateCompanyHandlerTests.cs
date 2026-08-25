using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Application.Companies.CreateCompany;
using AI.Investment.Application.UnitTests.Fakes;
using AI.Investment.Domain.Companies;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Application.UnitTests.Companies;

public sealed class CreateCompanyHandlerTests
{
    private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly InMemoryCompanyRepository _repository = new();
    private readonly CountingUnitOfWork _unitOfWork = new();

    private CreateCompanyHandler Handler(StubActionGateway gateway) =>
        new(_repository, _unitOfWork, gateway, new FixedCorrelationContext(), new FixedClock(Now));

    private static CreateCompanyCommand Command() =>
        new("Microsoft Corporation", "msft", "XNAS", "Technology", "Software", "US", null);

    [Fact]
    public async Task A_permitted_creation_persists_the_company()
    {
        var gateway = new StubActionGateway(ActionOutcomeStatus.Executed);

        var result = await Handler(gateway).HandleAsync(Command());

        Assert.Equal(CreateCompanyStatus.Created, result.Status);
        Assert.NotNull(result.Company);
        Assert.Equal("MSFT", result.Company!.Ticker);
        Assert.Single(_repository.Staged);
        Assert.Equal(1, _unitOfWork.SaveCount);
    }

    /// <summary>
    /// The point of the vertical slice: the write goes through the seam, not around it.
    /// </summary>
    [Fact]
    public async Task The_write_is_dispatched_through_the_action_gateway()
    {
        var gateway = new StubActionGateway(ActionOutcomeStatus.Executed);

        await Handler(gateway).HandleAsync(Command());

        var proposal = gateway.LastProposal;

        Assert.NotNull(proposal);
        Assert.Equal(Capability.ReferenceDataManagement, proposal!.Capability);
        Assert.Equal("company.create", proposal.ActionType.Value);
        Assert.Equal(ProposerKind.DeterministicService, proposal.ProposedBy.Kind);
        Assert.Equal(RiskTier.Low, proposal.RiskTier);
        Assert.True(proposal.Economics.HasNoFinancialEffect);
        Assert.Equal("company.create:MSFT", proposal.IdempotencyKey);
    }

    [Fact]
    public async Task A_denied_creation_writes_nothing()
    {
        var gateway = new StubActionGateway(ActionOutcomeStatus.Denied);

        var result = await Handler(gateway).HandleAsync(Command());

        Assert.Equal(CreateCompanyStatus.Denied, result.Status);
        Assert.Null(result.Company);
        Assert.Empty(_repository.Staged);
        Assert.Equal(0, _unitOfWork.SaveCount);
        Assert.Equal(0, gateway.EffectInvocations);
    }

    [Fact]
    public async Task An_approval_required_creation_writes_nothing()
    {
        var gateway = new StubActionGateway(ActionOutcomeStatus.ApprovalRequired);

        var result = await Handler(gateway).HandleAsync(Command());

        Assert.Equal(CreateCompanyStatus.ApprovalRequired, result.Status);
        Assert.Empty(_repository.Staged);
        Assert.Equal(0, _unitOfWork.SaveCount);
    }

    [Fact]
    public async Task A_suppressed_duplicate_writes_nothing()
    {
        var gateway = new StubActionGateway(ActionOutcomeStatus.DuplicateSuppressed);

        var result = await Handler(gateway).HandleAsync(Command());

        Assert.Equal(CreateCompanyStatus.DuplicateSuppressed, result.Status);
        Assert.Empty(_repository.Staged);
    }

    [Fact]
    public async Task An_existing_ticker_is_reported_without_reaching_the_gateway()
    {
        _repository.Companies.Add(
            Company.Create(CompanyId.New(), "Microsoft", Ticker.Create("MSFT"), Now));

        var gateway = new StubActionGateway(ActionOutcomeStatus.Executed);

        var result = await Handler(gateway).HandleAsync(Command());

        Assert.Equal(CreateCompanyStatus.AlreadyExists, result.Status);
        Assert.Null(gateway.LastProposal);
        Assert.Empty(_repository.Staged);
    }

    [Theory]
    [InlineData("", "MSFT")]
    [InlineData("   ", "MSFT")]
    [InlineData("Microsoft", "")]
    public async Task Missing_required_input_fails_validation(string name, string ticker)
    {
        var gateway = new StubActionGateway();

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            Handler(gateway).HandleAsync(new CreateCompanyCommand(name, ticker)));

        Assert.Null(gateway.LastProposal);
    }

    [Fact]
    public async Task A_malformed_ticker_is_rejected_by_the_domain()
    {
        var gateway = new StubActionGateway();

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            Handler(gateway).HandleAsync(new CreateCompanyCommand("Microsoft", "not a ticker")));

        Assert.Null(gateway.LastProposal);
    }
}
