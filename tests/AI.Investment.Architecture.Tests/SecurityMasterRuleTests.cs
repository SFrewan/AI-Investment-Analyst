using System.Reflection;
using AI.Investment.Domain.Securities;
using AI.Investment.Infrastructure.Persistence;
using NetArchTest.Rules;
using Xunit;

namespace AI.Investment.Architecture.Tests;

/// <summary>
/// The boundaries of the D1 reference model, as assertions rather than intentions.
/// </summary>
/// <remarks>
/// <para>
/// The Security Master is the first new bounded context in a long while, and the failure modes are
/// known in advance: a ticker quietly becoming a key, a status escaping the venue that stated it, a
/// provider call arriving in the Domain, or a market-wide cessation appearing because someone needed
/// one. Each of those is a rule here.
/// </para>
/// <para>
/// The rules about what must NOT exist are as load-bearing as the rules about what must. A type that
/// cannot be evidenced is worse than a missing type, because a nullable field invites a default.
/// </para>
/// </remarks>
public sealed class SecurityMasterRuleTests
{
    private const string SecuritiesNamespace = "AI.Investment.Domain.Securities";

    private static readonly Assembly DomainAssembly = typeof(Security).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(AppDbContext).Assembly;
    private static readonly Assembly ApiAssembly = typeof(AI.Investment.Api.Program).Assembly;

    [Fact]
    public void The_security_master_lives_in_the_domain()
    {
        Assert.Equal(DomainAssembly, typeof(Security).Assembly);
        Assert.Equal(DomainAssembly, typeof(Venue).Assembly);
        Assert.Equal(DomainAssembly, typeof(Listing).Assembly);
        Assert.Equal(DomainAssembly, typeof(ListingEvent).Assembly);
        Assert.Equal(DomainAssembly, typeof(ListingStatus).Assembly);
        Assert.Equal(DomainAssembly, typeof(SecurityId).Assembly);

        foreach (var type in new[]
        {
            typeof(Security), typeof(Venue), typeof(Listing), typeof(ListingEvent),
        })
        {
            Assert.Equal(SecuritiesNamespace, type.Namespace);
        }
    }

    /// <summary>
    /// The layering rule, restated for this namespace specifically.
    /// </summary>
    /// <remarks>
    /// The solution-wide version already covers it, and this is not redundant: the solution-wide
    /// rule would still pass if a future Security Master type reached outward, so long as some other
    /// namespace had not. Naming the context makes the failure message say which one broke.
    /// </remarks>
    [Fact]
    public void The_security_master_does_not_reach_the_application_infrastructure_or_api()
    {
        var result = Types.InAssembly(DomainAssembly)
            .That().ResideInNamespace(SecuritiesNamespace)
            .ShouldNot()
            .HaveDependencyOnAny(
                "AI.Investment.Application",
                "AI.Investment.Infrastructure",
                "AI.Investment.Api")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            "Security Master types depending outward: "
                + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void The_security_master_reaches_no_provider_no_network_and_no_database()
    {
        var result = Types.InAssembly(DomainAssembly)
            .That().ResideInNamespace(SecuritiesNamespace)
            .ShouldNot()
            .HaveDependencyOnAny(
                "System.Net",
                "System.Net.Http",
                "Npgsql",
                "Microsoft.EntityFrameworkCore",
                "Microsoft.Extensions.Logging")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            "Security Master types reaching a connector, a socket or a store: "
                + string.Join(", ", result.FailingTypeNames ?? []));
    }

