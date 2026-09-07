using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Domain.ValueObjects;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The pre-flight for resuming: what is left, what may be spent, and the gap between them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Read-only. No provider is called and no connector is enabled.</strong> Every request is
/// built exactly as a resumed acquisition would build it and then discarded; the ledger is asked
/// about each fingerprint and nothing is dispatched. Run and observation counts are asserted
/// unchanged, and the sealed manifest and identity table are checked against their approved values.
/// </para>
/// <para>
/// The numbers this recomputes were last stated from a report. A report is a claim; this is the
/// store. They have disagreed before in this repository, which is why it is worth the minute.
/// </para>
/// </remarks>
public sealed class ResumeReadinessTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_RESUME_READINESS";

    private const string SealedFingerprint =
        "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private const string SealedDigest =
        "c3667215b1e28c2093ed430e7ade180c40dfe616aef1f8d64e485721938f68db";

    private const int SealedBytes = 245126;

    private const string PriceSource = "eodhd-eod";
    private const string ActionsSource = "eodhd-splits";

    /// <summary>The batch the next stage would run, if one is authorised.</summary>
    private const int RecommendedBatch = 70;

    private static readonly DateTime WindowStart = new(2021, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public ResumeReadinessTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task What_remains_and_what_may_be_spent_are_both_recomputed_from_the_store()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The resume readiness check is off. Set {GateVariable}=1 to run it. It reads only.");

        var report = new StringBuilder();

        using var scope = _factory.Services.CreateScope();

        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<AppDbContext>();
        var clock = services.GetRequiredService<IClock>();
        var runStore = services.GetRequiredService<IIngestionRunStore>();

        var runsBefore = await context.IngestionRuns.AsNoTracking().CountAsync();
        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();

        var authorization = AcquisitionAuthorization.Load(
            Universe.RepositoryPath("declarations", "acquisition-eodhd-sample400.json"),
            SealedFingerprint);

        using var outcome = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("artifacts", "universe", "acquisition-outcome.json")));

        var consumed = outcome.RootElement.GetProperty("AuthorizationConsumed").GetInt32();
        var available = authorization.DispatchCeiling - consumed;

        // ---- what a resumed run would do, request by request ------------------------------------

        var members = await ReadySymbolsAsync();

        var priorPriceRun = await context.IngestionRuns
            .AsNoTracking()
            .Where(r => r.Request.Category == DataCategory.MarketPrices)
            .OrderBy(r => r.StartedAtUtc)
            .LastOrDefaultAsync();

        var subjectKind = priorPriceRun?.Request.Subject.Kind ?? "Security";
        var region = priorPriceRun?.Request.Region ?? Region.Global;

        var planned = 0;
        var suppressible = 0;
        var outstandingPrices = new List<string>();
        var outstandingSplits = 0;

        foreach (var (source, category) in new[]
        {
            (PriceSource, DataCategory.MarketPrices),
            (ActionsSource, DataCategory.CorporateActions),
        })
        {
            foreach (var symbol in members)
            {
                var request = IngestionRequest.Create(
                    SourceId.Create(source),
                    category,
                    region,
                    IngestionSubject.Create(subjectKind, symbol),
                    CorrelationId.Create(Universe.Inv($"readiness-{planned}")),
                    clock.UtcNow,
                    DateRange.Create(WindowStart, WindowEnd));

                planned++;

                if (await runStore.HasCompletedAsync(request.Fingerprint()))
                {
                    suppressible++;
                }
                else if (source == PriceSource)
                {
                    outstandingPrices.Add(symbol);
                }
                else
                {
                    outstandingSplits++;
                }
            }
        }

        var outstanding = outstandingPrices.Count + outstandingSplits;
        var shortfall = outstanding - available;

        // ---- the report --------------------------------------------------------------------------

        report.AppendLine("# Resume readiness");
        report.AppendLine();
        report.AppendLine("**Read-only. No provider was called.** Every request below was built as a");
        report.AppendLine("resumed run would build it, checked against the ledger, and discarded.");
        report.AppendLine();

        report.AppendLine("## What remains");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Planned by the authorisation | {planned} |"));
        report.AppendLine(Universe.Inv($"| **Suppressible by the ledger today** | **{suppressible}** |"));
        report.AppendLine(Universe.Inv($"| **Outstanding** | **{outstanding}** |"));
        report.AppendLine(Universe.Inv($"| …prices (`{PriceSource}`) | {outstandingPrices.Count} |"));
        report.AppendLine(Universe.Inv($"| …splits (`{ActionsSource}`), a later phase | {outstandingSplits} |"));
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Authorised ceiling | {authorization.DispatchCeiling} |"));
        report.AppendLine(Universe.Inv($"| Consumed by the interrupted run | {consumed} |"));
        report.AppendLine(Universe.Inv($"| **Available now** | **{available}** |"));
        report.AppendLine(Universe.Inv($"| **Shortfall against the outstanding work** | **{shortfall}** |"));
        report.AppendLine();
        report.AppendLine(Universe.Inv(
            $"The shortfall is exactly the {shortfall} requests that were dispatched and failed: a dispatch that fails still spent its authorisation, because the call was made. Prices alone need {outstandingPrices.Count} against {available} available, so **the price phase fits; the splits phase does not.**"));
        report.AppendLine();

        // ---- the batches a resumed run would take ---------------------------------------------------

        var batches = (outstandingPrices.Count + RecommendedBatch - 1) / RecommendedBatch;

        report.AppendLine(Universe.Inv($"## The {batches} price batches, at {RecommendedBatch} a batch"));
        report.AppendLine();
        report.AppendLine("| Batch | Symbols | First | Last |");
        report.AppendLine("| ---: | ---: | --- | --- |");

        for (var i = 0; i < batches; i++)
        {
            var slice = outstandingPrices.Skip(i * RecommendedBatch).Take(RecommendedBatch).ToList();

            report.AppendLine(Universe.Inv(
                $"| {i + 1} | {slice.Count} | `{slice[0]}` | `{slice[^1]}` |"));
        }

        report.AppendLine();
        report.AppendLine("Each batch is one process. The ledger is the only state carried between");
        report.AppendLine("them, so a batch that stops early costs nothing but the requests it made,");
        report.AppendLine("and the next one suppresses whatever the last completed.");
        report.AppendLine();

        // ---- the guardrails, checked rather than described --------------------------------------------

        var unauthorised = outstandingPrices
            .Where(s => !authorization.Symbols.Contains(s))
            .ToList();

        report.AppendLine("## Guardrails");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Outstanding price symbols outside the authorised 355 | **{unauthorised.Count}** |"));
        report.AppendLine(Universe.Inv($"| Window on every request | {WindowStart:yyyy-MM-dd} to {WindowEnd:yyyy-MM-dd} |"));
        report.AppendLine(Universe.Inv($"| Subject kind / region | `{subjectKind}` / `{region.Code}` |"));
        report.AppendLine(Universe.Inv($"| Runs before / after this check | {runsBefore} / {await context.IngestionRuns.AsNoTracking().CountAsync()} |"));
        report.AppendLine();

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "resume-readiness.md"),
            report.ToString());

        _output.WriteLine(report.ToString());

        // ---- nothing moved ------------------------------------------------------------------------------

        var manifestBytes = await File.ReadAllBytesAsync(Universe.RepositoryPath(
            "declarations", "universe-sample400-2021-2026.json"));

        Assert.Equal(
            SealedDigest,
            Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant());

        Assert.True(manifestBytes.Length == SealedBytes, "the sealed manifest's length changed");
        Assert.True(members.Count == 355, Universe.Inv($"{members.Count} ready symbols, not 355"));

        Assert.Equal(runsBefore, await context.IngestionRuns.AsNoTracking().CountAsync());
        Assert.Equal(observationsBefore, await context.Observations.AsNoTracking().CountAsync());

        Assert.True(planned == 710, Universe.Inv($"{planned} planned, not 710"));
        Assert.Empty(unauthorised);

        // The authorisation was neither consumed nor amended by looking at it.
        Assert.Equal(0, authorization.Consumed);
        Assert.Equal(704, authorization.DispatchCeiling);

        // The finding that governs the next decision: there is not enough left to finish.
        Assert.True(
            shortfall > 0,
            Universe.Inv($"the shortfall is {shortfall}; if it is no longer positive this report is stale"));

        Assert.True(
            outstandingPrices.Count <= available,
            Universe.Inv($"prices alone need {outstandingPrices.Count} against {available} available"));
    }

    private static async Task<List<string>> ReadySymbolsAsync()
    {
        using var identity = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("artifacts", "universe", "identity-sample400.json")));

        return identity.RootElement
            .GetProperty("Members")
            .EnumerateArray()
            .Where(m => m.TryGetProperty("Ready", out var r) && r.ValueKind == JsonValueKind.True)
            .Select(m => AcquisitionPlanning.SymbolFor(m.GetProperty("Ticker").GetString()!))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }
}
