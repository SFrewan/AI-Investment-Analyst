using AI.Investment.Application.Companies.SearchCompanies;
using AI.Investment.Application.UnitTests.Fakes;
using AI.Investment.Domain.Companies;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Application.UnitTests.Companies;

public sealed class SearchCompaniesHandlerTests
{
    private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly InMemoryCompanyRepository _repository = new();

    private SearchCompaniesHandler Handler() => new(_repository);

    private void Seed(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _repository.Companies.Add(Company.Create(
                CompanyId.New(),
                $"Company {i:0000}",
                Ticker.Create($"C{i:0000}"),
                Now));
        }
    }

    [Fact]
    public async Task An_empty_repository_returns_an_empty_page()
    {
        var result = await Handler().HandleAsync(new SearchCompaniesQuery());

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    /// <summary>
    /// An out-of-range page size is clamped rather than rejected: it is a caller mistake with an
    /// obvious safe reading, while honouring take=1000000 is a denial-of-service against our own
    /// database.
    /// </summary>
    [Theory]
    [InlineData(0, SearchCompaniesHandler.DefaultTake)]
    [InlineData(-5, SearchCompaniesHandler.DefaultTake)]
    [InlineData(1_000_000, SearchCompaniesHandler.MaxTake)]
    [InlineData(10, 10)]
    public async Task Page_size_is_clamped_to_a_safe_range(int requested, int expected)
    {
        Seed(300);

        var result = await Handler().HandleAsync(new SearchCompaniesQuery(Take: requested));

        Assert.Equal(expected, result.Take);
        Assert.Equal(expected, result.Items.Count);
    }

    [Fact]
    public async Task A_negative_skip_is_clamped_to_zero()
    {
        Seed(5);

        var result = await Handler().HandleAsync(new SearchCompaniesQuery(Skip: -10));

        Assert.Equal(0, result.Skip);
    }

    [Fact]
    public async Task The_total_count_reflects_the_whole_result_set_not_the_page()
    {
        Seed(100);

        var result = await Handler().HandleAsync(new SearchCompaniesQuery(Take: 10));

        Assert.Equal(100, result.TotalCount);
        Assert.Equal(10, result.Items.Count);
    }
}
