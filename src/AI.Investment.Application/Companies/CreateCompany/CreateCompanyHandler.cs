using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Actions;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Companies;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.ValueObjects;

namespace AI.Investment.Application.Companies.CreateCompany;

/// <summary>
/// Creates a company - routed deliberately through the Action/Policy seam.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Creating a company record is not dangerous, and that is the point.</strong> This
/// handler exists in Phase 1 to prove the safety architecture is real rather than theoretical,
/// on a use case where getting it wrong costs nothing. A seam introduced later, when the first
/// genuinely risky feature arrives, is a seam introduced under schedule pressure and retrofitted
/// to call sites that already exist.
/// </para>
/// <para>
/// Note the shape: the handler builds a proposal and hands the gateway a delegate. It never
/// decides whether it may proceed, and it cannot invoke its own effect. If the policy engine
/// says no, the delegate is simply never called - there is no branch in this file that could
/// get that wrong.
/// </para>
/// </remarks>
public sealed class CreateCompanyHandler
{
    /// <summary>Identifies this handler as the proposer in the audit trail.</summary>
    public const string ServiceId = "application.companies.create-company";

    public const string ServiceVersion = "1.0";

    private readonly ICompanyRepository _companies;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IActionGateway _gateway;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;

    public CreateCompanyHandler(
        ICompanyRepository companies,
        IUnitOfWork unitOfWork,
        IActionGateway gateway,
        ICorrelationContext correlation,
        IClock clock)
    {
        _companies = companies ?? throw new ArgumentNullException(nameof(companies));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<CreateCompanyResult> HandleAsync(
        CreateCompanyCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = CreateCompanyValidator.Validate(command);

        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }

        // The duplicate pre-check went with the ticker it was keyed on. Names are not unique and
        // never were, so re-keying that check onto one would assert something false. Idempotency
        // below is what actually stops a retried request creating a second row.

        var now = _clock.UtcNow;
        var companyId = CompanyId.New();

        var proposal = ActionProposal.Create(
            _correlation.Current,
            Capability.ReferenceDataManagement,
            ActionType.Create("company.create"),
            ActionTarget.Create("Company"),
            new CreateCompanyParameters(command.Name),

            // Adding a reference-data row spends nothing, risks nothing and can be undone.
            // Risk tier is computed from this by RiskTierCalculator - it is not asserted here,
            // and there is no parameter through which it could be.
            ActionEconomics.NoFinancialEffect(),

            ProposedBy.Service(ServiceId, ServiceVersion),

            // Keyed on the name now that the ticker has moved to the security. The property that
            // matters is unchanged - a retried request does not create a second row - and the
            // Phase 1 consequence is unchanged with it: a name created once cannot be created
            // again through this path, and a key scoped to a retry window is the later refinement.
            idempotencyKey: $"company.create:{command.Name.Trim()}",
            now);

        var outcome = await _gateway.DispatchAsync(
            proposal,
            async token =>
            {
                var company = Domain.Companies.Company.Create(
                    companyId,
                    command.Name,
                    now,
                    command.Sector,
                    command.Industry,
                    command.Country,
                    command.Description);

                _companies.Add(company);

                // Refused by the persistence layer unless the gateway has opened an
                // authorisation window - which it only does after a decision permitting
                // execution.
                await _unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);

                return company;
            },
            cancellationToken).ConfigureAwait(false);

        return outcome.Status switch
        {
            ActionOutcomeStatus.Executed => new CreateCompanyResult(
                CreateCompanyStatus.Created,
                outcome.Result!.ToDto(),
                outcome.Reason,
                outcome.Decision.DecisionId),

            ActionOutcomeStatus.ApprovalRequired => new CreateCompanyResult(
                CreateCompanyStatus.ApprovalRequired,
                Company: null,
                outcome.Reason,
                outcome.Decision.DecisionId),

            ActionOutcomeStatus.DuplicateSuppressed => new CreateCompanyResult(
                CreateCompanyStatus.DuplicateSuppressed,
                Company: null,
                outcome.Reason,
                outcome.Decision.DecisionId),

            // Denied, and any future status: refuse. Fail closed, including in a switch.
            _ => new CreateCompanyResult(
                CreateCompanyStatus.Denied,
                Company: null,
                outcome.Reason,
                outcome.Decision.DecisionId),
        };
    }
}
