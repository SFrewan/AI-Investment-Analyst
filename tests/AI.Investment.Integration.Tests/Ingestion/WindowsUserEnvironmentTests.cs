using AI.Investment.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AI.Investment.Integration.Tests.Ingestion;

/// <summary>
/// The credential arrives from where Windows stores it, not only from what this process inherited.
/// </summary>
/// <remarks>
/// <para>
/// The failure these exist for: a user environment variable lives in the per-user registry, and a
/// process reads its environment from a copy of its parent's block made when it started. Set the
/// variable, confirm it with <c>GetEnvironmentVariable(name, 'User')</c> - which reads the
/// registry - and a program launched from an Explorer window that predates the change still sees
/// nothing. Both observations are true, and until this source existed nothing in the report said
/// which question it had answered.
/// </para>
/// <para>
/// Most of these use an injected reader rather than a real variable, because a test that wrote to
/// the machine's user environment to prove a point would leave the point behind on the machine.
/// The one that does write uses a name of its own and removes it, and is the only test that can
/// prove the registry is genuinely being read - see
/// <see cref="A_variable_stored_for_this_user_is_read_from_where_Windows_keeps_it"/>.
/// </para>
/// <para>
/// As everywhere else in this suite, nothing here asserts a credential's value.
/// </para>
/// </remarks>
public sealed class WindowsUserEnvironmentTests
{
    private const string Stored = EodhdTestOptions.SyntheticKey;
    private const string Inherited = EodhdTestOptions.OtherSyntheticKey;

    /// <summary>A reader standing in for the per-user store, answering one path.</summary>
    private static Func<string, string?> Store(string? value) =>
        path => string.Equals(path, EodhdOptions.ApiKeyPath, StringComparison.Ordinal)
            ? value
            : null;

    // ---- the source supplies the credential ---------------------------------------------------

    /// <summary>
    /// A stored value reaches the bound options when the process block has nothing.
    /// </summary>
    /// <remarks>
    /// The regression this file is named for. Before the source existed this returned an empty
    /// credential and the check reported "not set" about a variable the operator had set.
    /// </remarks>
    [Fact]
    public void A_stored_credential_reaches_the_bound_options()
    {
        var options = Compose(stored: Stored, inherited: null);

        var presence = CredentialPresence.Of(options.ApiKey);

        Assert.True(presence.IsConfigured);
        Assert.Equal(CredentialPresence.Of(Stored).Fingerprint, presence.Fingerprint);
    }

    /// <summary>
    /// The process block still wins when it has a value, so overriding for one run still works.
    /// </summary>
    /// <remarks>
    /// The half that makes this safe. A source that read the stored value <em>over</em> the
    /// process block would break every existing way of pointing a single run at a different
    /// credential - a variable set in the shell that launches a test, a CI job's injected secret -
    /// and would do it silently, by preferring a machine-wide value to a deliberate local one.
    /// </remarks>
    [Fact]
    public void An_inherited_credential_outranks_a_stored_one()
    {
        var options = Compose(stored: Stored, inherited: Inherited);

        Assert.Equal(
            CredentialPresence.Of(Inherited).Fingerprint,
            CredentialPresence.Of(options.ApiKey).Fingerprint);
    }

    /// <summary>Neither present is still nothing, rather than an empty string that looks set.</summary>
    [Fact]
    public void Nothing_stored_and_nothing_inherited_is_no_credential() =>
        Assert.False(CredentialPresence.Of(Compose(stored: null, inherited: null).ApiKey).IsConfigured);

    /// <summary>
    /// A variable stored as blank is not a credential and does not shadow one.
    /// </summary>
    /// <remarks>
    /// The state left behind by clearing a variable through the Windows UI rather than removing
    /// it. An empty entry that counted as a source would out-rank the layers beneath it with a
    /// value of nothing, which is the confusion this whole class exists to end rather than add to.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_stored_variable_supplies_nothing(string blank) =>
        Assert.False(CredentialPresence.Of(Compose(stored: blank, inherited: null).ApiKey).IsConfigured);

