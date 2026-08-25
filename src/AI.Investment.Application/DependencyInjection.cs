using AI.Investment.Application.Actions;
using AI.Investment.Application.Companies.CreateCompany;
using AI.Investment.Application.Companies.GetCompany;
using AI.Investment.Application.Companies.SearchCompanies;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.Retention;
using AI.Investment.Domain.Actions;
using Microsoft.Extensions.DependencyInjection;

namespace AI.Investment.Application;

/// <summary>Registers the application layer's services.</summary>
/// <remarks>
/// Handlers are registered explicitly rather than by assembly scanning. Scanning is convenient
/// and hides what is registered; at this size an explicit list is shorter than the scanner
/// configuration would be, and a missing registration fails at start-up rather than at the
/// first request.
/// </remarks>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The policy engine is pure and holds no state, so a singleton is correct.
        services.AddSingleton<IPolicyEngine, PolicyEngine>();

        // The gateway is scoped: it participates in the same unit of work as the request.
        services.AddScoped<IActionGateway, ActionGateway>();

        // Scoped like the action gateway they build on: each participates in one unit of work.
        services.AddScoped<IIngestionGateway, IngestionGateway>();

        // Retention deletes evidence, so it is proposed under its own capability and denied by
        // default - a capability with no configured policy never executes.
        services.AddScoped<IRetentionEnforcer, RetentionEnforcer>();

        services.AddScoped<CreateCompanyHandler>();
        services.AddScoped<GetCompanyHandler>();
        services.AddScoped<SearchCompaniesHandler>();

        return services;
    }
}
