using System.Collections;
using Microsoft.Extensions.Configuration;

namespace AI.Investment.Infrastructure.Configuration;

/// <summary>
/// Reads a declared credential from the current user's <em>stored</em> environment, rather than
/// from the environment block this process happened to inherit.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The distinction this class exists for.</strong> On Windows a user environment variable
/// lives in the per-user registry. A process does not read it from there: it receives a copy of
/// its parent's environment block at creation, and that copy is a snapshot of whatever the parent
/// held. Explorer refreshes its own block when it processes the broadcast that follows a change -
/// when it processes it. Every process Explorer starts after that inherits the refreshed block;
/// every process it starts if it did not, does not, indefinitely.
/// </para>
/// <para>
/// So an operator can set a user variable, confirm it with
/// <c>[Environment]::GetEnvironmentVariable(name, 'User')</c> - which reads the registry - and
/// then watch a program launched by double-clicking a script report the variable as absent,
/// because that program inherited a block predating the change. Both observations are correct.
/// They are about different things, and nothing on screen says so.
/// </para>
/// <para>
/// <strong>Why this is a source and not an instruction to reboot.</strong> "Sign out and back in"
/// does fix it, and it fixes it silently, which means the next occurrence is diagnosed from
/// scratch. Reading the stored value makes the credential's arrival independent of which shell
/// launched what, and turns a class of failure that presents as a wrong key into one that cannot
/// happen. The remedy is still worth knowing and is in <c>docs/SECURITY.md</c>; it is no longer
/// load-bearing.
/// </para>
/// <para>
/// <strong>It is registered <em>below</em> the ordinary environment provider, not above it.</strong>
/// Configuration sources added later win, and <c>AddEnvironmentVariables</c> is re-added after
/// this one wherever this one is used. The process block therefore still decides when it has a
/// value - which keeps every existing way of overriding a setting for one run working exactly as
/// it did, including a variable set in the shell that launches a test. This source only answers
/// when nothing else would have.
/// </para>
/// <para>
/// <strong><see cref="SuppliedPaths"/> is an allow-list, deliberately.</strong> Mirroring the
/// whole user environment into configuration would let any variable an operator happens to have
/// named after a settings path silently bind, on a machine where they never opted into that. One
/// credential is what this exists for, and adding a second is a one-line, reviewable act.
/// </para>
/// </remarks>
public static class WindowsUserEnvironment
{
    /// <summary>
    /// The configuration paths this source will supply. One credential, by name.
    /// </summary>
    public static readonly IReadOnlyList<string> SuppliedPaths = [EodhdOptions.ApiKeyPath];

    /// <summary>The environment-variable name for a configuration path.</summary>
    /// <remarks>
    /// The same translation <c>AddEnvironmentVariables</c> applies in reverse: a colon separating
    /// configuration levels is a double underscore in a variable name.
    /// </remarks>
    public static string VariableNameFor(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);

        return configurationPath.Replace(":", "__", StringComparison.Ordinal);
    }

    /// <summary>
    /// The stored per-user value for a configuration path, or null where there is none.
    /// </summary>
    /// <remarks>
    /// Null on anything that is not Windows, because the per-user environment is a Windows
    /// concept: on Linux and macOS an environment variable exists only in a process block, so the
    /// ordinary provider is already reading the only copy there is.
    /// </remarks>
    public static string? Read(string configurationPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return Environment.GetEnvironmentVariable(
            VariableNameFor(configurationPath),
            EnvironmentVariableTarget.User);
    }

    /// <summary>
    /// The <em>names</em> of stored per-user variables beginning with a prefix. Never the values.
    /// </summary>
    /// <remarks>
    /// A diagnostic for the failure that looks identical to a missing variable and is not one: a
    /// variable set under a name that is nearly right. A single underscore where two belong, or a
    /// trailing space that survived a paste, produces a variable nothing reads and a report saying
    /// nothing is configured. Listing the names an operator actually has makes the typo visible
    /// without disclosing anything, because a name is not a secret and a value is.
    /// </remarks>
    public static IReadOnlyList<string> NamesBeginningWith(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var names = new List<string>();

        foreach (DictionaryEntry entry in
            Environment.GetEnvironmentVariables(EnvironmentVariableTarget.User))
        {
            if (entry.Key is string name &&
                name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                names.Add(name);
            }
        }

        names.Sort(StringComparer.Ordinal);

        return names;
    }

    /// <summary>
    /// Adds the stored per-user environment as a configuration source.
    /// </summary>
    /// <remarks>
    /// Callers add <c>AddEnvironmentVariables()</c> immediately after this, which looks redundant
    /// and is not: it restores the process block's precedence over this source, and over anything
    /// else the host registered before this call.
    /// </remarks>
    /// <param name="builder">The configuration being built.</param>
    /// <param name="read">
    /// How a path's stored value is read. Defaults to <see cref="Read"/>; supplied by tests, which
    /// cannot write to a real user's environment without leaving it behind.
    /// </param>
    public static IConfigurationBuilder AddWindowsUserEnvironment(
        this IConfigurationBuilder builder,
        Func<string, string?>? read = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Add(new WindowsUserEnvironmentSource(read));

        return builder;
    }
}

/// <summary>The source half of <see cref="WindowsUserEnvironment"/>.</summary>
public sealed class WindowsUserEnvironmentSource : IConfigurationSource
{
    private readonly Func<string, string?> _read;

    public WindowsUserEnvironmentSource(Func<string, string?>? read = null) =>
        _read = read ?? WindowsUserEnvironment.Read;

    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new WindowsUserEnvironmentProvider(_read);
}

/// <summary>
/// Supplies the declared paths that have a stored per-user value, and nothing else.
/// </summary>
/// <remarks>
/// A distinct provider type rather than an in-memory collection assembled at start-up, so that a
/// credential's origin is reportable. <c>MemoryConfigurationProvider</c> would tell an operator
/// asking where their token came from that it came from memory, which is true of every source
/// once it is loaded and answers nothing.
/// </remarks>
public sealed class WindowsUserEnvironmentProvider : ConfigurationProvider
{
    private readonly Func<string, string?> _read;

    internal WindowsUserEnvironmentProvider(Func<string, string?> read) => _read = read;

    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in WindowsUserEnvironment.SuppliedPaths)
        {
            var value = _read(path);

            // Blank is not a value. A variable set to an empty string would otherwise shadow
            // nothing while looking like a source, which is the confusion this class is about.
            if (!string.IsNullOrWhiteSpace(value))
            {
                data[path] = value;
            }
        }

        Data = data;
    }
}
