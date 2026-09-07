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
/// Installs the authorisation the split work would need, and cuts its batches. Spends nothing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No provider is called and no split request is dispatched.</strong> This stage reads the
/// ledger, writes one declaration, and writes one artefact describing the partition. Run,
/// observation and quarantine counts are asserted unchanged either side.
/// </para>
/// <para>
/// <strong>What it writes, and why that is not an approval.</strong> A declaration in
/// <c>declarations/</c> is a ceiling, not a licence: it states the most that <em>could</em> be
/// spent if somebody approved a batch. Nothing dispatches until a batch in
/// <see cref="AcquisitionSplitBatchTests.Partition"/> is marked authorised, and every batch this
/// stage describes is marked unauthorised. The two controls are deliberately separate - a budget
/// that existed only at the moment of approval would have to be re-approved to be audited.
/// </para>
/// <para>
/// <strong>Why the successor is written rather than the original amended.</strong> The price
/// authorisation is the record of what was approved and what it cost, and editing it would destroy
/// exactly the evidence it exists to hold. It is left byte-identical, and this stage asserts that.
/// The successor names it and carries its final spend forward as evidence - both digest-covered
/// under <see cref="AcquisitionAuthorization.SupersedingSchema"/>, so neither can be rewritten
/// without the file refusing to load.
/// </para>
/// </remarks>
public sealed class SplitAuthorizationTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_SPLIT_AUTHORIZATION";

    private const string SealedFingerprint =
        "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private const string SealedDigest =
        "c3667215b1e28c2093ed430e7ade180c40dfe616aef1f8d64e485721938f68db";

    private const string PriceDeclaration = "acquisition-eodhd-sample400.json";
    private const string SplitDeclaration = "acquisition-eodhd-splits-sample400.json";

    private const string SplitSource = "eodhd-splits";
    private const string DefaultSecurityKind = "Security";

    /// <summary>How many symbols a split batch holds, before the remainder.</summary>
    private const int BatchSize = 70;

    private static readonly DateTime WindowStart = new(2021, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public SplitAuthorizationTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_successor_authorisation_is_installed_and_its_batches_are_cut()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The split authorisation preparation is off. Set {GateVariable}=1 to run it. It "
            + "dispatches nothing.");

        var report = new StringBuilder();

        using var scope = _factory.Services.CreateScope();

        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<AppDbContext>();
        var clock = services.GetRequiredService<IClock>();
        var runStore = services.GetRequiredService<IIngestionRunStore>();

        var runsBefore = await context.IngestionRuns.AsNoTracking().CountAsync();
        var observationsBefore = await context.Observations.AsNoTracking().CountAsync();
        var quarantineBefore = await context.QuarantinedPayloads.AsNoTracking().CountAsync();

        // ---- the universe and the authorisation being superseded ---------------------------------

        var manifestPath = Universe.RepositoryPath(
            "declarations", "universe-sample400-2021-2026.json");

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);

        Assert.Equal(
            SealedDigest,
            Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant());

        var pricePath = Universe.RepositoryPath("declarations", PriceDeclaration);
        var priceBefore = await File.ReadAllBytesAsync(pricePath);
        var superseded = AcquisitionAuthorization.Load(pricePath, SealedFingerprint);

        Assert.Equal(AcquisitionAuthorization.Schema, superseded.SchemaVersion);

        var consumed = await ConsumedAsync();

        // ---- the outstanding split work, recomputed from the live ledger --------------------------

        var members = await ReadyMembersAsync();

        Assert.True(members.Count == 355, Universe.Inv($"{members.Count} ready members, not 355"));

        var priorRun = await context.IngestionRuns
            .AsNoTracking()
            .Where(r => r.Request.Category == DataCategory.CorporateActions)
            .OrderBy(r => r.StartedAtUtc)
            .LastOrDefaultAsync();

        var subjectKind = priorRun?.Request.Subject.Kind ?? DefaultSecurityKind;
        var region = priorRun?.Request.Region ?? Region.Global;

        var outstanding = new List<string>();
        var satisfied = new List<string>();
        var probe = 0;

        foreach (var member in members)
        {
            var request = IngestionRequest.Create(
                SourceId.Create(SplitSource),
                DataCategory.CorporateActions,
                region,
                IngestionSubject.Create(subjectKind, member.Symbol),
                CorrelationId.Create(Universe.Inv($"split-plan-{probe++}")),
                clock.UtcNow,
                DateRange.Create(WindowStart, WindowEnd));

            if (await runStore.HasCompletedAsync(request.Fingerprint()))
            {
                satisfied.Add(member.Symbol);
            }
            else
            {
                outstanding.Add(member.Symbol);
            }
        }

        var symbols = members.Select(m => m.Symbol).Order(StringComparer.Ordinal).ToList();

        // ---- the successor, digested with the production algorithm --------------------------------

        var digest = AcquisitionAuthorization.DigestFor(
            AcquisitionAuthorization.SupersedingSchema,
            SealedFingerprint,
            superseded.Vendor,
            [SplitSource],
            DateOnly.FromDateTime(WindowStart),
            DateOnly.FromDateTime(WindowEnd),
            symbols.Count,
            satisfied.Count,
            symbols.Count - satisfied.Count,
            symbols,
            superseded.AuthorizationId,
            consumed);

        var content = JsonSerializer.Serialize(
            new
            {
                Schema = AcquisitionAuthorization.SupersedingSchema,
                AuthorizationId = "eodhd-sample400-splits-2021-09-to-2026-08",
                EvidenceBaseFingerprint = SealedFingerprint,
                SupersedesAuthorizationId = superseded.AuthorizationId,
                SupersedesAuthorizationDigest = superseded.Digest,
                AlreadyConsumed = consumed,
                AlreadyConsumedNote = "Spent under the superseded authorisation and never credited "
                    + "back. Evidence only: this ceiling is sized to the work that remains, not "
                    + "inherited from the budget before it.",
                Note = "A ceiling, not a licence. No request is dispatched until a batch in the "
                    + "split runner's partition is marked authorised, and none is.",
                Vendor = superseded.Vendor,
                Sources = new[] { SplitSource },
                Endpoints = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SplitSource] = "api/splits/{symbol}",
                },
                Categories = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SplitSource] = nameof(DataCategory.CorporateActions),
                },
                WindowFromUtc = Universe.Inv($"{WindowStart:yyyy-MM-dd}"),
                WindowToUtc = Universe.Inv($"{WindowEnd:yyyy-MM-dd}"),
                SubjectKind = subjectKind,
                Region = region.Code,
                PlannedRequests = symbols.Count,
                AlreadySatisfied = satisfied.Count,
                DispatchCeiling = symbols.Count - satisfied.Count,
                AuthorizationDigest = digest,
                Symbols = symbols,
            },
            Universe.Json) + "\n";

        // Validated somewhere harmless first. Only a declaration the production loader accepts is
        // allowed anywhere near declarations/, so a bad digest or bad arithmetic never lands there.
        var staging = Path.Combine(Path.GetTempPath(), $"split-authorization-{Guid.NewGuid():N}.json");

        AcquisitionAuthorization candidate;

        try
        {
            await File.WriteAllTextAsync(staging, content);

            candidate = AcquisitionAuthorization.Load(staging, SealedFingerprint);
        }
        finally
        {
            File.Delete(staging);
        }

        Assert.Equal(AcquisitionAuthorization.SupersedingSchema, candidate.SchemaVersion);
        Assert.Equal(superseded.AuthorizationId, candidate.SupersedesAuthorizationId);
        Assert.Equal(consumed, candidate.AlreadyConsumed);
        Assert.Equal(outstanding.Count, candidate.DispatchCeiling);

        var installedPath = Universe.RepositoryPath("declarations", SplitDeclaration);

        await Universe.WriteAsync(installedPath, content);

        var installed = AcquisitionAuthorization.Load(installedPath, SealedFingerprint);

        // ---- the six batches, cut from the outstanding work ---------------------------------------

        var batches = new List<(int Index, string First, string Last, int Count)>();

        for (var i = 0; i * BatchSize < outstanding.Count; i++)
        {
            var slice = outstanding.Skip(i * BatchSize).Take(BatchSize).ToList();

            batches.Add((i + 1, slice[0], slice[^1], slice.Count));
        }

        // ---- the report ----------------------------------------------------------------------------

        report.AppendLine("# The split authorisation, installed; its batches, cut");
        report.AppendLine();
        report.AppendLine("**No provider was called and no split request was dispatched.** One");
        report.AppendLine("declaration was written and one artefact. The price authorisation was read");
        report.AppendLine("and left byte-identical, and every batch below is unauthorised.");
        report.AppendLine();

        report.AppendLine("## The successor");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| File | `declarations/{SplitDeclaration}` |"));
        report.AppendLine(Universe.Inv($"| Schema | `{installed.SchemaVersion}` |"));
        report.AppendLine(Universe.Inv($"| Authorisation id | `{installed.AuthorizationId}` |"));
        report.AppendLine(Universe.Inv($"| Supersedes | `{installed.SupersedesAuthorizationId}` |"));
        report.AppendLine(Universe.Inv($"| …its digest | `{superseded.Digest}` |"));
        report.AppendLine(Universe.Inv($"| Already consumed under it, as evidence | **{installed.AlreadyConsumed}** |"));
        report.AppendLine(Universe.Inv($"| Source | `{string.Join(", ", installed.Sources)}` |"));
        report.AppendLine(Universe.Inv($"| Window | {installed.WindowFrom:yyyy-MM-dd}..{installed.WindowTo:yyyy-MM-dd} |"));
        report.AppendLine(Universe.Inv($"| Symbols | {installed.Symbols.Count} |"));
        report.AppendLine(Universe.Inv($"| Planned | {installed.PlannedRequests} |"));
        report.AppendLine(Universe.Inv($"| Already satisfied in the ledger | {installed.AlreadySatisfied} |"));
        report.AppendLine(Universe.Inv($"| **Dispatch ceiling** | **{installed.DispatchCeiling}** |"));
        report.AppendLine(Universe.Inv($"| Digest | `{installed.Digest}` |"));
        report.AppendLine();
        report.AppendLine("The two supersession fields are inside the digest under this schema, so");
        report.AppendLine("rewriting either makes the file refuse to load rather than quietly lie.");
        report.AppendLine();

        report.AppendLine(Universe.Inv($"## The {batches.Count} batches, all unauthorised"));
        report.AppendLine();
        report.AppendLine("| Batch | Symbols | First | Last |");
        report.AppendLine("| ---: | ---: | --- | --- |");

        foreach (var (i, first, last, count) in batches)
        {
            report.AppendLine(Universe.Inv($"| {i} | {count} | `{first}` | `{last}` |"));
        }

        report.AppendLine();
        report.AppendLine("The partition table the split runner must carry, so it can be checked");
        report.AppendLine("against this report rather than transcribed on trust:");
        report.AppendLine();
        report.AppendLine("```csharp");

        foreach (var (i, first, last, count) in batches)
        {
            report.AppendLine(Universe.Inv(
                $"new({i}, \"{first}\", \"{last}\", {count}, {outstanding.Count}, 0, SplitDeclaration, Authorised: false),"));
        }

        report.AppendLine("```");
        report.AppendLine();
        report.AppendLine("The expected prior consumption is **0** for every batch: what the price");
        report.AppendLine("phase spent belongs to the superseded authorisation and is carried here as");
        report.AppendLine("evidence, not charged against this ceiling.");
        report.AppendLine();

        report.AppendLine("## The ledger this was cut from");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Ready members | {members.Count} |"));
        report.AppendLine(Universe.Inv($"| Split fingerprints already satisfied | {satisfied.Count} ({string.Join(", ", satisfied)}) |"));
        report.AppendLine(Universe.Inv($"| **Outstanding split fingerprints** | **{outstanding.Count}** |"));
        report.AppendLine(Universe.Inv($"| Ingestion runs before / after | {runsBefore} / {await context.IngestionRuns.AsNoTracking().CountAsync()} |"));
        report.AppendLine(Universe.Inv($"| Observations before / after | {observationsBefore} / {await context.Observations.AsNoTracking().CountAsync()} |"));
        report.AppendLine();

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "split-authorization.md"),
            report.ToString());

        _output.WriteLine(report.ToString());

        // ---- the invariants -------------------------------------------------------------------------

        Assert.True(
            priceBefore.SequenceEqual(await File.ReadAllBytesAsync(pricePath)),
            "the price authorisation changed; it is a historical record and must not be amended");

        Assert.True(
            manifestBytes.SequenceEqual(await File.ReadAllBytesAsync(manifestPath)),
            "the sealed manifest changed");

        Assert.True(outstanding.Count == 352, Universe.Inv($"{outstanding.Count} outstanding split requests, not 352"));
        Assert.True(satisfied.Count == 3, Universe.Inv($"{satisfied.Count} split fingerprints already satisfied, not 3"));
        Assert.True(batches.Count == 6, Universe.Inv($"{batches.Count} batches, not 6"));
        Assert.Equal([70, 70, 70, 70, 70, 2], batches.Select(b => b.Count).ToList());
        Assert.Equal(352, batches.Sum(b => b.Count));

        Assert.Equal(352, installed.DispatchCeiling);
        Assert.Equal(355, installed.PlannedRequests);
        Assert.Equal(3, installed.AlreadySatisfied);
        Assert.Equal([SplitSource], installed.Sources.Order(StringComparer.Ordinal).ToList());
        Assert.Equal(consumed, installed.AlreadyConsumed);
        Assert.Equal(superseded.AuthorizationId, installed.SupersedesAuthorizationId);
        Assert.NotEqual(superseded.AuthorizationId, installed.AuthorizationId);
        Assert.NotEqual(superseded.Digest, installed.Digest);
        Assert.Equal(0, installed.Consumed);

        // Nothing was dispatched, and no batch became runnable.
        Assert.Equal(runsBefore, await context.IngestionRuns.AsNoTracking().CountAsync());
        Assert.Equal(observationsBefore, await context.Observations.AsNoTracking().CountAsync());
        Assert.Equal(quarantineBefore, await context.QuarantinedPayloads.AsNoTracking().CountAsync());
        Assert.DoesNotContain(AcquisitionSplitBatchTests.Partition, b => b.Authorised);
    }

    // ---- reading -------------------------------------------------------------------------------------

    /// <summary>
    /// What every process has spent against the superseded authorisation, from what each wrote.
    /// </summary>
    private static async Task<int> ConsumedAsync()
    {
        var universe = Universe.RepositoryPath("artifacts", "universe");
        var total = 0;

        var outcome = Path.Combine(universe, "acquisition-outcome.json");

        if (File.Exists(outcome))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outcome));

            total += document.RootElement.GetProperty("AuthorizationConsumed").GetInt32();
        }

        foreach (var path in Directory
            .EnumerateFiles(universe, "acquisition-batch-*.json")
            .Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

            total += document.RootElement.GetProperty("AuthorizationConsumedThisRun").GetInt32();
        }

        return total;
    }

    private static async Task<List<Ready>> ReadyMembersAsync()
    {
        using var identity = JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("artifacts", "universe", "identity-sample400.json")));

        return identity.RootElement
            .GetProperty("Members")
            .EnumerateArray()
            .Where(m => m.TryGetProperty("Ready", out var r) && r.ValueKind == JsonValueKind.True)
            .Select(m =>
            {
                var ticker = m.GetProperty("Ticker").GetString()!;

                return new Ready(ticker, AcquisitionPlanning.SymbolFor(ticker));
            })
            .OrderBy(m => m.Symbol, StringComparer.Ordinal)
            .ToList();
    }

    private sealed record Ready(string Ticker, string Symbol);
}
