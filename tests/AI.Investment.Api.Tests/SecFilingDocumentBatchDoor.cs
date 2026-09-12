using System.Globalization;
using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Domain.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The execution door for one filing-document batch. It is wired, and it is shut.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Four locks, and the door opens only when all four are drawn back.</strong> The gate
/// variable must be set and must name a batch; that batch must be marked <c>Authorised</c> in the
/// partition; the sealed authorisation must cover the exact documents and still be in date; and the
/// connector must be enabled with a deployment identity. Each is a separate deliberate act and each
/// refusal costs nothing.
/// </para>
/// <para>
/// The same shape as <see cref="SecFilingsBatchDoor"/>, which does this for the submissions
/// category. Nothing here is a second dispatch mechanism: it assembles a context and hands it to
/// the runner, which uses the production acquisition path.
/// </para>
/// </remarks>
internal static class SecFilingDocumentBatchDoor
{
    /// <summary>Set to <c>1</c> to arm the door. Absent on every ordinary run.</summary>
    public const string GateVariable = "AIINV_FILING_DOC_BATCH";

    /// <summary>Which batch. There is no default: a run that does not say must not pick one.</summary>
    public const string IndexVariable = "AIINV_FILING_DOC_BATCH_INDEX";

    /// <summary>Set to <c>1</c> to reconcile the registry row. A separate act from dispatching.</summary>
    public const string AdmitVariable = "AIINV_FILING_DOC_ADMIT";

    public const string SealedFingerprint = "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    /// <summary>The digest approved at F6c, restated as a literal so a changed file is visible.</summary>
    public const string ApprovedDigest =
        "f927ab5d5059a25379a20fd731efece5c192f0d6aba7d255eb5ac038d3d07a3b";

    public const string ArtefactPattern = "acquisition-filing-doc-*.json";

    public static bool IsOpen(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return string.Equals(environment(GateVariable), "1", StringComparison.Ordinal);
    }

    public static bool AdmissionRequested(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return string.Equals(environment(AdmitVariable), "1", StringComparison.Ordinal);
    }

    /// <summary>The batch the environment names, or null when it names none that exists.</summary>
    public static SecEdgarFilingDocumentPartition.FilingDocumentBatchDefinition? Batch(
        Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (!int.TryParse(
                environment(IndexVariable),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var index))
        {
            return null;
        }

        return SecEdgarFilingDocumentPartition.Batches.SingleOrDefault(b => b.Index == index);
    }

    /// <summary>Loads the sealed authorisation, digest and declaration binding verified.</summary>
    public static AcquisitionAuthorization LoadAuthorization() =>
        AcquisitionAuthorization.Load(
            Universe.RepositoryPath("declarations", SecEdgarFilingDocumentPartition.Declaration),
            SealedFingerprint,
            Universe.RepositoryPath());

    /// <summary>
    /// What earlier processes already spent against this authorisation, read from their artefacts.
    /// </summary>
    /// <remarks>
    /// <strong>The ceiling belongs to the authorisation, not to a process.</strong> Each run loads
    /// the declaration afresh with a counter at zero, so without this every batch would believe it
    /// had the whole ceiling left and the approved total would be spendable once per batch. What was
    /// spent is read from the outcome artefacts earlier runs wrote and charged before anything is
    /// dispatched.
    /// </remarks>
    public static async Task<int> PriorConsumptionAsync()
    {
        var directory = Universe.RepositoryPath("artifacts", "universe");

        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var spent = 0;

        foreach (var path in Directory.EnumerateFiles(directory, ArtefactPattern))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(false));

            if (document.RootElement.TryGetProperty("ConsumedThisRun", out var consumed)
                && consumed.TryGetInt32(out var value))
            {
                spent += value;
            }
        }

        return spent;
    }

    /// <summary>Correlations earlier recorded attempts already claimed.</summary>
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

            if (!document.RootElement.TryGetProperty("Targets", out var targets)
                || targets.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var target in targets.EnumerateArray())
            {
                if (target.TryGetProperty("Correlation", out var correlation)
                    && correlation.GetString() is { } text)
                {
                    burned.Add(text);
                }
            }
        }

        return burned;
    }

    /// <summary>How many attempts at this batch have already been recorded, plus one.</summary>
    public static int AttemptNumber(int batchIndex)
    {
        var directory = Universe.RepositoryPath("artifacts", "universe");

        if (!Directory.Exists(directory))
        {
            return 1;
        }

        var mine = Universe.Inv($"acquisition-filing-doc-{batchIndex:00}-attempt-");
        var attempts = Directory
            .EnumerateFiles(directory, ArtefactPattern)
            .Count(p => Path.GetFileName(p).StartsWith(mine, StringComparison.Ordinal));

        return attempts + 1;
    }

    public static string ArtefactPathFor(int batchIndex, int attempt) =>
        Universe.RepositoryPath(
            "artifacts", "universe",
            Universe.Inv($"acquisition-filing-doc-{batchIndex:00}-attempt-{attempt:00}.json"));

    /// <summary>
    /// Assembles the runner and its context from the host the application actually composes.
    /// </summary>
    /// <remarks>
    /// Every service comes out of the real container, so a batch cannot run against a hand-built
    /// gateway, a stub archive or a policy engine that permits everything. The targets come from the
    /// sealed declaration through the partition, never from a query.
    /// </remarks>
    public static async Task<(SecFilingDocumentBatchRunner Runner, FilingDocumentRunContext Context)> OpenAsync(
        IServiceProvider services,
        SecEdgarFilingDocumentPartition.FilingDocumentBatchDefinition batch,
        AcquisitionAuthorization authorization,
        Func<string, string?> environment,
        int attempt)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(environment);

        var runner = new SecFilingDocumentBatchRunner(
            services.GetRequiredService<IDataAcquisition>(),
            services.GetRequiredService<IIngestionRunStore>(),
            services.GetRequiredService<IRawResponseArchive>(),
            services.GetRequiredService<IClock>());

        var source = await services
            .GetRequiredService<ISourceRegistry>()
            .GetByIdAsync(SourceId.Create(SecFilingDocumentBatchRunner.Source))
            .ConfigureAwait(false);

        var provider = services
            .GetRequiredService<IProviderCatalogue>()
            .Find(SourceId.Create(SecFilingDocumentBatchRunner.Source));

        var context = new FilingDocumentRunContext(
            batch,
            SecEdgarFilingDocumentPartition.TargetsFor(batch.Index),
            authorization,
            SecEdgarFilingDocumentPartition.Declaration,
            SecEdgarFilingDocumentPartition.PilotDeclarationPath,
            source,
            provider,
            environment(SecFilingDocumentBatchRunner.ContactVariable),
            attempt,
            await BurnedCorrelationsAsync().ConfigureAwait(false));

        return (runner, context);
    }
}
