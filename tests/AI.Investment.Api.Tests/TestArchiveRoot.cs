using System.Runtime.CompilerServices;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Keeps this assembly's archive writes out of the durable archive.
/// </summary>
/// <remarks>
/// <para>
/// Every host in this project boots the real API, which reads the real
/// <c>src/AI.Investment.Api/appsettings.json</c> - it is copied into the test output as content.
/// Since that file now names the durable archive, a test that stored a payload would write into
/// the directory the system keeps its evidence in. Nothing here should be able to do that, however
/// harmless the payload: an archive that accumulates test fixtures is an archive whose contents
/// can no longer be trusted to be things a provider actually returned.
/// </para>
/// <para>
/// <strong>The override is set here rather than in each host.</strong> Six
/// <c>WebApplicationFactory</c> subclasses boot this API today. Isolation that has to be repeated
/// six times is isolation the seventh will be written without, and the failure would be silent -
/// a passing test that quietly deposited a fixture in the evidence store. A module initialiser
/// runs before the first test in the assembly, applies to every host including ones not yet
/// written, and is asserted by <c>RawArchiveRootTests</c>.
/// </para>
/// <para>
/// <strong>It resolves to the assembly's own output directory</strong> - explicitly, not by
/// accident of the working directory, which is what the old configuration did and what the
/// resolver now refuses to do. Build output is the right home for anything a test writes, because
/// build output is disposable and test fixtures should be. It is also, byte for byte, where this
/// assembly has always written, so no payload already held there is stranded by the change.
/// </para>
/// <para>
/// It is set unconditionally, including over a value already in the environment. The point is a
/// guarantee rather than a default: a stray <c>RawArchive__RootPath</c> in an operator's shell
/// must not be able to aim the test suite at the evidence store. Directing a genuine acquisition
/// somewhere durable is a different decision, and one that should be visible in code rather than
/// inherited from a shell.
/// </para>
/// </remarks>
internal static class TestArchiveRoot
{
    /// <summary>The environment form of <c>RawArchive:RootPath</c>.</summary>
    internal const string ConfigurationVariable = "RawArchive__RootPath";

    /// <summary>
    /// The durable archive, as the shipped settings name it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one exception to the isolation below. An authorised acquisition spends acquisition
    /// units to obtain evidence, and evidence written into build output is evidence a rebuild may
    /// remove - which is the whole reason this constant exists. It is used only when the
    /// filing-document door is armed, by <c>UniverseApiFactory</c>, and by nothing else.
    /// </para>
    /// <para>
    /// Relative, and deliberately so: <c>RawArchiveRoot</c> anchors it to the working tree, so it
    /// names the same directory whatever the process was launched from without pinning a machine
    /// into a file. <c>RawArchiveRootTests</c> asserts it is exactly what
    /// <c>src/AI.Investment.Api/appsettings.json</c> ships, so the two cannot drift apart in
    /// silence.
    /// </para>
    /// </remarks>
    internal const string DurableRootPath = "src/AI.Investment.Api/archive";

    /// <summary>Where this assembly's archive writes go.</summary>
    internal static string Location { get; } = Path.Combine(AppContext.BaseDirectory, "archive");

    [ModuleInitializer]
    internal static void Isolate() =>
        Environment.SetEnvironmentVariable(ConfigurationVariable, Location);
}
