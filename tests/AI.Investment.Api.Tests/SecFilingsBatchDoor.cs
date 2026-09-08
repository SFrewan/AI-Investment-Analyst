using System.Globalization;
using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Domain.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The execution door: how the SEC runner is reached from the real application environment.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The runtime path this wires into already exists and is not being replaced.</strong> Both
/// acquisition runners in this repository are reached the same way, and nothing else in the
/// repository reaches a provider at all:
/// </para>
/// <code>
/// scripts\run-&lt;x&gt;.cmd
///   -> scripts\run-&lt;x&gt;.ps1        sets Providers__&lt;Vendor&gt;__Enabled and the gate variables
///     -> scripts\gate-tests.ps1     dotnet test --filter FullyQualifiedName~&lt;RunnerTests&gt;
///       -> the gated fact           WebApplicationFactory&lt;Program&gt; builds the real host
///         -> the real container     Program.cs composition, real configuration, real DI
/// </code>
/// <para>
/// So the door is: two environment variables with no defaults, a gated fact, and a script. This
/// class is the part of that with any logic in it, separated out so it can be driven against the
/// real container by tests without the gate being open - which is what lets "the provider is
/// disabled, so nothing can leave" be proven rather than asserted.
/// </para>
/// <para>
/// <strong>Two independent locks, and neither is this class's to open.</strong>
/// <c>Providers:SecEdgar:Enabled</c> is <c>false</c> in the shipped configuration, so the connector
/// is never registered and the preflight refuses on <c>ingestion.provider-available@1</c>. And every
/// batch in the partition is <c>Authorised: false</c>, so the runner refuses at its first gate
/// before a single service is resolved. Opening either is a separate, visible act.
/// </para>
/// <para>
/// <strong>Nothing here reads a secret into anything that persists.</strong> The fair-access contact
/// is read from the environment and handed to the runner, which validates it and reports only
/// whether it was usable. The value is never logged, never written to an artefact, and never
/// interpolated into a refusal message.
/// </para>
/// </remarks>
internal static class SecFilingsBatchDoor
{
    /// <summary>Opens the door. Deliberately has no default.</summary>
    public const string GateVariable = "AIINV_SEC_BATCH";

    /// <summary>Which batch to run. Deliberately has no default.</summary>
    public const string IndexVariable = "AIINV_SEC_BATCH_INDEX";

    /// <summary>The evidence base the installed authorisation is for.</summary>
    public const string SealedFingerprint = "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    /// <summary>The sealed universe, which is the authority on who is a member.</summary>
    public const string SealedManifest = "universe-sample400-2021-2026.json";

    /// <summary>The glob every SEC attempt artefact answers to.</summary>
    public const string ArtefactPattern = "acquisition-sec-*.json";

    /// <summary>The artefact field naming the authorisation a run charged.</summary>
    public const string AuthorizationProperty = "Authorization";

    /// <summary>And the field holding what it charged.</summary>
    public const string ConsumedProperty = "AuthorizationConsumedThisRun";

    /// <summary>Whether the gate variable is set to exactly "1". Nothing else opens it.</summary>
    public static bool IsOpen(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return string.Equals(environment(GateVariable), "1", StringComparison.Ordinal);
    }

    /// <summary>
    /// The batch an operator named, or null when they named none or named one that does not exist.
    /// </summary>
    /// <remarks>
    /// There is no default batch, following <c>AIINV_SPLIT_BATCH_INDEX</c>: a run that does not say
    /// which batch it is must not pick one.
    /// </remarks>
    public static SecEdgarSixMemberPartition.SecFilingBatchDefinition? Batch(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return int.TryParse(
            environment(IndexVariable),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var index)
            ? BatchAt(index)
            : null;
    }

    /// <summary>The batch with that index, or null. The partition is the only source of batches.</summary>
    public static SecEdgarSixMemberPartition.SecFilingBatchDefinition? BatchAt(int index) =>
        Array.Find(SecEdgarSixMemberPartition.Batches, b => b.Index == index);

    /// <summary>The installed authorisation, loaded against the sealed evidence base.</summary>
    /// <remarks>
    /// The digest is verified by the loader before any arithmetic, and the arithmetic is then forced:
    /// ceiling equals planned less satisfied, and planned equals subjects times sources. An
    /// authorisation that cannot be read is not an authorisation for everything.
    /// </remarks>
    public static AcquisitionAuthorization LoadAuthorization() =>
        AcquisitionAuthorization.Load(
            Universe.RepositoryPath("declarations", SecEdgarSixMemberPartition.Declaration),
            SealedFingerprint);