    /// <summary>The source answers for the credential and for nothing else.</summary>
    /// <remarks>
    /// Mirroring the whole user environment would let any variable named after a settings path
    /// bind on a machine whose owner never opted into that. The allow-list is the boundary, and
    /// this is the test that notices if it stops being one.
    /// </remarks>
    [Fact]
    public void The_source_supplies_only_the_declared_paths()
    {
        // A reader that would answer anything at all, to show that only the declared path is asked.
        var configuration = new ConfigurationBuilder()
            .AddWindowsUserEnvironment(_ => Stored)
            .Build();

        Assert.Equal(WindowsUserEnvironment.SuppliedPaths.Count, Supplied(configuration).Count);
        Assert.Contains(EodhdOptions.ApiKeyPath, Supplied(configuration), StringComparer.Ordinal);
        Assert.Null(configuration["Database:ConnectionString"]);
    }

    [Fact]
    public void The_declared_path_is_the_credential() =>
        Assert.Equal(
            EodhdOptions.ApiKeyPath,
            Assert.Single(WindowsUserEnvironment.SuppliedPaths));

    // ---- the name translation --------------------------------------------------------------

    [Fact]
    public void A_configuration_path_becomes_the_documented_variable_name() =>
        Assert.Equal(
            EodhdOptions.ApiKeyEnvironmentVariable,
            WindowsUserEnvironment.VariableNameFor(EodhdOptions.ApiKeyPath));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_path_has_no_variable_name(string blank) =>
        Assert.Throws<ArgumentException>(() => WindowsUserEnvironment.VariableNameFor(blank));

    // ---- the part that actually touches Windows ------------------------------------------------

    /// <summary>
    /// A value stored for this user is read back from the store, with nothing in the process block.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only test here that proves the claim rather than the plumbing: that
    /// <see cref="WindowsUserEnvironment.Read"/> reads a place <c>AddEnvironmentVariables</c>
    /// cannot see. It writes a real per-user variable, under a name of its own that no
    /// configuration binds, asserts that the process block does <em>not</em> contain it - which is
    /// what makes the read meaningful - and removes it again.
    /// </para>
    /// <para>
    /// Skipped off Windows, where a per-user environment does not exist and a process block is the
    /// only copy there is.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public void A_variable_stored_for_this_user_is_read_from_where_Windows_keeps_it()
    {
        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "A per-user stored environment is a Windows concept; elsewhere the process block is " +
            "the only copy and the ordinary provider already reads it.");

        // Not the credential's name, and not a path anything binds. This test is about the
        // mechanism, and borrowing the real name would write a real setting onto the machine.
        const string Path = "AI.Investment.Tests:UserScopeProbe";

        var name = WindowsUserEnvironment.VariableNameFor(Path);
        var value = "stored-" + Guid.NewGuid().ToString("N");

        try
        {
            Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);

            // The process block is a copy taken when this process started, so a variable set now
            // is not in it. That is the whole asymmetry, asserted rather than assumed.
            Assert.Null(Environment.GetEnvironmentVariable(name));

            Assert.Equal(value, WindowsUserEnvironment.Read(Path));

            Assert.Contains(
                name,
                WindowsUserEnvironment.NamesBeginningWith("AI.Investment"),
                StringComparer.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User);
        }
    }

    /// <summary>The name diagnostic reports names, and cannot report a value.</summary>
    [Fact]
    public void Listing_stored_names_returns_names()
    {
        foreach (var name in WindowsUserEnvironment.NamesBeginningWith("Providers"))
        {
            Assert.StartsWith("Providers", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("=", name, StringComparison.Ordinal);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>
    /// The composition the host and the backfill fixture both build, in miniature.
    /// </summary>
    /// <remarks>
    /// The stored source, then the process environment on top of it - the same two lines and the
    /// same order as <c>Program.BuildApplication</c> and <c>BackfillApiFactory</c>. The process
    /// block is stood in for by an in-memory collection so the test does not depend on, or
    /// disturb, the environment it runs in.
    /// </remarks>
    private static EodhdOptions Compose(string? stored, string? inherited)
    {
        var builder = new ConfigurationBuilder().AddWindowsUserEnvironment(Store(stored));

        if (inherited is not null)
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [EodhdOptions.ApiKeyPath] = inherited,
            });
        }

        return builder.Build().GetSection(EodhdOptions.SectionName).Get<EodhdOptions>()
            ?? new EodhdOptions();
    }

    private static List<string> Supplied(IConfiguration configuration) =>
        configuration.AsEnumerable()
            .Where(pair => pair.Value is not null)
            .Select(pair => pair.Key)
            .ToList();
}
