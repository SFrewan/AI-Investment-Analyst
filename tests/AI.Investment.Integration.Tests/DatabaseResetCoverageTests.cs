using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Investment.Integration.Tests;

/// <summary>
/// Proves that the statement which empties the test database empties all of it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PostgresFixture.TruncateStatement"/> is written out by hand so that it is a
/// compile-time constant and so that a reviewer can read what it destroys. The price of writing it
/// out is that it can fall behind the model, and the failure mode is quiet: rows in the forgotten
/// table survive between tests, and some later test fails on a duplicate key for reasons that have
/// nothing to do with the code it is testing. That is precisely the defect the reset exists to
/// remove, so it is checked rather than remembered.
/// </para>
/// <para>
/// No database is needed. Building the model does not open a connection, which is why this test
/// runs everywhere and does not skip.
/// </para>
/// </remarks>
public sealed class DatabaseResetCoverageTests
{
    private const string QualifiedNamePrefix = "\"public\".\"";

    [Fact]
    public void Every_mapped_table_is_emptied_between_tests()
    {
        foreach (var table in MappedTables())
        {
            Assert.Contains(
                $"{QualifiedNamePrefix}{table}\"",
                PostgresFixture.TruncateStatement,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The other direction: a table listed in the statement but no longer in the model would be a
    /// truncation of something this model does not own.
    /// </summary>
    [Fact]
    public void The_reset_statement_names_nothing_the_model_does_not_map()
    {
        Assert.Equal(MappedTables().Count, CountQualifiedNames(PostgresFixture.TruncateStatement));
    }

    /// <summary>
    /// The statement qualifies every table with <c>public</c>. A configuration that moved a table
    /// to another schema would leave it untouched by a reset that still says <c>public</c>.
    /// </summary>
    [Fact]
    public void Every_mapped_table_is_in_the_public_schema()
    {
        using var context = BuildModelOnlyContext();

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            var schema = entityType.GetSchema();

            Assert.True(
                schema is null or "public",
                $"{entityType.Name} is mapped to schema '{schema}', which the test " +
                $"database reset does not empty.");
        }
    }

    /// <summary>Counts the schema-qualified table names the statement lists.</summary>
    private static int CountQualifiedNames(string statement)
    {
        var count = 0;

        for (var at = statement.IndexOf(QualifiedNamePrefix, StringComparison.Ordinal);
             at >= 0;
             at = statement.IndexOf(QualifiedNamePrefix, at + QualifiedNamePrefix.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static List<string> MappedTables()
    {
        using var context = BuildModelOnlyContext();

        // Owned types report their owner's table, so distinct names are the tables that exist.
        return context.Model
            .GetEntityTypes()
            .Select(entityType => entityType.GetTableName())
            .Where(name => name is not null)
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// A context built purely to ask the model a question. Npgsql parses the connection string
    /// but opens nothing until a query runs, and no query runs here.
    /// </summary>
    private static AppDbContext BuildModelOnlyContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=model-only.invalid;Database=model_only")
            .Options;

        return new AppDbContext(options, new ScopedWriteAuthorization());
    }
}
