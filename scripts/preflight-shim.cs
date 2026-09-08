using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Application.Normalization;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Ingestion;
using AI.Investment.Infrastructure.Persistence;
using AI.Investment.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>
/// Write authorisation that never authorises anything.
/// </summary>
/// <remarks>
/// The persistence layer consults this before committing. Supplying a permissive one would have
/// made "nothing was written" a claim about this code's intentions; supplying this one makes it a
/// property of the context: a SaveChanges on this session throws, and an attempt to open an
/// authorisation window throws before it can be opened.
/// </remarks>
public sealed class PreflightDenyAllWrites : IWriteAuthorization
{
    public bool IsAuthorized { get { return false; } }

    public Guid? AuthorizingDecisionId { get { return null; } }

    public IDisposable Authorize(PolicyDecision decision)
    {
        throw new InvalidOperationException(
            "READ-ONLY PREFLIGHT: an authorisation window was requested. Refused.");
    }
}

/// <summary>
/// A normalisation pipeline that refuses to run and remembers that it was asked.
/// </summary>
/// <remarks>
/// The replay service is constructed with this so that "the ambiguous payload never reaches
/// normalisation" is demonstrated rather than asserted. If the service's ordering were ever
/// changed so that it normalised before checking cardinality, this throws.
/// </remarks>
public sealed class PreflightRefusingPipeline : INormalizationPipeline
{
    public bool WasCalled { get; private set; }

    public Task<NormalizationSummary> NormalizeAsync(IngestionRun run, CancellationToken cancellationToken = default)
    {
        WasCalled = true;

        throw new InvalidOperationException(
            "READ-ONLY PREFLIGHT: normalisation was attempted. Refused.");
    }
}

public static class ReplayPreflight
{
    public static string Run(
        string connectionString,
        string archiveRoot,
        string[] symbols,
        string[] hashes,
        string[] expectedRunIds,
        string sharedHash)
    {
        var report = new Dictionary<string, object>();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        using (var context = new AppDbContext(options, new PreflightDenyAllWrites()))
        {
            // ---- database identity, from the live connection rather than from configuration ----
            var connection = context.Database.GetDbConnection();
            connection.Open();

            var identity = new Dictionary<string, object>();
            identity["Database"] = connection.Database;
            identity["DataSource"] = connection.DataSource;
            identity["ServerVersion"] = connection.ServerVersion;

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT current_database() || '|' || current_schema() || '|' || version()";
                var parts = Convert.ToString(cmd.ExecuteScalar()).Split('|');
                identity["CurrentDatabase"] = parts.Length > 0 ? parts[0] : "";
                identity["CurrentSchema"] = parts.Length > 1 ? parts[1] : "";
                identity["Version"] = parts.Length > 2 ? parts[2] : "";
            }

            report["Identity"] = identity;

            // Refuse the test database outright, before anything else is read.
            if (string.Equals(connection.Database, "ai_investment_tests", StringComparison.OrdinalIgnoreCase))
            {
                report["Refused"] = "the connection resolved to ai_investment_tests, which holds no evidence";
                return JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            }

            // ---- before counts ----
            report["Before"] = Counts(context);

            var archive = new FileSystemRawResponseArchive(
                Options.Create(new RawArchiveOptions { RootPath = archiveRoot }));

            var lookup = new EfIngestionRunStore(context);
            var pipeline = new PreflightRefusingPipeline();
            var service = new ArchivedPayloadReplayService(lookup, archive, pipeline);

            report["ArchiveRoot"] = archiveRoot;

            // ---- the five targets ----
            var targets = new List<Dictionary<string, object>>();

            for (var i = 0; i < hashes.Length; i++)
            {
                targets.Add(Examine(context, lookup, archive, symbols[i], hashes[i], expectedRunIds[i]));
            }

            report["Targets"] = targets;

            // ---- the shared empty array, through the actual service ----
            var shared = new Dictionary<string, object>();
            var sharedContentHash = ContentHash.Create(sharedHash);

            var sharedRuns = lookup.RunsForArchivedPayloadAsync(sharedContentHash).GetAwaiter().GetResult();

            shared["ContentHash"] = sharedHash;
            shared["RunsReturned"] = sharedRuns.Count;
            shared["PriceRuns"] = sharedRuns.Count(r =>
                string.Equals(r.Request.SourceId.Value, "eodhd-eod", StringComparison.Ordinal));
            shared["PriceRunSubjects"] = sharedRuns
                .Where(r => string.Equals(r.Request.SourceId.Value, "eodhd-eod", StringComparison.Ordinal))
                .Select(r => r.Request.Subject.Identifier)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

            var sharedResult = service.ReplayAsync(sharedContentHash).GetAwaiter().GetResult();

            shared["ServiceStatus"] = sharedResult.Status.ToString();
            shared["ServiceChoseARun"] = sharedResult.RunId.HasValue;
            shared["ServiceRecordedObservations"] = sharedResult.RecordedObservations;
            shared["ObservationsRecorded"] = sharedResult.ObservationsRecorded;
            shared["NormalizationWasAttempted"] = pipeline.WasCalled;
            shared["Reason"] = sharedResult.Reason;

            report["SharedEmptyArray"] = shared;

            // ---- determinism: the same question twice ----
            var first = lookup.RunsForArchivedPayloadAsync(sharedContentHash).GetAwaiter().GetResult()
                .Select(r => r.Id.Value.ToString()).ToArray();
            var second = lookup.RunsForArchivedPayloadAsync(sharedContentHash).GetAwaiter().GetResult()
                .Select(r => r.Id.Value.ToString()).ToArray();

            report["LookupIsDeterministic"] = first.SequenceEqual(second, StringComparer.Ordinal);

            // ---- after counts ----
            report["After"] = Counts(context);
            report["NormalizationEverAttempted"] = pipeline.WasCalled;

            connection.Close();
        }

