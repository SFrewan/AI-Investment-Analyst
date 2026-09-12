using AI.Investment.Domain.Companies;
using AI.Investment.Domain.Exceptions;
using AI.Investment.Domain.Securities;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.Companies;

/// <summary>
/// The D3 bridge: a company may carry the SEC filer identity, and a security never does.
/// </summary>
/// <remarks>
/// The decision this encodes is that <c>Company</c> stays current-state and CIK is nullable and
/// unique when present. What these tests protect is the narrow part: that an invalid CIK cannot
/// enter the aggregate at all, that the zero padding survives, and that nothing quietly moved legal
/// identity onto the instrument.
/// </remarks>
public sealed class CompanyCikTests
{
    private static readonly DateTime Now = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- the value object

    [Fact]
    public void A_ten_digit_cik_is_accepted_and_kept_exactly()
    {
        var cik = Cik.Create("0000320193");

        Assert.Equal("0000320193", cik.Value);
        Assert.Equal("0000320193", cik.ToString());
    }

    [Fact]
    public void Leading_zeroes_are_significant_and_survive()
    {
        // EDGAR writes CIK 320193 as 0000320193, and a path that drops the padding resolves to
        // nothing. Storing it as a number would lose exactly this.
        var cik = Cik.Create("0000000320");

        Assert.Equal("0000000320", cik.Value);
        Assert.StartsWith("0000000", cik.Value, StringComparison.Ordinal);
        Assert.Equal(Cik.Digits, cik.Value.Length);
    }

    [Theory]
    [InlineData("320193")]          // unpadded - the provider normaliser's job, not this type's
    [InlineData("00000320193")]     // eleven digits
    [InlineData("000032019")]       // nine digits
    [InlineData("")]
    [InlineData("   ")]
    public void A_cik_that_is_not_exactly_ten_digits_is_refused(string candidate)
    {
        Assert.Throws<DomainValidationException>(() => Cik.Create(candidate));
    }

    [Theory]
    [InlineData("CIK0000320")]
    [InlineData("000032019a")]
    [InlineData("00003201 3")]
    [InlineData("0000-32019")]
    public void A_cik_containing_anything_but_digits_is_refused(string candidate)
    {
        Assert.Throws<DomainValidationException>(() => Cik.Create(candidate));
    }

    [Fact]
    public void The_all_zero_value_is_not_a_cik()
    {
        var thrown = Assert.Throws<DomainValidationException>(() => Cik.Create("0000000000"));

        Assert.Contains("not a CIK", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed_because_it_is_a_transport_artefact()
    {
        Assert.Equal("0000320193", Cik.Create("  0000320193  ").Value);
    }

    [Fact]
    public void Try_create_reports_failure_without_throwing()
    {
        Assert.True(Cik.TryCreate("0000320193", out var good));
        Assert.Equal("0000320193", good.Value);

        Assert.False(Cik.TryCreate("320193", out var bad));
        Assert.Null(bad);

        Assert.False(Cik.TryCreate(null, out _));
    }

    [Fact]
    public void Two_ciks_with_the_same_digits_are_the_same_value()
    {
        Assert.Equal(Cik.Create("0000320193"), Cik.Create("0000320193"));
        Assert.NotEqual(Cik.Create("0000320193"), Cik.Create("0000789019"));
    }

    // ---------------------------------------------------------------- on the aggregate

    [Fact]
    public void A_company_without_a_cik_is_ordinary_and_not_incomplete()
    {
        var company = Company.Create(CompanyId.New(), "Contoso", Now);

        Assert.Null(company.Cik);
    }

    [Fact]
    public void A_company_may_carry_a_cik_when_one_is_known()
    {
        var company = Company.Create(
            CompanyId.New(), "Apple Inc.", Now, cik: Cik.Create("0000320193"));

        Assert.Equal("0000320193", company.Cik!.Value);
    }

    [Fact]
    public void An_invalid_cik_cannot_reach_the_aggregate_because_it_cannot_be_constructed()
    {
        // There is no overload taking a string, so the only way in is through the value object,
        // and the value object refuses. The aggregate needs no validation of its own.
        Assert.Throws<DomainValidationException>(() => Company.Create(
            CompanyId.New(), "Contoso", Now, cik: Cik.Create("nonsense")));

        Assert.DoesNotContain(
            typeof(Company).GetMethods().Where(m => m.DeclaringType == typeof(Company)),
            m => m.GetParameters().Any(p => p.ParameterType == typeof(string)
                && p.Name is not null
                && p.Name.Contains("cik", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void No_mutation_path_can_set_or_invalidate_a_cik()
    {
        var mutators = typeof(Company).GetMethods()
            .Where(m => m.DeclaringType == typeof(Company))
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(Cik)
                || p.ParameterType == typeof(Cik)))
            .Select(m => m.Name)
            .Where(n => n != nameof(Company.Create))
            .ToList();

        Assert.Empty(mutators);

        Assert.False(typeof(Company).GetProperty(nameof(Company.Cik))!.SetMethod!.IsPublic);
    }

    // ---------------------------------------------------------------- the boundary

    [Fact]
    public void Cik_lives_on_the_company_and_nowhere_near_the_security()
    {
        Assert.NotNull(typeof(Company).GetProperty(nameof(Company.Cik)));

        Assert.DoesNotContain(
            typeof(Security).GetProperties().Select(p => p.Name),
            n => n.Contains("Cik", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(
            Enum.GetNames<SecurityIdentifierKind>(),
            k => k.Contains("Cik", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_company_aggregate_gained_no_history_and_no_validity_interval()
    {
        // The D3 decision keeps Company current-state. If a future requirement proves otherwise it
        // opens its own gate; it does not arrive as a quiet pair of columns here.
        var names = typeof(Company).GetProperties().Select(p => p.Name).ToList();

        foreach (var forbidden in new[] { "CikValidFrom", "CikValidTo", "Version", "ValidFrom", "ValidTo" })
        {
            Assert.DoesNotContain(forbidden, names);
        }
    }
}