    /// <summary>The sealed universe's members, for the runner's identity gate.</summary>
    public static async Task<HashSet<string>> SealedCiksAsync()
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("declarations", SealedManifest)).ConfigureAwait(false));

        return manifest.RootElement
            .GetProperty("Members")
            .EnumerateArray()
            .Select(m => m.GetProperty("Cik").GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The reading scope the installed declaration states: shared forms, one member's extras, and
    /// each member's selection windows.
    /// </summary>
    /// <remarks>
    /// Read from the declaration rather than restated here, so the scope a run applies is the scope
    /// a reviewer approved. The per-member entry is taken as it is written - one member, its own
    /// forms, and <c>AppliesToOtherMembers</c> false - and the runner honours that by CIK.
    /// </remarks>
    public static async Task<SecFilingScope> ReadScopeAsync()
    {
        using var declaration = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("declarations", SecEdgarSixMemberPartition.Declaration))
            .ConfigureAwait(false));

        var selection = declaration.RootElement.GetProperty("FilingSelection");

        var shared = selection.GetProperty("Forms")
            .EnumerateArray()
            .Select(f => f.GetString() ?? string.Empty)
            .Where(f => f.Length > 0)
            .ToList();

        var windows = new Dictionary<string, List<(DateOnly From, DateOnly To)>>(StringComparer.Ordinal);

        foreach (var breach in selection.GetProperty("Breaches").EnumerateArray())
        {
            var cik = breach.GetProperty("Cik").GetString() ?? string.Empty;
            var from = Date(breach, "SelectFilingsFromUtc");
            var to = Date(breach, "SelectFilingsToUtc");

            if (cik.Length == 0 || from is not { } start || to is not { } end)
            {
                continue;
            }

            if (!windows.TryGetValue(cik, out var list))
            {
                list = [];
                windows[cik] = list;
            }

            list.Add((start, end));
        }

        var scoped = selection.GetProperty("PerMemberFormScope")
            .EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.Object)
            .ToList();

        var secondaryCik = scoped.Count == 1 ? scoped[0].Name : null;

        var secondaryForms = secondaryCik is null
            ? new List<string>()
            : scoped[0].Value.GetProperty("AdditionalForms")
                .EnumerateArray()
                .Select(f => f.GetString() ?? string.Empty)
                .Where(f => f.Length > 0)
                .ToList();

        return new SecFilingScope(shared, secondaryForms, secondaryCik, windows);
    }

    /// <summary>
    /// What earlier runs already spent against this authorisation, from the artefacts they wrote.
    /// </summary>
    /// <remarks>
    /// Scoped to the authorisation the artefact says it charged, exactly as the split runner scopes
    /// its own: an artefact naming a different authorisation belongs to a closed account, and one
    /// that names none is refused rather than counted as zero. Six SEC artefacts now exist - one per
    /// member, one unit each - so this answers six, the whole ceiling, and it answers six because it
    /// looked rather than because it assumed. It answered zero before any of them had run, for the
    /// same reason.
    /// </remarks>
    public static async Task<int> PriorConsumptionAsync(string authorizationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationId);

        var directory = Universe.RepositoryPath("artifacts", "universe");

        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var total = 0;

        foreach (var path in Directory.EnumerateFiles(directory, ArtefactPattern).Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(false));

            var root = document.RootElement;

            if (!root.TryGetProperty(AuthorizationProperty, out var named))
            {
                throw new InvalidOperationException(
                    $"`{Path.GetFileName(path)}` does not say which authorisation it charged. An " +
                    "artefact that cannot be attributed is not evidence of nothing; it is refused.");
            }

            if (!string.Equals(named.GetString(), authorizationId, StringComparison.Ordinal))
            {
                continue;
            }

            if (root.TryGetProperty(ConsumedProperty, out var consumed))
            {
                total += consumed.GetInt32();
            }
        }

        return total;
    }

    /// <summary>Every correlation an earlier recorded attempt already claimed.</summary>
    public static async Task<HashSet<string>> BurnedCorrelationsAsync()
    {
        var burned = new HashSet<string>(StringComparer.Ordinal);
        var directory = Universe.RepositoryPath("artifacts", "universe");

        if (!Directory.Exists(directory))
        {
            return burned;
        }

        foreach (var path in Directory.EnumerateFiles(directory, ArtefactPattern))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(false));

            if (document.RootElement.TryGetProperty("Correlation", out var correlation)
                && correlation.GetString() is { Length: > 0 } value)
            {
                burned.Add(value);
            }
        }

        return burned;
    }

    /// <summary>
    /// Builds the runner from the real container, and the context from the real declarations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every dependency comes out of the host the application actually composes - the same
    /// <c>IDataAcquisition</c>, the same archive, the same ledger, the same clock. That is what makes
    /// this wiring rather than a second framework: nothing is constructed here that the application
    /// does not construct for itself.
    /// </para>
    /// <para>
    /// <strong>The connector is resolved through <see cref="IProviderCatalogue"/>, and that is the
    /// whole configuration story.</strong> <c>AddSecEdgar</c> reads
    /// <c>Providers:SecEdgar:Enabled</c> while the container is being built and registers nothing at
    /// all when it is false, so a disabled connector arrives here as a null provider and the
    /// preflight refuses it under the gateway's own rule. There is no separate "is it enabled"
    /// question to ask, and no second rule for the same condition.
    /// </para>
    /// </remarks>
    public static async Task<(SecFilingsBatchRunner Runner, SecRunContext Context)> OpenAsync(
        IServiceProvider services,
        SecEdgarSixMemberPartition.SecFilingBatchDefinition batch,
        AcquisitionAuthorization authorization,
        DataSource? source,
        Func<string, string?> environment,
        int attemptNumber)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(environment);

        var runner = new SecFilingsBatchRunner(
            services.GetRequiredService<IDataAcquisition>(),
            services.GetRequiredService<IIngestionRunStore>(),
            services.GetRequiredService<IRawResponseArchive>(),
            services.GetRequiredService<IClock>());

        var scope = await ReadScopeAsync().ConfigureAwait(false);
        var windows = scope.WindowsFor(batch.Cik);

        var context = new SecRunContext(
            batch,
            authorization,
            await SealedCiksAsync().ConfigureAwait(false),
            source,

            // The connector, or null when configuration left it unregistered. This is the switch.
            services.GetRequiredService<IProviderCatalogue>().Find(SourceId.Create(SecFilingsBatchRunner.Source)),

            // Read, handed on, never recorded.
            environment(SecFilingsBatchRunner.ContactVariable),
            attemptNumber,
            scope.EarliestWindowStartFor(batch.Cik),
            windows,
            scope.SharedForms,
            scope.SecondaryForms,
            scope.SecondaryFormsCik,
            await BurnedCorrelationsAsync().ConfigureAwait(false));

        return (runner, context);
    }

    /// <summary>Where one attempt's immutable record is written.</summary>
    public static string ArtefactPathFor(int batchIndex, int attempt) =>
        Universe.RepositoryPath(
            "artifacts", "universe",
            Universe.Inv($"acquisition-sec-{batchIndex:00}-attempt-{attempt:00}.json"));

    private static DateOnly? Date(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateOnly.TryParseExact(
            value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
}

/// <summary>
/// The reading scope the installed declaration states, as the runner needs it.
/// </summary>
/// <param name="SharedForms">The forms every member may be read for.</param>
/// <param name="SecondaryForms">The extra forms one named member may be read for.</param>
/// <param name="SecondaryFormsCik">That member's CIK, or null when no member has extra scope.</param>
/// <param name="Windows">Each member's selection windows, by CIK.</param>
internal sealed record SecFilingScope(
    IReadOnlyList<string> SharedForms,
    IReadOnlyList<string> SecondaryForms,
    string? SecondaryFormsCik,
    IReadOnlyDictionary<string, List<(DateOnly From, DateOnly To)>> Windows)
{
    /// <summary>This member's selection windows, or none when the declaration states none.</summary>
    public IReadOnlyList<(DateOnly From, DateOnly To)> WindowsFor(string cik) =>
        Windows.TryGetValue(cik, out var windows) ? windows : [];

    /// <summary>
    /// The earliest date this member's windows begin at - the date the history has to reach back to.
    /// </summary>
    /// <remarks>
    /// A member with no stated window returns <see cref="DateOnly.MaxValue"/>, which no document can
    /// cover. Failing closed: a member whose scope the declaration does not state is not a member
    /// whose absence of filings means anything.
    /// </remarks>
    public DateOnly EarliestWindowStartFor(string cik)
    {
        var windows = WindowsFor(cik);

        return windows.Count == 0 ? DateOnly.MaxValue : windows.Min(w => w.From);
    }
}