        return JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    }

    private static Dictionary<string, object> Counts(AppDbContext context)
    {
        var counts = new Dictionary<string, object>();
        counts["observations"] = context.Observations.AsNoTracking().LongCount();
        counts["ingestion_runs"] = context.IngestionRuns.AsNoTracking().LongCount();
        counts["processed_actions"] = context.ProcessedActions.AsNoTracking().LongCount();
        counts["quarantined_payloads"] = context.QuarantinedPayloads.AsNoTracking().LongCount();

        return counts;
    }

    private static Dictionary<string, object> Examine(
        AppDbContext context,
        EfIngestionRunStore lookup,
        FileSystemRawResponseArchive archive,
        string symbol,
        string hash,
        string expectedRunId)
    {
        var row = new Dictionary<string, object>();
        row["Symbol"] = symbol;
        row["ContentHash"] = hash;
        row["ExpectedRunId"] = expectedRunId;

        // 1. Resolve through the actual lookup path - the same call the replay service makes. This
        //    exercises the jsonb containment SQL, IngestionRunId.Create, EF materialisation of the
        //    run and its owned request, and the in-memory ordering, all in production code.
        var contentHash = ContentHash.Create(hash);
        var runs = lookup.RunsForArchivedPayloadAsync(contentHash).GetAwaiter().GetResult();

        row["RunsReturned"] = runs.Count;
        row["ResolvesToExactlyOneRun"] = runs.Count == 1;

        if (runs.Count != 1)
        {
            row["Materialised"] = false;
            row["AllPreconditionsPass"] = false;

            return row;
        }

        var run = runs[0];

        // 2. Materialisation. Reading these at all is the proof: every one comes from an owned type
        //    EF had to map back out of the row.
        row["Materialised"] = true;
        row["RunId"] = run.Id.Value.ToString();
        row["RunIdMatchesExpected"] = string.Equals(
            run.Id.Value.ToString(), expectedRunId, StringComparison.OrdinalIgnoreCase);
        row["SourceId"] = run.Request.SourceId.Value;
        row["Category"] = run.Request.Category.ToString();
        row["Region"] = run.Request.Region.ToString();
        row["SubjectKind"] = run.Request.Subject.Kind;
        row["SubjectIdentifier"] = run.Request.Subject.Identifier;
        row["Outcome"] = run.Outcome.ToString();
        row["StartedAtUtc"] = run.StartedAtUtc.ToString("o");
        row["CompletedAtUtc"] = run.CompletedAtUtc.HasValue ? run.CompletedAtUtc.Value.ToString("o") : null;
        row["RequestFingerprint"] = run.Request.Fingerprint();
        row["ArtifactCount"] = run.Artifacts.Count;
        row["Artifacts"] = run.Artifacts.Select(a => a.Value).ToArray();

        row["SourceIsEodhdEod"] = string.Equals(run.Request.SourceId.Value, "eodhd-eod", StringComparison.Ordinal);
        row["CategoryIsMarketPrices"] = string.Equals(run.Request.Category.ToString(), "MarketPrices", StringComparison.Ordinal);
        row["SubjectMatchesSymbol"] = string.Equals(run.Request.Subject.Identifier, symbol, StringComparison.Ordinal);
        row["ExactlyOneArtifact"] = run.Artifacts.Count == 1;
        row["ArtifactIsTheExpectedPayload"] =
            run.Artifacts.Count == 1 && string.Equals(run.Artifacts[0].Value, hash, StringComparison.OrdinalIgnoreCase);
        row["OutcomeIsSucceeded"] = run.Outcome == IngestionOutcome.Succeeded;

        // 3. The archive precondition the service checks before it calls the pipeline: every
        //    artifact must still be describable, or the run is refused rather than quarantined.
        var described = true;
        var descriptions = new List<Dictionary<string, object>>();

        foreach (var artifact in run.Artifacts)
        {
            var payload = archive.DescribeAsync(artifact).GetAwaiter().GetResult();

            if (payload == null)
            {
                described = false;
                descriptions.Add(new Dictionary<string, object> { { "Artifact", artifact.Value }, { "Described", false } });

                continue;
            }

            var d = new Dictionary<string, object>();
            d["Artifact"] = artifact.Value;
            d["Described"] = true;
            d["SourceId"] = payload.SourceId.Value;
            d["MediaType"] = payload.MediaType;
            d["RetrievedAtUtc"] = payload.RetrievedAtUtc.ToString("o");
            d["ByteLength"] = payload.ByteLength;
            descriptions.Add(d);
        }

        row["ArchiveDescribesEveryArtifact"] = described;
        row["ArchivedPayloads"] = descriptions;

        // 4. Zero observations for this subject today, which is what leaves the seam's key free.
        var subject = run.Request.Subject.Identifier;
        var observations = context.Observations
            .AsNoTracking()
            .Count(o => o.Subject.Identifier == subject);

        row["ObservationsHeldForSubject"] = observations;
        row["HoldsNoObservations"] = observations == 0;

        // 5. The idempotency key the pipeline would use, and whether the seam has already claimed it.
        var key = "normalization.record:" + run.Id;
        var claimed = context.ProcessedActions.AsNoTracking().Any(p => p.IdempotencyKey == key);

        row["IdempotencyKey"] = key;
        row["IdempotencyKeyClaimed"] = claimed;
        row["IdempotencyKeyIsFree"] = !claimed;

        row["AllPreconditionsPass"] =
            runs.Count == 1
            && (bool)row["RunIdMatchesExpected"]
            && (bool)row["SourceIsEodhdEod"]
            && (bool)row["CategoryIsMarketPrices"]
            && (bool)row["SubjectMatchesSymbol"]
            && (bool)row["ExactlyOneArtifact"]
            && (bool)row["ArtifactIsTheExpectedPayload"]
            && (bool)row["OutcomeIsSucceeded"]
            && described
            && observations == 0
            && !claimed;

        return row;
    }
}
