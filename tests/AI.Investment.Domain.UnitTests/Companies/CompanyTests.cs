using AI.Investment.Domain.Companies;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Companies;

public sealed class CompanyTests
{
    private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    private static Company NewCompany() =>
        Company.Create(CompanyId.New(), "Microsoft Corporation", Ticker.Create("MSFT"), Now);

    [Fact]
    public void Create_trims_the_name()
    {
        var company = Company.Create(CompanyId.New(), "  Contoso  ", Ticker.Create("CTS"), Now);
        Assert.Equal("Contoso", company.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_company_cannot_exist_without_a_name(string? name) =>
        Assert.Throws<DomainValidationException>(() =>
            Company.Create(CompanyId.New(), name!, Ticker.Create("CTS"), Now));

    [Fact]
    public void A_name_longer_than_the_limit_is_rejected() =>
        Assert.Throws<DomainValidationException>(() =>
            Company.Create(CompanyId.New(), new string('x', Company.MaxNameLength + 1), Ticker.Create("CTS"), Now));

    [Fact]
    public void A_non_utc_creation_timestamp_is_rejected() =>
        Assert.Throws<DomainValidationException>(() =>
            Company.Create(CompanyId.New(), "Contoso", Ticker.Create("CTS"), DateTime.Now));

    [Fact]
    public void Creation_sets_both_timestamps_to_the_same_instant()
    {
        var company = NewCompany();

        Assert.Equal(Now, company.CreatedAtUtc);
        Assert.Equal(Now, company.UpdatedAtUtc);
    }

    [Fact]
    public void Renaming_updates_the_name_and_the_modification_timestamp()
    {
        var company = NewCompany();
        var later = Now.AddHours(1);

        company.Rename("Microsoft", later);

        Assert.Equal("Microsoft", company.Name);
        Assert.Equal(later, company.UpdatedAtUtc);
        Assert.Equal(Now, company.CreatedAtUtc);
    }

    [Fact]
    public void Renaming_to_an_invalid_name_is_rejected_and_leaves_the_company_unchanged()
    {
        var company = NewCompany();

        Assert.Throws<DomainValidationException>(() => company.Rename("  ", Now.AddHours(1)));
        Assert.Equal("Microsoft Corporation", company.Name);
        Assert.Equal(Now, company.UpdatedAtUtc);
    }

    [Fact]
    public void A_company_cannot_be_modified_before_it_was_created() =>
        Assert.Throws<DomainRuleViolationException>(() => NewCompany().Rename("Contoso", Now.AddHours(-1)));

    [Fact]
    public void Changing_the_listing_replaces_ticker_and_exchange()
    {
        var company = NewCompany();

        company.ChangeListing(Ticker.Create("MSFT"), Exchange.Create("XNAS"), Now.AddDays(1));

        Assert.Equal("MSFT", company.Ticker.Value);
        Assert.Equal("XNAS", company.Exchange!.Code);
    }

    [Fact]
    public void Updating_the_profile_normalises_blank_values_to_null()
    {
        var company = NewCompany();

        company.UpdateProfile("  Technology  ", "   ", null, "  A software company.  ", Now.AddDays(1));

        Assert.Equal("Technology", company.Sector);
        Assert.Null(company.Industry);
        Assert.Null(company.Country);
        Assert.Equal("A software company.", company.Description);
    }

    [Fact]
    public void An_over_long_description_is_rejected() =>
        Assert.Throws<DomainValidationException>(() =>
            NewCompany().UpdateProfile(null, null, null, new string('x', Company.MaxDescriptionLength + 1), Now));

    [Fact]
    public void Identity_is_by_id_not_by_contents()
    {
        var id = CompanyId.New();
        var first = Company.Create(id, "Contoso", Ticker.Create("CTS"), Now);
        var second = Company.Create(id, "Completely Different Name", Ticker.Create("XYZ"), Now);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Different_ids_are_different_companies() =>
        Assert.NotEqual(NewCompany(), NewCompany());

    [Fact]
    public void An_empty_company_id_is_rejected() =>
        Assert.Throws<DomainValidationException>(() => CompanyId.Create(Guid.Empty));

    /// <summary>
    /// Guards the invariant directly: if a public setter is ever reintroduced, the aggregate
    /// stops being able to protect itself and this test fails.
    /// </summary>
    [Fact]
    public void No_property_is_publicly_settable()
    {
        var settable = typeof(Company)
            .GetProperties()
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(settable);
    }
}
