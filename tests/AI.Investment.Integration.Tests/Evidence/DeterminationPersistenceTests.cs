using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.Evidence;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Investment.Integration.Tests.Evidence;

/// <summary>
/// The Stage E determination store against a real database.
/// </summary>
/// <remarks>
/// The reproducibility key is the part that cannot be proved in memory: it is a unique index over
/// four columns, one of which is a digest folded from an ordered list held as jsonb. A value
/// converter plus a comparer is exactly the arrangement that looks correct in a unit test and loses
/// its ordering on the way back from PostgreSQL.
/// </remarks>
[Collection(nameof(SharedPostgresDatabase))]
public sealed class DeterminationPersistenceTests : IAsyncLifetime
{
    private static readonly DateTime AsOf = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

    private static readonly ContentHash First = ContentHash.Compute("first"u8);
    private static readonly ContentHash Second = ContentHash.Compute("second"u8);
    private static readonly ContentHash Third = ContentHash.Compute("third"u8);

    private readonly PostgresFixture _fixture;

    public DeterminationPersistenceTests(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task The_determinations_table_exists_and_holds_no_foreign_key()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        Assert.Equal(0, await context.Determinations.CountAsync());

        var entity = context.Model.FindEntityType(typeof(Determination))!;

        // The design in one assertion: inputs are hashes, not references.
        Assert.Empty(entity.GetForeignKeys());

        var columns = entity.GetProperties().Select(p => p.GetColumnName()).ToList();

        Assert.Contains("inputs", columns);
        Assert.Contains("inputs_hash", columns);
        Assert.Contains("output_content_hash", columns);
    }

    [SkippableFact]
    public async Task A_determination_round_trips_with_its_inputs_in_order()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();

        await using (var context = _fixture.CreateContext(authorization))
        {
            using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
            {
                context.Determinations.Add(Record(inputs: [Third, First, Second]));
                await context.SaveChangesAsync();
            }
        }

        await using var reader = _fixture.CreateContext(new ScopedWriteAuthorization());

        var stored = await reader.Determinations.SingleAsync();

        Assert.Equal(ClaimKind.Calculation, stored.Kind);
        Assert.Equal("gate6-coverage", stored.RuleId);
        Assert.Equal("1", stored.RuleVersion);
        Assert.Equal(AsOf, stored.AsOfUtc);
        Assert.Null(stored.Confidence);

        // Order survives the jsonb round-trip, and the stored digest still describes it.
        Assert.Equal([Third, First, Second], stored.Inputs);
        Assert.True(stored.InputsHashMatchesInputs());
        Assert.Equal(ContentHash.Compute("24/130"u8), stored.OutputContentHash);
    }

    [SkippableFact]
    public async Task The_same_rule_version_instant_and_inputs_cannot_be_recorded_twice()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            context.Determinations.Add(Record());
            await context.SaveChangesAsync();

            // A different output makes it worse, not better: one of the two would be wrong.
            context.Determinations.Add(Record(output: "19/125"));

            await Assert.ThrowsAnyAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
    }

    [SkippableFact]
    public async Task A_different_rule_version_as_of_or_input_set_is_a_distinct_determination()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            context.Determinations.Add(Record());
            context.Determinations.Add(Record(ruleVersion: "2"));
            context.Determinations.Add(Record(asOfUtc: AsOf.AddDays(-1)));
            context.Determinations.Add(Record(inputs: [Second, First]));
            context.Determinations.Add(Record(ruleId: "other-rule"));

            await context.SaveChangesAsync();
        }

        await using var reader = _fixture.CreateContext(new ScopedWriteAuthorization());

        Assert.Equal(5, await reader.Determinations.CountAsync());
    }

    /// <summary>
    /// Re-ordering the inputs is a different determination, not a duplicate of the same one.
    /// </summary>
    [SkippableFact]
    public async Task Input_ordering_is_part_of_the_key_at_the_database_level()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            context.Determinations.Add(Record(inputs: [First, Second]));
            context.Determinations.Add(Record(inputs: [Second, First]));
            await context.SaveChangesAsync();
        }

        await using var reader = _fixture.CreateContext(new ScopedWriteAuthorization());

        var hashes = await reader.Determinations.Select(d => d.InputsHash).ToListAsync();

        Assert.Equal(2, hashes.Distinct(StringComparer.Ordinal).Count());
    }

    [SkippableFact]
    public async Task Recording_a_determination_without_an_authorisation_window_is_refused()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await using var context = _fixture.CreateContext(new ScopedWriteAuthorization());

        context.Determinations.Add(Record());

        await Assert.ThrowsAsync<UnauthorizedWriteException>(() => context.SaveChangesAsync());
    }

    [SkippableFact]
    public async Task A_stored_determination_cannot_be_modified()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await SeedAsync();

        var second = new ScopedWriteAuthorization();
        await using var editor = _fixture.CreateContext(second);

        var stored = await editor.Determinations.SingleAsync();
        editor.Entry(stored).Property(d => d.OutputCanonical).CurrentValue = "rewritten";

        using (second.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            var thrown = await Assert.ThrowsAsync<UnauthorizedWriteException>(
                () => editor.SaveChangesAsync());

            Assert.Contains("append-only", thrown.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [SkippableFact]
    public async Task A_stored_determination_cannot_be_deleted()
    {
        Skip.IfNot(_fixture.Available, _fixture.UnavailableReason);

        await SeedAsync();

        var second = new ScopedWriteAuthorization();
        await using var editor = _fixture.CreateContext(second);

        editor.Determinations.Remove(await editor.Determinations.SingleAsync());

        using (second.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            await Assert.ThrowsAsync<UnauthorizedWriteException>(() => editor.SaveChangesAsync());
        }
    }

    private async Task SeedAsync()
    {
        var authorization = new ScopedWriteAuthorization();

        await using var context = _fixture.CreateContext(authorization);

        using (authorization.Authorize(SeamTestDecisions.ExecuteDecision(Now)))
        {
            context.Determinations.Add(Record());
            await context.SaveChangesAsync();
        }
    }

    private static Determination Record(
        string ruleId = "gate6-coverage",
        string ruleVersion = "1",
        IEnumerable<ContentHash>? inputs = null,
        string output = "24/130",
        DateTime? asOfUtc = null) =>
        Determination.Record(
            DeterminationId.New(),
            ClaimKind.Calculation,
            ruleId,
            ruleVersion,
            inputs ?? [First, Second],
            output,
            asOfUtc ?? AsOf,
            Now);
}
