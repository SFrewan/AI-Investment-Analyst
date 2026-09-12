using System.Reflection;
using AI.Investment.Domain.Companies;
using AI.Investment.Domain.Securities;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Investment.Architecture.Tests;

/// <summary>
/// The D3 boundary: legal identity belongs to the company, and the instrument never borrows it.
/// </summary>
/// <remarks>
/// The failure this guards against is quiet and attractive: a CIK on <c>Security</c> would make
/// symbol resolution a one-hop join and would be wrong the first time a company issued two share
/// classes. These rules make that impossible to add without something going red.
/// </remarks>
public sealed class CompanyIdentityRuleTests
{
    private static readonly Assembly DomainAssembly = typeof(Company).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(AppDbContext).Assembly;

    [Fact]
    public void Cik_exists_on_the_company_and_on_nothing_else_in_the_domain()
    {
        Assert.NotNull(typeof(Company).GetProperty(nameof(Company.Cik)));

        var offenders = DomainAssembly.GetTypes()
            .Where(t => t != typeof(Company) && t != typeof(Cik))
            .SelectMany(t => t.GetProperties().Select(p => (Type: t, Property: p)))
            .Where(x => x.Property.Name.Contains("Cik", StringComparison.OrdinalIgnoreCase)
                || x.Property.PropertyType == typeof(Cik))
            .Select(x => $"{x.Type.Name}.{x.Property.Name}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "One CIK can issue several securities, so a CIK anywhere but the company is ambiguous "
                + "exactly where it matters: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_security_has_no_cik_and_the_identifier_kinds_offer_none()
    {
        Assert.DoesNotContain(
            typeof(Security).GetProperties().Select(p => p.Name),
            n => n.Contains("Cik", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(
            Enum.GetNames<SecurityIdentifierKind>(),
            k => k.Contains("Cik", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_security_references_exactly_one_company_and_a_company_may_own_many()
    {
        var companyId = typeof(Security).GetProperty(nameof(Security.CompanyId));

        Assert.NotNull(companyId);
        Assert.Equal(typeof(CompanyId), companyId.PropertyType);
        Assert.False(companyId.SetMethod!.IsPublic);

        // The other direction is deliberately absent: no collection of securities hangs off Company,
        // so nothing can iterate a company's instruments as if that were its identity.
        Assert.DoesNotContain(
            typeof(Company).GetProperties().Select(p => p.PropertyType),
            t => t == typeof(Security) || (t.IsGenericType && t.GetGenericArguments().Contains(typeof(Security))));
    }

    /// <summary>
    /// The narrowing, as a standing rule: the legal entity carries nothing tradable.
    /// </summary>
    /// <remarks>
    /// This is anti-pattern 15 closed. <c>Company</c> carried a ticker and an exchange and rewrote
    /// them through <c>ChangeListing</c>, which made a symbol the de-facto key of a company and made
    /// every historical question unanswerable from production data. If any of the three reappears -
    /// including as a convenience for a lookup - this fails.
    /// </remarks>
    [Fact]
    public void The_company_aggregate_carries_no_tradable_identity()
    {
        var names = typeof(Company).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain("Ticker", names);
        Assert.DoesNotContain("Exchange", names);
        Assert.DoesNotContain("Symbol", names);
        Assert.DoesNotContain("Venue", names);

        Assert.Null(typeof(Company).GetMethod("ChangeListing"));

        // What it does own: legal identity.
        Assert.NotNull(typeof(Company).GetProperty(nameof(Company.Cik)));
        Assert.NotNull(typeof(Company).GetProperty(nameof(Company.Name)));
    }

    /// <summary>
    /// The companies table carries no tradable column and no ticker index either.
    /// </summary>
    [Fact]
    public void The_companies_table_carries_no_tradable_column()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=none")
            .Options;

        using var context = new AppDbContext(options, new StubWriteAuthorization());

        var company = context.Model.FindEntityType(typeof(Company))!;

        Assert.DoesNotContain(
            company.GetProperties().Select(p => p.GetColumnName()),
            c => c is "ticker" or "exchange");

        Assert.DoesNotContain(
            company.GetIndexes().Select(i => i.GetDatabaseName()),
            n => n == "ix_companies_ticker");
    }

    [Fact]
    public void Ticker_is_still_never_the_identity_of_a_security()
    {
        foreach (var type in new[] { typeof(Security), typeof(SecurityId) })
        {
            Assert.DoesNotContain(
                type.GetProperties().Select(p => p.Name),
                n => n.Contains("Ticker", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("Symbol", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// The company aggregate gained an identifier, not a history.
    /// </summary>
    /// <remarks>
    /// D3 decided Company stays current-state. A version column or a validity interval arriving here
    /// would be that decision reversed without a gate, so its absence is asserted.
    /// </remarks>
    public static readonly string[] ForbiddenTemporalNames =
        ["Version", "ValidFrom", "ValidTo", "EffectiveFrom", "EffectiveTo", "SupersededBy"];

    [Fact]
    public void The_company_aggregate_is_still_current_state()
    {
        var offenders = typeof(Company).GetProperties()
            .Where(p => ForbiddenTemporalNames.Any(f =>
                p.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Company versioning is a separate architectural decision with its own gate: "
                + string.Join(", ", offenders));

        Assert.Null(DomainAssembly.GetTypes()
            .FirstOrDefault(t => t.Name is "CompanyVersion" or "CompanyHistory" or "CompanyIdentityAssertion"));
    }

    /// <summary>
    /// The uniqueness is unique-WHEN-PRESENT, and the model says so.
    /// </summary>
    [Fact]
    public void The_cik_index_is_unique_only_where_a_cik_is_present()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=none")
            .Options;

        using var context = new AppDbContext(options, new StubWriteAuthorization());

        var company = context.Model.FindEntityType(typeof(Company))!;

        var cikIndex = company.GetIndexes()
            .Single(i => i.GetDatabaseName() == "ix_companies_cik");

        Assert.True(cikIndex.IsUnique);
        Assert.Equal("cik IS NOT NULL", cikIndex.GetFilter());

        // And the column itself stays nullable: a company outside SEC reporting has no CIK.
        Assert.True(company.FindProperty(nameof(Company.Cik))!.IsNullable);

        // The ticker index is gone with the ticker: D4 moved tradable identity to the security.
        Assert.DoesNotContain(
            company.GetIndexes().Select(i => i.GetDatabaseName()),
            n => n == "ix_companies_ticker");
    }

    /// <summary>
    /// Enough of the seam to build the model. It authorises nothing and never will.
    /// </summary>
    private sealed class StubWriteAuthorization : AI.Investment.Application.Abstractions.IWriteAuthorization
    {
        public bool IsAuthorized => false;

        public Guid? AuthorizingDecisionId => null;

        public IDisposable Authorize(AI.Investment.Domain.Actions.PolicyDecision decision) =>
            throw new NotSupportedException(
                "This context exists to read the EF model. Nothing is written through it.");
    }
}
