using System.Globalization;
using System.Text;
using AI.Investment.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The credential is not in the repository, and - when asked - it is where the operator put it.
/// </summary>
/// <remarks>
/// <para>
/// Two different questions, deliberately in one place. The first runs on every build and is about
/// this repository: no tracked configuration file carries an EODHD credential. The second runs
/// only when asked and is about one machine: the credential the operator set is reaching the
/// composition that will spend the subscription, it arrived from the environment rather than from
/// a stale secrets store, and it has not leaked into anything this repository writes.
/// </para>
/// <para>
/// <strong>Neither of them ever holds the value in an assertion or writes it anywhere.</strong>
/// The live check works entirely through <see cref="CredentialPresence"/>, which reports whether
/// something is configured, how long it is and a domain-separated fingerprint. An operator can
/// compare that fingerprint against a second machine and learn whether the two agree, without
/// either machine having disclosed what they agree on.
/// </para>
/// </remarks>
public sealed class CredentialHygieneTests : IClassFixture<BackfillApiFactory>
{
    private const string GateVariable = "AIINV_CREDENTIAL";

    /// <summary>Directories whose contents are not source and would only slow the scan.</summary>
    private static readonly string[] Ignored = ["bin", "obj", ".git", ".vs", "node_modules"];

    /// <summary>The parts of the tree this repository actually writes into.</summary>
    private static readonly string[] Scanned =
        ["src", "tests", "docs", "scripts", "declarations", "artifacts"];

    /// <summary>Files above this size are payload archives, not text this repository wrote.</summary>
    private const long MaxScannedBytes = 32L * 1024 * 1024;

    private readonly BackfillApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public CredentialHygieneTests(BackfillApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    // ---- always: the repository carries no credential ------------------------------------------

    /// <summary>
    /// No <c>appsettings</c> file in the repository carries a value at the credential's key.
    /// </summary>
    /// <remarks>
    /// Read through the configuration binder rather than searched for as text, so it asks the
    /// question the application asks: is there a value at this path? A comment that mentions the
    /// key is fine and there are two of them; a value is not, whatever it is spelled like.
    /// </remarks>
    [Fact]
    public void No_settings_file_in_the_repository_carries_the_credential()
    {
        var offenders = new List<string>();

        foreach (var file in SettingsFiles())
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(file, optional: false)
                .Build();

            if (!string.IsNullOrWhiteSpace(configuration[EodhdOptions.ApiKeyPath]))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A credential is present at '" + EodhdOptions.ApiKeyPath + "' in: " +
            string.Join(", ", offenders) + ". It belongs in the user-secrets store or the " +
            "environment variable " + EodhdOptions.ApiKeyEnvironmentVariable +
            ". Remove it, then rotate it: a value that reached a tracked file must be treated as " +
            "disclosed even if it was never pushed.");
    }

    /// <summary>There is at least one settings file, or the test above is vacuously true.</summary>
    [Fact]
    public void The_scan_actually_finds_settings_files() =>
        Assert.NotEmpty(SettingsFiles());

