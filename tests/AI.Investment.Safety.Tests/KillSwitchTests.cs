using AI.Investment.Domain.Enums;
using AI.Investment.Infrastructure.Actions;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Persistence;
using AI.Investment.Infrastructure.Policy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// The kill-switch drill.
/// </summary>
/// <remarks>
/// <para>
/// A switch nobody has ever pulled is a switch of unknown state, so these tests pull it - by the
/// environment variable and by making the database unreadable - and assert what the system answers.
/// <strong>The case that matters most is the one where the switch cannot be read at all</strong>,
/// because that is the branch a fail-open defect would hide in: nothing is engaged, nothing throws,
/// and everything proceeds.
/// </para>
/// <para>
/// The environment variable is process-wide state, so these tests run in their own non-parallel
/// collection and restore whatever was there before. A test that leaked an engaged kill switch into
/// another test class would turn every other test green for the wrong reason.
/// </para>
/// </remarks>
[Collection(nameof(KillSwitchEnvironment))]
public sealed class KillSwitchTests : IDisposable
{
    private const string UnreachableDatabase =
        "Host=127.0.0.1;Port=1;Database=kill_switch_unreachable;Username=nobody;Password=nothing;Timeout=1;Command Timeout=1";

    private readonly string? _previous =
        Environment.GetEnvironmentVariable(SafetyOptions.KillSwitchEnvironmentVariable);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SafetyOptions.KillSwitchEnvironmentVariable, _previous);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_switch_that_cannot_be_read_reports_unknown_rather_than_disengaged()
    {
        Environment.SetEnvironmentVariable(SafetyOptions.KillSwitchEnvironmentVariable, null);

        var state = await Build(killSwitchEngaged: false).ReadAsync();

        Assert.Equal(KillSwitchState.Unknown, state);
        Assert.NotEqual(KillSwitchState.Disengaged, state);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("on")]
    [InlineData("engaged")]
    [InlineData("ENGAGED")]
    public async Task The_environment_variable_engages_the_switch_without_the_database(string value)
    {
        Environment.SetEnvironmentVariable(SafetyOptions.KillSwitchEnvironmentVariable, value);

        Assert.Equal(KillSwitchState.Engaged, await Build().ReadAsync());
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("yes please")]
    [InlineData("-1")]
    public async Task An_unrecognised_value_reads_as_unknown_rather_than_being_ignored(string value)
    {
        Environment.SetEnvironmentVariable(SafetyOptions.KillSwitchEnvironmentVariable, value);

        Assert.Equal(KillSwitchState.Unknown, await Build().ReadAsync());
    }

    [Fact]
    public async Task The_configured_flag_engages_the_switch_when_the_variable_says_nothing()
    {
        Environment.SetEnvironmentVariable(SafetyOptions.KillSwitchEnvironmentVariable, null);

        Assert.Equal(KillSwitchState.Engaged, await Build(killSwitchEngaged: true).ReadAsync());
    }

    [Fact]
    public async Task A_variable_that_says_disengaged_still_leaves_the_database_to_answer()
    {
        Environment.SetEnvironmentVariable(SafetyOptions.KillSwitchEnvironmentVariable, "false");

        // The database is unreachable, so the remaining half cannot answer, and an unanswerable
        // switch is an engaged one.
        Assert.Equal(KillSwitchState.Unknown, await Build().ReadAsync());
    }

    private static DatabaseAndEnvironmentKillSwitch Build(bool? killSwitchEngaged = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(UnreachableDatabase)
            .Options;

        var context = new AppDbContext(options, new ScopedWriteAuthorization());

        return new DatabaseAndEnvironmentKillSwitch(
            context,
            new StaticOptionsMonitor(new SafetyOptions { KillSwitchEngaged = killSwitchEngaged }),
            NullLogger<DatabaseAndEnvironmentKillSwitch>.Instance);
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<SafetyOptions>
    {
        public StaticOptionsMonitor(SafetyOptions value) => CurrentValue = value;

        public SafetyOptions CurrentValue { get; }

        public SafetyOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<SafetyOptions, string?> listener) => null;
    }
}

/// <summary>
/// Serialises the tests that mutate the kill-switch environment variable.
/// </summary>
[CollectionDefinition(nameof(KillSwitchEnvironment), DisableParallelization = true)]
public sealed class KillSwitchEnvironment;
