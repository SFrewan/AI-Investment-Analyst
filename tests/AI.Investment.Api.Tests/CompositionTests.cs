using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// That everything the host will be asked to build at runtime can actually be built.
/// </summary>
/// <remarks>
/// <para>
/// Written after <c>PortfolioController</c> shipped with an unregistered dependency. The
/// controller asked for <c>PortfolioReader</c>, nothing registered it, and both portfolio routes
/// answered 500 on every call. Nothing failed: the endpoint tests asserted the response was not
/// 401, not 403 and not 404, and a 500 is none of those.
/// </para>
/// <para>
/// <strong>Why a container test rather than a better endpoint assertion.</strong> A controller is
/// built by <c>ActivatorUtilities</c> resolving every constructor parameter from the request
/// scope, so an unregistered dependency fails <em>before</em> the action runs - and it fails as a
/// 500, indistinguishable over HTTP from the database being unreachable, which is the normal
/// state of the API test host. No assertion about a status code can separate the two. Asking the
/// container the same question the activator asks can, it needs no database, and it names both
/// the controller and the type it could not resolve.
/// </para>
/// <para>
/// These tests aggregate rather than stopping at the first failure. A composition defect is
/// usually one missing registration breaking several call sites, and seeing all of them at once
/// is the difference between one fix and four rounds of it.
/// </para>
/// </remarks>
public sealed class CompositionTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public CompositionTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// Every controller in the API can be constructed from the real container.
    /// </summary>
    /// <remarks>
    /// Constructed exactly the way ASP.NET Core constructs one, from a request scope, so a
    /// dependency registered with the wrong lifetime fails here too rather than at the first
    /// request in production.
    /// </remarks>
    [Fact]
    public void Every_controller_can_be_built_from_the_container()
    {
        var controllers = Controllers();

        Assert.NotEmpty(controllers);

        using var scope = _factory.Services.CreateScope();

        var failures = new List<string>();

        foreach (var controller in controllers)
        {
            try
            {
                var instance = ActivatorUtilities.CreateInstance(scope.ServiceProvider, controller);

                (instance as IDisposable)?.Dispose();
            }
            catch (InvalidOperationException error)
            {
                failures.Add(Describe(controller, error));
            }
        }

        Assert.True(
            failures.Count == 0,
            "One or more controllers cannot be built from the container, so every request to them "
            + "would answer 500 before reaching the action:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// Every constructor parameter of every controller resolves individually.
    /// </summary>
    /// <remarks>
    /// The same question asked one dependency at a time, so the report names the type that is
    /// missing rather than only the controller that wanted it. Kept separate from the test above
    /// because that one is the faithful reproduction of what the framework does, and this one is
    /// the diagnosis.
    /// </remarks>
    [Fact]
    public void Every_controller_dependency_is_registered()
    {
        using var scope = _factory.Services.CreateScope();

        var missing = new List<string>();

        foreach (var controller in Controllers())
        {
            foreach (var constructor in controller.GetConstructors())
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    if (parameter.HasDefaultValue)
                    {
                        continue;
                    }

                    if (scope.ServiceProvider.GetService(parameter.ParameterType) is not null)
                    {
                        continue;
                    }

                    missing.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"  {controller.Name} needs {parameter.ParameterType.Name} ({parameter.Name}), which is registered nowhere."));
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            "Unregistered controller dependencies:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, missing));
    }

    /// <summary>
    /// The regression this class exists for.
    /// </summary>
    /// <remarks>
    /// Named rather than discovered. The two tests above are the general defence; this one fails
    /// with the name of the thing that was actually broken, which is what a future reader needs
    /// when it breaks again.
    /// </remarks>
    [Fact]
    public void The_portfolio_read_model_is_registered()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.NotNull(
            scope.ServiceProvider.GetService(typeof(Application.Portfolio.PortfolioReader)));
    }

    /// <summary>
    /// Every hosted service the host registered can be resolved.
    /// </summary>
    /// <remarks>
    /// A hosted service that cannot be built stops the host from starting, so this would surface
    /// eventually - but it would surface as every API test failing at once with a message about
    /// the entry point, which is a much longer walk back to the missing registration than a test
    /// that names it. Resolving the collection is itself the assertion: an unbuildable one throws.
    /// </remarks>
    [Fact]
    public void Every_hosted_service_can_be_resolved()
    {
        var resolved = _factory.Services
            .GetServices<IHostedService>()
            .Select(service => service.GetType().Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(resolved);
    }

    private static List<Type> Controllers() =>
        typeof(Program).Assembly
            .GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract)
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The controller, and the innermost reason the container gave.
    /// </summary>
    /// <remarks>
    /// The activator wraps the real complaint in one or more outer exceptions, and the outer ones
    /// say only that the type could not be constructed. The innermost message is the one that
    /// names the service.
    /// </remarks>
    private static string Describe(Type controller, Exception error)
    {
        var innermost = error;

        while (innermost.InnerException is not null)
        {
            innermost = innermost.InnerException;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"  {controller.Name}: {innermost.Message}");
    }
}