    /// <summary>
    /// The shipped defaults still say where the credential goes.
    /// </summary>
    /// <remarks>
    /// The absence of a value is only half of it. A file that dropped the note explaining why the
    /// value is absent is a file that invites the next person to add one.
    /// </remarks>
    [Fact]
    public void The_shipped_settings_still_say_where_the_credential_goes()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "AI.Investment.Api", "appsettings.json"));

        Assert.Contains(
            EodhdOptions.ApiKeyEnvironmentVariable,
            text,
            StringComparison.Ordinal);
    }

    // ---- on request: the credential on this machine ---------------------------------------------

    /// <summary>
    /// Asks the running composition whether the operator's credential arrived, and from where.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Boots <see cref="BackfillApiFactory"/> rather than a configuration builder of its own,
    /// because that fixture is the composition the acquisition runs under. A check that proved a
    /// different pipeline could read the variable would prove the wrong thing on exactly the day
    /// it mattered.
    /// </para>
    /// <para>
    /// <strong>No provider is called.</strong> It resolves options and reads configuration. The
    /// vendor is not contacted, no ingestion request is built and nothing is written to the
    /// database.
    /// </para>
    /// </remarks>
    [SkippableFact]
    public async Task The_configured_credential_reaches_the_acquisition_composition()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"Credential check is off. Set {GateVariable}=1 to run it. It makes no provider calls.");

        var report = new StringBuilder();

        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var configuration = services.GetRequiredService<IConfiguration>();
        var presence = CredentialPresence.Of(configuration[EodhdOptions.ApiKeyPath]);

        // Two different questions that look like one. The process block is the copy this test
        // inherited from whatever launched it; the stored value is what the operator actually set.
        // A report that conflated them said "not set" about a variable that was set.
        var fromProcess = CredentialPresence.Of(
            Environment.GetEnvironmentVariable(EodhdOptions.ApiKeyEnvironmentVariable));

        var fromUserStore = CredentialPresence.Of(
            WindowsUserEnvironment.Read(EodhdOptions.ApiKeyPath));

        var winner = SourceOf(configuration, EodhdOptions.ApiKeyPath);

        report.AppendLine("# EODHD credential - presence check");
        report.AppendLine();
        report.AppendLine("No provider was called. The credential's value is not in this file.");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | --- |");
        report.AppendLine(Inv($"| Configuration path | `{EodhdOptions.ApiKeyPath}` |"));
        report.AppendLine(Inv($"| Environment variable | `{EodhdOptions.ApiKeyEnvironmentVariable}` |"));
        report.AppendLine(Inv($"| Present | {(presence.IsConfigured ? "yes" : "**no**")} |"));
        report.AppendLine(Inv($"| Length | {presence.Length} characters |"));
        report.AppendLine(Inv($"| Fingerprint | `{presence.Fingerprint}` |"));
        report.AppendLine(Inv($"| Leading or trailing whitespace | {(presence.IsPadded ? "**yes**" : "no")} |"));
        report.AppendLine(Inv($"| Supplied by | `{winner}` |"));
        report.AppendLine(Inv($"| Stored for this user | {(fromUserStore.IsConfigured ? "yes" : "**no**")} |"));
        report.AppendLine(Inv($"| Stored fingerprint | `{fromUserStore.Fingerprint}` |"));
        report.AppendLine(Inv($"| Inherited by this process | {(fromProcess.IsConfigured ? "yes" : "no")} |"));
        report.AppendLine(Inv($"| Inherited fingerprint | `{fromProcess.Fingerprint}` |"));

        var leaks = FilesContaining(configuration[EodhdOptions.ApiKeyPath]);

        report.AppendLine(Inv($"| Files in the repository containing it | {leaks.Count} |"));
        report.AppendLine();

        report.AppendLine(Verdict(presence, fromUserStore, fromProcess, winner));
        report.AppendLine();

        // The names an operator actually has, when nothing arrived. A variable set under a name
        // that is nearly right is indistinguishable from no variable at all, and is the likelier
        // mistake once the stored value has been ruled out.
        if (!fromUserStore.IsConfigured && !fromProcess.IsConfigured)
        {
            var similar = WindowsUserEnvironment.NamesBeginningWith("Providers");

            report.AppendLine("Stored per-user variable names beginning with `Providers` " +
                "(names only, never values):");
            report.AppendLine();
            report.AppendLine(similar.Count == 0
                ? "- none"
                : "- `" + string.Join("`\n- `", similar) + "`");
            report.AppendLine();
        }

        await WriteAsync(report);
        _output.WriteLine(report.ToString());

        Assert.True(
            presence.IsConfigured,
            "No credential reached the acquisition composition, and none is stored for this user " +
            "under " + EodhdOptions.ApiKeyEnvironmentVariable + ". Inheriting it is no longer " +
            "required - the stored value is read directly - so this means the variable is not " +
            "set, or is set under a different name. See the names listed in " +
            "artifacts/verify/credential.md.");

        Assert.False(
            presence.IsPadded,
            "The credential reached the composition with leading or trailing whitespace. " +
            EodhdOptions.PaddedApiKeyMessage);

        // The options the connectors read carry the same credential the configuration holds. Both
        // sides are compared as fingerprints, so a mismatch is reported without either being shown.
        var bound = services.GetRequiredService<IOptions<EodhdOptions>>().Value;

        Assert.Equal(presence.Fingerprint, CredentialPresence.Of(bound.ApiKey).Fingerprint);

        Assert.True(
            leaks.Count == 0,
            "The credential appears in " + string.Join(", ", leaks) + ". Rotate it, then find out " +
            "what wrote it there.");
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>
    /// What the check concludes, in the sentence an operator needs.
    /// </summary>
    /// <remarks>
    /// The interesting case is the third one. A machine that has been through an earlier stage of
    /// this project may still hold a token in the user-secrets store; that store and an
    /// <c>appsettings</c> file are both a <c>JsonConfigurationProvider</c>, which is why the source
    /// is reported by file rather than by type. A stale entry there is not a leak and breaks
    /// nothing - it simply means the token that gets spent is not the one just set.
    /// </remarks>
    private static string Verdict(
        CredentialPresence inForce,
        CredentialPresence fromUserStore,
        CredentialPresence fromProcess,
        string winner)
    {
        if (!inForce.IsConfigured)
        {
            return "**No credential reached the composition, and none is stored for this user.** " +
                "Set " + EodhdOptions.ApiKeyEnvironmentVariable + " - the names listed below are " +
                "what is actually stored, which is where a near-miss spelling shows up.";
        }

        var environmental = fromUserStore.IsConfigured || fromProcess.IsConfigured;

        if (!environmental)
        {
            return "**The credential in force did not come from the environment.** It came from `" +
                winner + "`, and nothing is stored for this user under " +
                EodhdOptions.ApiKeyEnvironmentVariable + ". Nothing is broken, but the token that " +
                "would be spent is whatever that source holds.";
        }

        var expected = fromProcess.IsConfigured ? fromProcess : fromUserStore;

        if (!string.Equals(inForce.Fingerprint, expected.Fingerprint, StringComparison.Ordinal))
        {
            return "**Two different credentials are present, and the environment's is not the one " +
                "in force.** `" + winner + "` outranks it with fingerprint `" + inForce.Fingerprint +
                "`, while the environment holds `" + expected.Fingerprint + "`. Remove the other " +
                "so there is a single source of truth: `dotnet user-secrets remove \"" +
                EodhdOptions.ApiKeyPath + "\" --project src/AI.Investment.Api`.";
        }

        // The case this whole change is about: stored, not inherited, and it arrived anyway.
        return fromProcess.IsConfigured
            ? "**The environment variable is the credential in force**, inherited by this process " +
              "and supplied by `" + winner + "`."
            : "**The stored per-user variable is the credential in force**, supplied by `" +
              winner + "`. This process did not inherit it - it was launched by something that " +
              "started before the variable was set - so it was read from where it is stored " +
              "instead. That is the intended behaviour, not a fallback that hid a problem: " +
              "signing out and back in would make the two agree, and nothing depends on it.";
    }

    /// <summary>
    /// The configuration provider that supplied a path - the last one registered that has it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Later sources override earlier ones, so the winner is the last provider that can answer.
    /// </para>
    /// <para>
    /// <strong>Reported by file, not by type, and that distinction is the point.</strong> The
    /// user-secrets store is loaded with <c>AddJsonFile</c>, so it and every <c>appsettings</c>
    /// file report as the same <c>JsonConfigurationProvider</c> - which makes the type name unable
    /// to answer the one question this diagnostic exists for. A file provider's own
    /// <c>ToString</c> names its path, which separates <c>secrets.json</c> from
    /// <c>appsettings.Development.json</c>.
    /// </para>
    /// </remarks>
    private static string SourceOf(IConfiguration configuration, string path)
    {
        if (configuration is not IConfigurationRoot root)
        {
            return "unknown";
        }

        var providers = root.Providers.ToList();

        for (var index = providers.Count - 1; index >= 0; index--)
        {
            var provider = providers[index];

            if (!provider.TryGet(path, out _))
            {
                continue;
            }

            return provider is FileConfigurationProvider
                ? provider.ToString() ?? provider.GetType().Name
                : provider.GetType().Name;
        }

        return "nothing";
    }

    /// <summary>Repository files containing the credential verbatim. Empty is the only good answer.</summary>
    private static List<string> FilesContaining(string? credential)
    {
        var found = new List<string>();

        if (string.IsNullOrWhiteSpace(credential))
        {
            return found;
        }

        var root = RepositoryRoot();

        foreach (var directory in Scanned)
        {
            var path = Path.Combine(root, directory);

            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                if (IsIgnored(file) || new FileInfo(file).Length > MaxScannedBytes)
                {
                    continue;
                }

                if (Contains(file, credential))
                {
                    found.Add(Path.GetRelativePath(root, file));
                }
            }
        }

        return found;
    }

    private static bool Contains(string file, string credential)
    {
        try
        {
            return File.ReadAllText(file).Contains(credential, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            // A file being written by something else is not evidence of a leak.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsIgnored(string path)
    {
        foreach (var segment in Ignored)
        {
            if (path.Contains(
                    Path.DirectorySeparatorChar + segment + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> SettingsFiles()
    {
        var root = Path.Combine(RepositoryRoot(), "src");

        return Directory
            .EnumerateFiles(root, "appsettings*.json", SearchOption.AllDirectories)
            .Where(file => !IsIgnored(file))
            .ToList();
    }

    private static string RepositoryRoot() => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    private static async Task WriteAsync(StringBuilder report)
    {
        var path = Path.Combine(RepositoryRoot(), "artifacts", "verify", "credential.md");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllTextAsync(path, report.ToString());
    }
}
