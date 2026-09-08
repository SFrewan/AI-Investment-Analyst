using System.Globalization;
using System.Reflection;
using AI.Investment.Application;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Infrastructure;
using AI.Investment.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AI.Investment.Integration.Tests.Ingestion;

/// <summary>
/// Proves the per-run provenance record is actually wired into the composition the application
/// builds - not merely written.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IngestionGateway"/> takes its <see cref="IProviderExchangeStore"/> as an OPTIONAL
/// constructor parameter defaulting to null, so that a composition which never registered one keeps
/// working exactly as it did before. That default is the right behaviour and it is also the risk:
/// a container that failed to register the store would produce a gateway that records nothing,
/// silently, and every test using a hand-built gateway would still pass. The only thing that can
/// tell the difference is asking the real composition root for a real gateway and looking.
/// </para>
/// <para>
/// No database is contacted. The connection string below is well-formed and unreachable on purpose:
/// EF Core opens no connection when a context is merely constructed, and nothing here executes a
/// query. No provider is contacted either - both connectors are switched off in the configuration.
/// </para>
/// </remarks>
public sealed class ProvenanceCompositionTests
{
    /// <summary>Well-formed, and pointed at a port nothing listens on. It is never dialled.</summary>
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=composition_probe_tests;Username=none;Password=none";

    private const string ExchangeStoreField = "_exchangeStore";

    [Fact]
    public void The_composition_root_injects_the_provider_exchange_store_into_the_gateway()
    {
        var archiveRoot = TemporaryArchiveRoot();

        try
        {
            var services = Compose(archiveRoot);

            using var provider = services.BuildServiceProvider(validateScopes: true);
            using var scope = provider.CreateScope();

            var gateway = scope.ServiceProvider.GetRequiredService<IIngestionGateway>();

            var field = typeof(IngestionGateway).GetField(
                ExchangeStoreField,
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(field);

            var store = field!.GetValue(gateway);

            // The whole point. Null here means every future acquisition records no request
            // provenance and says nothing about it.
            Assert.NotNull(store);
            Assert.IsType<EfProviderExchangeStore>(store);
        }
        finally
        {
            Cleanup(archiveRoot);
        }
    }

    [Fact]
    public void The_infrastructure_registration_supplies_the_EF_backed_exchange_store()
    {
        var archiveRoot = TemporaryArchiveRoot();

        try
        {
            var descriptor = Assert.Single(
                Compose(archiveRoot),
                registration => registration.ServiceType == typeof(IProviderExchangeStore));

            Assert.Equal(typeof(EfProviderExchangeStore), descriptor.ImplementationType);
            Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        }
        finally
        {
            Cleanup(archiveRoot);
        }
    }

    /// <summary>
    /// The seam is optional by design, and this states that in a test rather than in a comment.
    /// </summary>
    /// <remarks>
    /// It is also the recorded limitation: absence is silent. Nothing throws, nothing warns, and
    /// the run succeeds having captured no evidence - which is why the test above exists to prove
    /// the real container does register one.
    /// </remarks>
    [Fact]
    public void The_exchange_store_parameter_is_optional_so_its_absence_can_never_block_acquisition()
    {
        var constructor = Assert.Single(typeof(IngestionGateway).GetConstructors());

        var parameter = Assert.Single(
            constructor.GetParameters(),
            candidate => candidate.ParameterType == typeof(IProviderExchangeStore));

        Assert.True(parameter.IsOptional);
        Assert.Null(parameter.DefaultValue);
    }

    // ServiceCollection, not IServiceCollection: CA1859 is enforced as an error here, and the
    // concrete type is what every caller in this file actually wants.
    private static ServiceCollection Compose(string archiveRoot)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Database:ConnectionString"] = UnreachableConnectionString,
                ["RawArchive:RootPath"] = archiveRoot,

                // No provider is reachable from this composition, so nothing here can spend a call
                // even by accident.
                ["Providers:Eodhd:Enabled"] = "false",
                ["Providers:SecEdgar:Enabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();

        services.AddLogging();

        // The generic host supplies this one; a bare ServiceCollection does not, and part of the
        // graph below IIngestionGateway asks for it. Standing one in is what makes this composition
        // equivalent to the API's, rather than a weaker version of it - everything else here is the
        // application's own registration code, unmodified.
        services.AddSingleton<IHostEnvironment>(new ProbeHostEnvironment(archiveRoot));

        services.AddApplication();
        services.AddInfrastructure(configuration);

        return services;
    }

    /// <summary>The host environment the generic host would otherwise provide.</summary>
    private sealed class ProbeHostEnvironment : IHostEnvironment
    {
        public ProbeHostEnvironment(string contentRoot)
        {
            ContentRootPath = contentRoot;
            ContentRootFileProvider = new NullFileProvider();
        }

        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "AI.Investment.CompositionProbe";

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; }
    }

    private static string TemporaryArchiveRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "aiinv-composition-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        Directory.CreateDirectory(path);

        return path;
    }

    private static void Cleanup(string archiveRoot)
    {
        try
        {
            if (Directory.Exists(archiveRoot))
            {
                Directory.Delete(archiveRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory that outlives the test is litter, not a failure.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