    /// <summary>
    /// No EODHD, no SEC, and nothing that names them.
    /// </summary>
    /// <remarks>
    /// Checked by name rather than by dependency because the connectors live in Infrastructure and
    /// the dependency rule above already forbids that assembly. What this adds is the case where
    /// somebody writes a vendor's rules into the Domain by hand - an endpoint string, a symbol
    /// suffix convention - which would import the vendor without importing its code.
    /// </remarks>
    [Fact]
    public void The_security_master_names_no_vendor()
    {
        var offenders = DomainAssembly.GetTypes()
            .Where(t => t.Namespace == SecuritiesNamespace)
            .Where(t => t.Name.Contains("Eodhd", StringComparison.OrdinalIgnoreCase)
                || t.Name.Contains("SecEdgar", StringComparison.OrdinalIgnoreCase)
                || t.Name.Contains("Edgar", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Name)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "The reference model must not be shaped by one vendor: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_security_master_holds_no_policy_no_execution_and_no_ai()
    {
        var result = Types.InAssembly(DomainAssembly)
            .That().ResideInNamespace(SecuritiesNamespace)
            .ShouldNot()
            .HaveDependencyOnAny(
                "AI.Investment.Domain.Actions",
                "AI.Investment.Domain.Approvals",
                "AI.Investment.Domain.Autonomy",
                "AI.Investment.Domain.Limits",
                "AI.Investment.Domain.Ai",
                "AI.Investment.Domain.Shadow",
                "AI.Investment.Domain.Portfolio")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            "Reference data must not decide, execute or infer anything: "
                + string.Join(", ", result.FailingTypeNames ?? []));
    }

    /// <summary>
    /// The invariant the whole context is built around: a ticker is never a key.
    /// </summary>
    [Fact]
    public void A_ticker_is_never_the_identity_of_a_security()
    {
        var identityTypes = new[] { typeof(Security), typeof(SecurityId), typeof(Listing) };

        foreach (var type in identityTypes)
        {
            var offending = type.GetProperties()
                .Where(p => p.Name.Contains("Ticker", StringComparison.OrdinalIgnoreCase)
                    || p.Name.Contains("Symbol", StringComparison.OrdinalIgnoreCase))
                .Select(p => $"{type.Name}.{p.Name}")
                .ToList();

            Assert.True(
                offending.Count == 0,
                "A symbol on an identity type becomes a key within a release: "
                    + string.Join(", ", offending));
        }

        // Identity is a surrogate, and the Domain's Ticker value object is not reachable from it.
        Assert.Equal(typeof(Guid), typeof(SecurityId).GetProperty(nameof(SecurityId.Value))!.PropertyType);
    }

    /// <summary>
    /// Every listing statement is bound to the venue that made it.
    /// </summary>
    [Fact]
    public void A_listing_statement_cannot_exist_without_a_venue()
    {
        Assert.NotNull(typeof(ListingEvent).GetProperty(nameof(ListingEvent.VenueId)));
        Assert.NotNull(typeof(Listing).GetProperty(nameof(Listing.VenueId)));

        // No method on the listing types reports a status without being asked about one venue,
        // because there is only one listing per pairing and nothing aggregates across them.
        var crossVenue = typeof(Listing).GetMethods()
            .Where(m => m.DeclaringType == typeof(Listing))
            .Where(m => m.Name.Contains("Market", StringComparison.OrdinalIgnoreCase)
                || m.Name.Contains("AnyVenue", StringComparison.OrdinalIgnoreCase)
                || m.Name.Contains("AllVenues", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Name)
            .ToList();

        Assert.True(
            crossVenue.Count == 0,
            "A statement about one venue is never evidence about all venues: "
                + string.Join(", ", crossVenue));
    }

    /// <summary>
    /// The types that must stay absent until their evidence exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>TradingCessation</c> needs a venue-complete source, which nothing held or authorised can
    /// provide. These are easy to add badly, and a nullable field would invite a default, so their
    /// absence is asserted rather than remembered.
    /// </para>
    /// <para>
    /// <strong><c>UniverseMembership</c> was on this list and has been removed, deliberately.</strong>
    /// It was forbidden because the identity decision behind it had not been taken; the D2 gate took
    /// it, and stage D authorises the type. This test failing was the mechanism working - the
    /// decision had to be made explicitly rather than arriving with the code. <c>UniverseVersion</c>
    /// stays forbidden, because that same decision concluded a sealed version <em>is</em> a
    /// <c>Universe</c> row and a separate version type would be a second identity for one thing.
    /// </para>
    /// <para>
    /// <strong><c>Determination</c> has been removed for the same reason, at stage E.</strong> It
    /// was forbidden while tier 4 of the evidence spine had no persistence path; the stage that
    /// builds that path authorises the type, and this test failing was again the mechanism working.
    /// The remaining three stay: each needs evidence that nothing held or authorised can supply.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_concepts_without_evidence_stay_absent_from_production()
    {
        string[] forbidden =
        [
            "TradingCessation", "TradingSuspension", "VenueCompleteness", "UniverseVersion",
        ];

        var present = new[] { DomainAssembly, InfrastructureAssembly, ApiAssembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => forbidden.Any(f => string.Equals(t.Name, f, StringComparison.Ordinal)))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        Assert.True(
            present.Count == 0,
            "These types must not exist until the evidence that types them does: "
                + string.Join(", ", present));
    }

    /// <summary>
    /// No boolean or nullable date could stand in for a cessation the model refuses to express.
    /// </summary>
    [Fact]
    public void No_delisting_shortcut_exists_on_any_reference_type()
    {
        var offenders = new List<string>();

        foreach (var type in DomainAssembly.GetTypes().Where(t => t.Namespace == SecuritiesNamespace))
        {
            foreach (var property in type.GetProperties())
            {
                var name = property.Name;

                if (name.Contains("IsDelisted", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("CessationDate", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("DelistedOn", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("LastPrice", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{type.Name}.{name}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A shortcut that a default could fill is how an unevidenced claim gets made: "
                + string.Join(", ", offenders));
    }

    /// <summary>
    /// The application and infrastructure layers may consume the reference model.
    /// </summary>
    /// <remarks>
    /// The other direction of the layering rule, asserted so that "Domain depends on nothing" is not
    /// mistaken for "nothing depends on Domain". Infrastructure maps these types, and that is the
    /// arrangement working rather than a violation.
    /// </remarks>
    [Fact]
    public void Infrastructure_may_consume_the_reference_model()
    {
        var configured = InfrastructureAssembly.GetTypes()
            .Where(t => t.Name.EndsWith("Configuration", StringComparison.Ordinal))
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType
                && i.GetGenericArguments().Any(a => a.Namespace == SecuritiesNamespace)))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ["ListingConfiguration", "ListingEventConfiguration", "SecurityConfiguration", "VenueConfiguration"],
            configured);
    }
}
