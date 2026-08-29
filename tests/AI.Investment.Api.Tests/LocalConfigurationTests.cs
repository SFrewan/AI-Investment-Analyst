using System.ComponentModel.DataAnnotations;
using AI.Investment.Infrastructure.Configuration;
using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The configuration a host cannot start without, and what it says when it is missing.
/// </summary>
/// <remarks>
/// A developer meets this validation before they meet anything else in the platform. The failure
/// is deliberately kept fatal - a host that started without a database would fail later, on a
/// request, on a different machine - so the only thing left to get right is that it says where to
/// put the value.
/// </remarks>
public sealed class LocalConfigurationTests
{
    [Fact]
    public void A_missing_connection_string_fails_validation()
    {
        var results = Validate(new DatabaseOptions());

        Assert.NotEmpty(results);
    }

    [Fact]
    public void A_whitespace_connection_string_fails_validation()
    {
        var results = Validate(new DatabaseOptions { ConnectionString = "   " });

        Assert.NotEmpty(results);
    }

    /// <summary>
    /// The message names the key, the preferred mechanism and the document. Asserting on it keeps
    /// the three from drifting apart the next time one of them is renamed.
    /// </summary>
    [Theory]
    [InlineData("Database:ConnectionString")]
    [InlineData("dotnet user-secrets")]
    [InlineData("Database__ConnectionString")]
    [InlineData("docs/LOCAL-DEVELOPMENT.md")]
    public void The_failure_says_where_to_put_the_value(string expected)
    {
        var results = Validate(new DatabaseOptions());

        Assert.Contains(
            results,
            result => result.ErrorMessage is not null &&
                result.ErrorMessage.Contains(expected, StringComparison.Ordinal));
    }

    /// <summary>
    /// It does not name a password, a host or an example credential. A validation message is
    /// printed to a console and pasted into an issue.
    /// </summary>
    [Theory]
    [InlineData("Password")]
    [InlineData("Username")]
    [InlineData("Host=")]
    public void The_failure_names_no_credential(string forbidden)
    {
        Assert.DoesNotContain(
            forbidden,
            DatabaseOptions.MissingConnectionStringMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_supplied_connection_string_validates()
    {
        var results = Validate(new DatabaseOptions
        {
            ConnectionString = "Host=localhost;Database=ai_investment;Username=someone",
        });

        Assert.Empty(results);
    }

    /// <summary>The bound section is the one the documentation tells a developer to set.</summary>
    [Fact]
    public void The_section_name_is_the_documented_one()
    {
        Assert.Equal("Database", DatabaseOptions.SectionName);
    }

    private static List<ValidationResult> Validate(DatabaseOptions options)
    {
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(
            options,
            new ValidationContext(options),
            results,
            validateAllProperties: true);

        return results;
    }
}
