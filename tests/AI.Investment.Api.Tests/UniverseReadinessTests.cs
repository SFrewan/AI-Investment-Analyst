using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ingestion;
using AI.Investment.Domain.Analytics.Financial;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Admission;
using AI.Investment.Infrastructure.Ingestion.Providers;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Audits the identity of the sealed four hundred and says which of them could safely be priced.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No price, split, dividend, corporate action, score, opportunity, strategy or order.</strong>
/// The only requests this stage may make are the two SEC submissions calls left of the authorised
/// three hundred and sixty-seven, and only for members whose identity a transport failure left
/// unresolved. No EODHD client is constructed.
/// </para>
/// <para>
/// <strong>The sealed universe is the thing being protected.</strong> Its file is hashed before the
/// run and again after, its fingerprint is compared against the value that was approved rather than
/// recomputed from whatever the file now says, and the membership is asserted identical in content
/// and order. Everything this stage learns is written beside it, never into it.
/// </para>
/// <para>
/// Gated on <c>AIINV_UNIVERSE_READINESS=1</c>.
/// </para>
/// </remarks>
public sealed class UniverseReadinessTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_UNIVERSE_READINESS";

    /// <summary>
    /// The retry is off unless this is explicitly set, so regenerating the report costs nothing.
    /// </summary>
    /// <remarks>
    /// The stage's expensive half - retrying the CIK whose submissions document will not load -
    /// has already run and failed. Its evidence is recorded. Re-running the stage to correct a
    /// label in the report should not spend the last authorised request on a fourth attempt at
    /// the same document, so the retry needs its own deliberate switch rather than riding along
    /// with the stage's.
    /// </remarks>
    private const string RetryVariable = "AIINV_UNIVERSE_RETRY";

    private const string SealedFingerprint =
        "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    /// <summary>Calls already spent against the authorised 367.</summary>
    /// <remarks>365 from the identity pass, plus the one retry this stage has already made.</remarks>
    private const int SecCallsAlreadySpent = 366;

    private const int SecCallBudget = 367;

    /// <summary>What is left, and the ceiling this stage may not pass.</summary>
    private const int RemainingBudget = SecCallBudget - SecCallsAlreadySpent;

    private const int MillisecondsBetweenRequests = 240;

    private const decimal Gate2Floor = 0.05m;

    private const decimal MinimumQuarterlyPeriodsPerYear = 2.9m;

    private static readonly string Attempt =
        DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public UniverseReadinessTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_identity_layer_is_audited_and_every_member_is_judged_acquirable_or_not()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The readiness audit is off. Set {GateVariable}=1 to run it. It makes no provider " +
            $"call at all unless {RetryVariable}=1 also asks for the retry, and never a billable " +
            "one.");

        var watch = Stopwatch.StartNew();
        var root = _factory.Services;

        var manifestPath = Universe.RepositoryPath(
            "declarations", "universe-sample400-2021-2026.json");

        Skip.If(!File.Exists(manifestPath), "The sealed sample manifest is not on disk.");

        // ---- the seal, before anything --------------------------------------------------------

        var manifestBefore = await File.ReadAllTextAsync(manifestPath);
        var manifestHashBefore = Hash(manifestBefore);

        Assert.True(
            IdentityResolution.IsSealedAs(manifestBefore, SealedFingerprint),
            "The manifest on disk is not the sealed universe this stage was authorised against.");

        var sealedMembers = ReadSealed(manifestBefore);

        Assert.Equal(Universe.Size, sealedMembers.Count);

        var order = sealedMembers.Select(m => m.Cik).ToList();

        var declarationsPath = Universe.RepositoryPath(StrategyRegister.RelativePath);
        var declarationsHashBefore = Hash(await File.ReadAllTextAsync(declarationsPath));

        var before = await CountsAsync(root);

        // ---- step 1: what the existing identity table says ------------------------------------

        var priorFailures = await PriorFailuresAsync();

        // ---- step 2: at most the remaining budget, and only for the unresolved ----------------

        var identities = await SecIdentitiesAsync(root, order);

        var unresolved = order
            .Where(c => !identities.ContainsKey(c))
            .ToList();

        var calls = 0;
        var succeeded = 0;
        var refused = 0;
        var failed = 0;
        var thisRunFailures = new List<string>();

        var retryAuthorised = string.Equals(
            Environment.GetEnvironmentVariable(RetryVariable),
            "1",
            StringComparison.Ordinal);

        var toRetry = retryAuthorised ? unresolved : new List<string>();

        foreach (var cik in toRetry)
        {
            if (calls >= RemainingBudget)
            {
                break;
            }

            using var scope = root.CreateScope();
            var services = scope.ServiceProvider;

            var request = IngestionRequest.Create(
                SecEdgarProvider.Id,
                DataCategory.CompanyProfile,
                Region.UnitedStates,
                IngestionSubject.Create(Universe.CompanyKind, cik),
                CorrelationId.Create(Universe.Inv($"readiness-{cik}-{Attempt}")),
                services.GetRequiredService<IClock>().UtcNow);

            var result = await services.GetRequiredService<IDataAcquisition>().AcquireAsync(request);

            if (result.Run.Outcome == IngestionOutcome.Refused)
            {
                // Refused at the gateway: nothing left the machine, nothing was spent.
                refused++;

                continue;
            }

            calls++;

            if (result.WasFetched)
            {
                succeeded++;
            }
            else
            {
                failed++;

                thisRunFailures.Add(Universe.Inv(
                    $"{cik}: {result.Run.Outcome} - {result.Run.Reason ?? "no reason recorded"}"));
            }

            await Task.Delay(MillisecondsBetweenRequests);
        }

        // Re-read after the retry, so a member resolved just now is judged on what it now has.
        identities = await SecIdentitiesAsync(root, order);

        var delisted = LoadDelisted();

        // ---- steps 3, 4 and 6: resolve, classify, and judge acquirability ---------------------

        var rows = new List<ReadinessRow>();

        foreach (var member in sealedMembers)
        {
            identities.TryGetValue(member.Cik, out var sec);

            var provisional = string.Equals(
                member.TickerSource,
                "sec-submissions",
                StringComparison.Ordinal)
                    ? null
                    : member.Ticker;

            var ambiguous = provisional is null &&
                sec?.Ticker is null &&
                MatchCount(sec?.Name ?? member.Name, delisted) > 1;

            var resolution = IdentityResolution.Resolve(sec?.Ticker, provisional, ambiguous);

            var transportFailed = sec is null &&
                (priorFailures.Contains(member.Cik) ||
                    thisRunFailures.Exists(f => f.StartsWith(member.Cik, StringComparison.Ordinal)));

            var readiness = IdentityResolution.Assess(resolution, transportFailed);

            rows.Add(new ReadinessRow(
                member.Cik,
                sec?.Name ?? member.Name,
                resolution.Ticker,
                resolution.Status,
                IdentityResolution.SymbolSource(resolution.Status, transportFailed),
                Confidence(resolution.Status, transportFailed),
                IdentityResolution.IsAuthoritative(resolution.Status),
                readiness.Ready,
                readiness.Reason,
                resolution.PreviousTicker,
                member.TickerSource,
                resolution.Conflict,
                transportFailed
                    ? "EDGAR's submissions document could not be retrieved for this CIK"
                    : null,
                sec?.Exchange,
                sec?.Sic,
                sec?.Sector));
        }

        // ---- step 5: the identity artefact and the quality report -----------------------------

        var artefact = new ReadinessArtefact(
            SealedFingerprint,
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            "EDGAR submissions are authoritative for ticker identity. A symbol recovered from " +
            "EODHD's delisted list by exact name match is provisional, is never promoted without " +
            "EDGAR evidence, and is not acquisition-ready. A vendor's exchange suffix is normalised " +
            "away; a share-class marker is not. An ambiguous name match is refused rather than " +
            "resolved. A transport failure leaves a member unresolved and never falls back to a " +
            "name match. No member is ever dropped or replaced.",
            rows.Count(r => r.Ready),
            rows.Count(r => !r.Ready),
            rows);

        // The identity table is only this stage's to write until something supersedes it. The
        // filing recovery merges into the same file, and re-running this stage afterwards would
        // rewrite it from the pre-recovery logic and silently revert 86 recovered identities -
        // which is exactly what happened once. A table carrying recovery outcomes is left alone.
        var identityPath = Universe.RepositoryPath(
            "artifacts", "universe", "identity-sample400.json");

        var superseded = File.Exists(identityPath) &&
            (await File.ReadAllTextAsync(identityPath))
                .Contains("\"RecoveryOutcome\"", StringComparison.Ordinal);

        if (superseded)
        {
            _output.WriteLine(
                "The identity table carries recovery outcomes and was NOT rewritten: this stage "
                + "would have reverted it to the pre-recovery identities. The gates below are "
                + "judged on the stores, which are unaffected.");
        }
        else
        {
            await Universe.WriteAsync(
                identityPath,
                JsonSerializer.Serialize(artefact, Universe.Json) + "\n");
        }

        // ---- step 7: nothing moved that should not have ---------------------------------------

        var manifestAfter = await File.ReadAllTextAsync(manifestPath);
        var after = await CountsAsync(root);

        var gates = await JudgeAsync(root, sealedMembers, calls, after);

        var report = Compose(rows, gates, calls, succeeded, refused, failed,
            thisRunFailures, priorFailures, before, after, watch, retryAuthorised);

        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "verify", "universe-identity-final.md"),
            report);

        _output.WriteLine(report);

        // The seal, byte for byte.
        Assert.Equal(manifestHashBefore, Hash(manifestAfter));
        Assert.True(IdentityResolution.IsSealedAs(manifestAfter, SealedFingerprint));
        Assert.Equal(order, ReadSealed(manifestAfter).Select(m => m.Cik).ToList());
        Assert.Equal(Universe.Size, rows.Count);

        // Declarations untouched.
        Assert.Equal(declarationsHashBefore, Hash(await File.ReadAllTextAsync(declarationsPath)));

        // The financial store, the opportunity store and the price namespace, untouched.
        Assert.Equal(before.FinancialRows, after.FinancialRows);
        Assert.Equal(before.FinancialDistinct, after.FinancialDistinct);
        Assert.Equal(before.Opportunities, after.Opportunities);
        Assert.Equal(before.PriceRows, after.PriceRows);
        Assert.Equal(before.PriceDistinct, after.PriceDistinct);
        Assert.Equal(before.CorporateActionRows, after.CorporateActionRows);
        Assert.Equal(before.CorporateActionDistinct, after.CorporateActionDistinct);
        Assert.Equal(before.Quarantine, after.Quarantine);

        // Ingestion runs may grow only by the identity requests this stage actually made.
        Assert.True(
            after.Runs - before.Runs <= calls + refused,
            Universe.Inv($"{after.Runs - before.Runs} ingestion runs were recorded for {calls} ")
            + Universe.Inv($"calls and {refused} refusals."));

        Assert.True(
            calls <= RemainingBudget,
            Universe.Inv($"{calls} SEC calls against {RemainingBudget} remaining of the ")
            + Universe.Inv($"{SecCallBudget} authorised."));

        // Without the retry switch the stage is a pure regeneration and spends nothing.
        Assert.True(
            retryAuthorised || calls == 0,
            Universe.Inv($"{calls} SEC calls were made with {RetryVariable} unset."));

        // No row may name EDGAR as the source of a symbol EDGAR did not supply.
        Assert.DoesNotContain(rows, r =>
            !IdentityResolution.IsAuthoritative(r.Status) &&
            r.Source.StartsWith("EDGAR", StringComparison.Ordinal));

        // And every authoritative row must, since that is where its symbol came from.
        Assert.DoesNotContain(rows, r =>
            IdentityResolution.IsAuthoritative(r.Status) &&
            !r.Source.StartsWith("EDGAR", StringComparison.Ordinal));

        // A row with no symbol and no candidates claims no source at all.
        Assert.DoesNotContain(rows, r =>
            r.Ticker is null &&
            r.Status != IdentityResolution.Ambiguous &&
            !r.Source.StartsWith("none", StringComparison.Ordinal));

        // An ambiguous row is the one case with a source but no symbol: the delisted list did
        // supply candidates, and the label has to say both halves of that.
        Assert.All(
            rows.Where(r => r.Status == IdentityResolution.Ambiguous),
            r =>
            {
                Assert.Null(r.Ticker);
                Assert.Contains("ambiguous", r.Source, StringComparison.Ordinal);
                Assert.False(r.Ready);
            });

        // The failure evidence must survive a regeneration. Losing it would turn an unresolved
        // member into an ordinary one without anyone deciding to.
        var unresolvedMember = rows.Single(r =>
            string.Equals(r.Cik, "0001503584", StringComparison.Ordinal));

        Assert.NotNull(unresolvedMember.TransportFailure);
        Assert.False(unresolvedMember.Ready);
        Assert.False(IdentityResolution.IsAuthoritative(unresolvedMember.Status));

        // The conflict table is the conflicts, not everything carrying a conflict message.
        var conflictRows = rows.Where(r => IdentityResolution.IsConflict(r.Status)).ToList();

        Assert.DoesNotContain(conflictRows, r => r.Status == IdentityResolution.Ambiguous);

        Assert.All(conflictRows, r =>
        {
            Assert.NotNull(r.Ticker);
            Assert.NotNull(r.PreviousTicker);
        });

        // Pinned: the sealed four hundred, with no further call authorised, disagree in exactly
        // this many places. A change here means the identity layer moved, which it may not.
        Assert.Equal(36, rows.Count(r => IdentityResolution.IsConflict(r.Status)));
        Assert.Equal(26, rows.Count(r => r.Status == IdentityResolution.Ambiguous));
        Assert.Equal(69, rows.Count(r => r.Status == IdentityResolution.Provisional));
        Assert.Equal(36, rows.Count(r => r.Status == IdentityResolution.Unmatched));
        Assert.Equal(269, rows.Count(r => IdentityResolution.IsAuthoritative(r.Status)));

        var failedGates = gates.Where(g => g.State == GateState.Failed).ToList();

        Assert.True(
            failedGates.Count == 0,
            "Gates failed: " + string.Join(
                " | ",
                failedGates.Select(g => Universe.Inv($"{g.Number}. {g.Name}: {g.Detail}"))));
    }

    // ---- reading ---------------------------------------------------------------------------------

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static List<SealedMember> ReadSealed(string json)
    {
        using var document = JsonDocument.Parse(json);

        var members = new List<SealedMember>();

        if (!document.RootElement.TryGetProperty("Members", out var list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return members;
        }

        foreach (var entry in list.EnumerateArray())
        {
            members.Add(new SealedMember(
                Text(entry, "Cik") ?? string.Empty,
                Text(entry, "Name"),
                Text(entry, "Ticker"),
                Text(entry, "TickerSource") ?? "none"));
        }

        return members;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Members the previous identity pass could not retrieve a document for.</summary>
    private static async Task<HashSet<string>> PriorFailuresAsync()
    {
        var failures = new HashSet<string>(StringComparer.Ordinal);
        var path = Universe.RepositoryPath("artifacts", "universe", "identity-sample400.json");

        if (!File.Exists(path))
        {
            return failures;
        }

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

        if (!document.RootElement.TryGetProperty("Members", out var list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return failures;
        }

        foreach (var entry in list.EnumerateArray())
        {
            // Three keys, because this stage rewrites the file it reads. The identity pass wrote
            // the failure under "Evidence"; this stage writes it under "TransportFailure". Reading
            // only the older key meant the second run of this stage found no prior failure and
            // quietly reclassified an unresolved member as an ordinary one - the evidence was not
            // preserved, it was overwritten by the act of regenerating the report.
            var evidence = Text(entry, "TransportFailure")
                ?? Text(entry, "Evidence")
                ?? Text(entry, "PriorFailure");

            if (evidence is not null &&
                (evidence.Contains("no submissions document", StringComparison.OrdinalIgnoreCase) ||
                    evidence.Contains("could not be retrieved", StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add(Text(entry, "Cik") ?? string.Empty);
            }
        }

        return failures;
    }

    private static async Task<Dictionary<string, SecIdentity>> SecIdentitiesAsync(
        IServiceProvider root,
        List<string> order)
    {
        using var scope = root.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var wanted = order.ToHashSet(StringComparer.Ordinal);

        var rows = await context.Observations
            .AsNoTracking()
            .Where(o => o.Subject.Kind == Universe.CompanyKind)
            .Where(o => EF.Functions.Like(o.Attribute, "company.%"))
            .Select(o => new
            {
                Cik = o.Subject.Identifier!,
                o.Attribute,
                o.Value.Canonical,
                o.Provenance.RetrievedAtUtc,
            })
            .ToListAsync();

        var identities = new Dictionary<string, SecIdentity>(StringComparer.Ordinal);

        foreach (var group in rows
            .Where(r => wanted.Contains(r.Cik))
            .GroupBy(r => r.Cik, StringComparer.Ordinal))
        {
            var latest = group
                .GroupBy(r => r.Attribute, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(r => r.RetrievedAtUtc).First().Canonical,
                    StringComparer.Ordinal);

            string? Read(string attribute) =>
                latest.TryGetValue(attribute, out var value) && !string.IsNullOrWhiteSpace(value)
                    ? value.Trim()
                    : null;

            identities[group.Key] = new SecIdentity(
                Read("company.name"),
                Read("company.ticker"),
                Read("company.exchange"),
                Read("company.sic"),
                Read("company.sic-description"));
        }

        return identities;
    }

    private static Dictionary<string, int>? LoadDelisted()
    {
        var path = Universe.RepositoryPath("artifacts", "universe", "eodhd-delisted-us.json");

        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind == JsonValueKind.Object &&
                row.TryGetProperty("Name", out var name) &&
                name.ValueKind == JsonValueKind.String &&
                name.GetString() is { Length: > 0 } text)
            {
                var key = Normalise(text);

                if (key.Length > 0)
                {
                    counts[key] = counts.TryGetValue(key, out var held) ? held + 1 : 1;
                }
            }
        }

        return counts;
    }

    private static int MatchCount(string? name, Dictionary<string, int>? delisted)
    {
        if (name is null || delisted is null)
        {
            return 0;
        }

        var key = Normalise(name);

        return key.Length > 0 && delisted.TryGetValue(key, out var count) ? count : 0;
    }

    private static string Normalise(string name)
    {
        var letters = new StringBuilder(name.Length);

        foreach (var c in name.ToUpperInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                letters.Append(c);
            }
            else if (letters.Length > 0 && letters[^1] != ' ')
            {
                letters.Append(' ');
            }
        }

        return string.Concat(letters.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => !Suffixes.Contains(w)));
    }

    private static readonly HashSet<string> Suffixes = new(StringComparer.Ordinal)
    {
        "INC", "INCORPORATED", "CORP", "CORPORATION", "CO", "COMPANY", "LLC", "LP", "LLLP",
        "LTD", "LIMITED", "PLC", "NV", "SA", "AG", "THE", "CLASS", "COMMON", "STOCK",
    };

    private static string Confidence(string status, bool transportFailed) => status switch
    {
        IdentityResolution.Confirmed => "high - EDGAR and the delisted list name the same symbol",
        IdentityResolution.Conflict => "high - EDGAR's own submissions document, which overrode a name match",
        IdentityResolution.SecOnly => "high - EDGAR's own submissions document",
        IdentityResolution.Provisional => "low - an exact name match and nothing else",
        IdentityResolution.Ambiguous => "none - several symbols share the name",
        _ => transportFailed
            ? "none - the document that would have said could not be retrieved"
            : "none - no source names a symbol",
    };

    // ---- counts and gates -------------------------------------------------------------------------

    private static async Task<Counts> CountsAsync(IServiceProvider root)
    {
        using var scope = root.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var facts = context.Observations
            .AsNoTracking()
            .Where(o => o.Subject.Kind == Universe.CompanyKind)
            .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.Prefix + "%"));

        return new Counts(
            await facts.CountAsync(),
            await facts
                .Select(o => new
                {
                    Cik = o.Subject.Identifier!,
                    o.Attribute,
                    o.Value.Canonical,
                    o.Provenance.AsOfUtc,
                    o.Provenance.PublishedAtUtc,
                })
                .Distinct()
                .CountAsync(),
            await context.Observations.AsNoTracking()
                .CountAsync(o => o.Attribute == ObservationDeduplication.PriceAttribute),

            // The distinct identity of a price row, counted the same way as a financial one.
            // Gate 12 read PASS for months over the financial namespace alone while 5,249
            // duplicate price rows sat beside it, because nothing counted this.
            await context.Observations.AsNoTracking()
                .Where(o => o.Attribute == ObservationDeduplication.PriceAttribute)
                .Select(o => new
                {
                    o.Subject.Kind,
                    o.Subject.Identifier,
                    o.Attribute,
                    o.Value.Canonical,
                    o.Provenance.AsOfUtc,
                    o.Provenance.PublishedAtUtc,
                })
                .Distinct()
                .CountAsync(),
            await context.Observations.AsNoTracking()
                .CountAsync(o => o.Attribute == ObservationDeduplication.SplitAttribute),
            await context.Observations.AsNoTracking()
                .Where(o => o.Attribute == ObservationDeduplication.SplitAttribute)
                .Select(o => new
                {
                    o.Subject.Kind,
                    o.Subject.Identifier,
                    o.Attribute,
                    o.Value.Canonical,
                    o.Provenance.AsOfUtc,
                    o.Provenance.PublishedAtUtc,
                })
                .Distinct()
                .CountAsync(),
            await context.IngestionRuns.AsNoTracking().CountAsync(),
            await context.QuarantinedPayloads.AsNoTracking().CountAsync(),
            await context.Opportunities.AsNoTracking().CountAsync());
    }

    private static async Task<List<Gate>> JudgeAsync(
        IServiceProvider root,
        List<SealedMember> members,
        int calls,
        Counts after)
    {
        using var scope = root.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var facts = context.Observations
            .AsNoTracking()
            .Where(o => o.Subject.Kind == Universe.CompanyKind)
            .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.Prefix + "%"));

        var early = await facts.CountAsync(o => o.Provenance.PublishedAtUtc < o.Provenance.AsOfUtc);
        var late = await facts
            .CountAsync(o => o.Provenance.PublishedAtUtc > o.Provenance.RetrievedAtUtc);

        var attributes = await facts.Select(o => o.Attribute).Distinct().ToListAsync();
        var known = KnownAttributes();
        var strangers = attributes.Where(a => !known.Contains(a)).ToList();

        var ciks = members.Select(m => m.Cik).ToHashSet(StringComparer.Ordinal);

        var quarterly = (await context.Observations
                .AsNoTracking()
                .Where(o => o.Subject.Kind == Universe.CompanyKind)
                .Where(o => EF.Functions.Like(o.Attribute, FinancialFigures.QuarterlyPrefix + "%"))
                .Select(o => new { Cik = o.Subject.Identifier!, o.Provenance.AsOfUtc })
                .Distinct()
                .ToListAsync())
            .Where(q => ciks.Contains(q.Cik))
            .ToList();

        var periods = new HashSet<string>(StringComparer.Ordinal);
        var companyYears = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in quarterly)
        {
            if (row.AsOfUtc.Year < Universe.Cuts[0].Year || row.AsOfUtc.Year > Universe.Cuts[^1].Year)
            {
                continue;
            }

            periods.Add(Universe.Inv($"{row.Cik}|{row.AsOfUtc:yyyy-MM-dd}"));
            companyYears.Add(Universe.Inv($"{row.Cik}|{row.AsOfUtc.Year}"));
        }

        var cadence = companyYears.Count == 0
            ? 0m
            : Math.Round((decimal)periods.Count / companyYears.Count, 2);

        var covered = quarterly.Select(q => q.Cik).Distinct(StringComparer.Ordinal).Count();

        var registerText = await File.ReadAllTextAsync(
            Universe.RepositoryPath(StrategyRegister.RelativePath));

        var declarations = StrategyRegister.Parse(registerText).Count;
        var cites = registerText.Contains(
            UniverseSampleManifestTests.BaseName,
            StringComparison.Ordinal);

        // Survivorship is a property of the sealed manifest, re-read rather than recomputed here.
        var dropouts = await DropoutsAsync();
        var share = (decimal)dropouts / members.Count;

        return
        [
            new Gate(1, "Manifest sealed before any hypothesis",
                cites ? GateState.Failed : GateState.Passed,
                cites
                    ? "a sealed declaration already names this evidence base"
                    : Universe.Inv($"`{SealedFingerprint}` unchanged, hashed before and after; no ")
                      + "declaration names it"),

            new Gate(2, "Survivorship",
                share >= Gate2Floor ? GateState.Passed : GateState.Failed,
                Universe.Inv($"{dropouts} of {members.Count} - {share:P2} against a floor of ")
                + Universe.Inv($"{Gate2Floor:P0}, re-read from the sealed manifest")),

            new Gate(3, "Membership look-ahead", GateState.Passed,
                "no member added, removed or reordered; no cohort date moved"),

            new Gate(4, "Quarterly cadence", GateState.Deferred,
                Universe.Inv($"{cadence:F2} against {MinimumQuarterlyPeriodsPerYear:F1}, over the ")
                + Universe.Inv($"{covered} of {members.Count} members whose facts are held")),

            new Gate(5, "No redefinition",
                strangers.Count == 0 ? GateState.Passed : GateState.Failed,
                strangers.Count == 0
                    ? Universe.Inv($"all {attributes.Count} stored financial attributes are declared ")
                      + "annual or quarterly names; this stage wrote only company.* rows"
                    : "unknown attributes: " + string.Join(", ", strangers.Take(8))),

            new Gate(6, "Coverage", GateState.Deferred,
                "needs a price series for every member, which needs the acquisition"),

            new Gate(7, "Point-in-time invariants",
                early == 0 && late == 0 ? GateState.Passed : GateState.Failed,
                Universe.Inv($"{early} facts published before the period they describe and {late} ")
                + Universe.Inv($"published after they were retrieved, over {after.FinancialRows} rows")),

            new Gate(8, "Request budget",
                calls <= RemainingBudget ? GateState.Passed : GateState.Failed,
                Universe.Inv($"{calls} SEC calls this stage against {RemainingBudget} remaining; ")
                + Universe.Inv($"{SecCallsAlreadySpent + calls} of {SecCallBudget} cumulative; ")
                + "0 EODHD calls, 0 price and 0 corporate-action requests"),

            new Gate(9, "Idempotency", GateState.Deferred,
                "the acquisition stage was not rerun; the manifest hash, membership, financial "
                + "rows, opportunity rows and declarations were all asserted unchanged instead"),

            new Gate(10, "Opportunities",
                after.Opportunities == 0 ? GateState.Passed : GateState.Failed,
                Universe.Inv($"{after.Opportunities}; no prediction, order, score or cycle")),

            new Gate(11, "Declarations", GateState.Passed,
                Universe.Inv($"{declarations} sealed declarations re-parsed, every fingerprint ")
                + "recomputed, and the register file hashed unchanged"),

            // Zero tolerance, in BOTH namespaces. The financial rule is unchanged and is not
            // weakened by the addition; the market-data namespace is simply judged by the same
            // one, which is what it should always have been.
            new Gate(12, "De-duplication",
                ObservationDeduplication.Passes(after.FinancialRows, after.FinancialDistinct)
                    && ObservationDeduplication.Passes(after.PriceRows, after.PriceDistinct)
                    && ObservationDeduplication.Passes(after.CorporateActionRows, after.CorporateActionDistinct)
                    ? GateState.Passed
                    : GateState.Failed,
                Universe.Inv($"financial {after.FinancialDistinct} distinct across {after.FinancialRows} rows (excess {ObservationDeduplication.Excess(after.FinancialRows, after.FinancialDistinct)}); prices {after.PriceDistinct} across {after.PriceRows} (excess {ObservationDeduplication.Excess(after.PriceRows, after.PriceDistinct)}); splits {after.CorporateActionDistinct} across {after.CorporateActionRows} (excess {ObservationDeduplication.Excess(after.CorporateActionRows, after.CorporateActionDistinct)})")),
        ];
    }

    private static async Task<int> DropoutsAsync()
    {
        var path = Universe.RepositoryPath("declarations", "universe-sample400-2021-2026.json");

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

        if (!document.RootElement.TryGetProperty("Members", out var list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var dropouts = 0;

        foreach (var entry in list.EnumerateArray())
        {
            var stopped = entry.TryGetProperty("StoppedFiling", out var s) &&
                s.ValueKind == JsonValueKind.True;

            var absent = entry.TryGetProperty("AbsentFromFinalCrossSection", out var a) &&
                a.ValueKind == JsonValueKind.True;

            if (stopped || absent)
            {
                dropouts++;
            }
        }

        return dropouts;
    }

    private static HashSet<string> KnownAttributes()
    {
        string[] names =
        [
            FinancialFigures.Revenue, FinancialFigures.GrossProfit,
            FinancialFigures.OperatingIncome, FinancialFigures.NetIncome,
            FinancialFigures.DepreciationAndAmortisation, FinancialFigures.OperatingCashFlow,
            FinancialFigures.CapitalExpenditure, FinancialFigures.CashAndEquivalents,
            FinancialFigures.TotalDebt, FinancialFigures.CurrentAssets,
            FinancialFigures.CurrentLiabilities, FinancialFigures.Inventory,
            FinancialFigures.TotalAssets, FinancialFigures.TotalEquity,
            FinancialFigures.DilutedShares, FinancialFigures.FreeCashFlow,
            FinancialFigures.QuickAssets, FinancialFigures.Ebitda, FinancialFigures.NetDebt,
            FinancialFigures.QuarterlyRevenue, FinancialFigures.QuarterlyGrossProfit,
            FinancialFigures.QuarterlyOperatingIncome, FinancialFigures.QuarterlyNetIncome,
            FinancialFigures.QuarterlyDepreciationAndAmortisation,
            FinancialFigures.QuarterlyOperatingCashFlow,
            FinancialFigures.QuarterlyCapitalExpenditure, FinancialFigures.QuarterlyDilutedShares,
        ];

        return names.ToHashSet(StringComparer.Ordinal);
    }

    // ---- reporting -----------------------------------------------------------------------------------

    private static string Compose(
        List<ReadinessRow> rows,
        List<Gate> gates,
        int calls,
        int succeeded,
        int refused,
        int failed,
        List<string> thisRunFailures,
        HashSet<string> priorFailures,
        Counts before,
        Counts after,
        Stopwatch watch,
        bool retryAuthorised)
    {
        var report = new StringBuilder();

        int Status(string status) =>
            rows.Count(r => string.Equals(r.Status, status, StringComparison.Ordinal));

        report.AppendLine("# Identity quality and acquisition readiness - the sealed four hundred");
        report.AppendLine();
        report.AppendLine("**No price, split, dividend, corporate action, score, opportunity,");
        report.AppendLine("strategy or order.** No EODHD call. The sealed universe is unchanged.");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Sealed universe | `{SealedFingerprint}` |"));
        report.AppendLine(Universe.Inv($"| Members | {rows.Count} |"));
        report.AppendLine(Universe.Inv($"| **SEC calls this stage** | **{calls}** of {RemainingBudget} remaining |"));
        report.AppendLine(Universe.Inv($"| Cumulative SEC calls | {SecCallsAlreadySpent + calls} of {SecCallBudget} |"));
        report.AppendLine(Universe.Inv($"| Succeeded / refused / failed | {succeeded} / {refused} / {failed} |"));
        report.AppendLine(Universe.Inv($"| **EODHD calls this stage** | **0** |"));
        report.AppendLine(Universe.Inv(
            $"| Retry of the unresolved CIK | {(retryAuthorised ? "authorised" : $"**off** - `{RetryVariable}` is not set, so no request left the machine")} |"));
        report.AppendLine();

        report.AppendLine("## Step 1 - the identity table, grouped");
        report.AppendLine();
        report.AppendLine("| Group | Status | Members | SEC-authoritative |");
        report.AppendLine("| --- | --- | ---: | --- |");
        report.AppendLine(Universe.Inv($"| A | `{IdentityResolution.Confirmed}` | {Status(IdentityResolution.Confirmed)} | yes |"));
        report.AppendLine(Universe.Inv($"| B | `{IdentityResolution.Conflict}` | {Status(IdentityResolution.Conflict)} | yes |"));
        report.AppendLine(Universe.Inv($"| C | `{IdentityResolution.SecOnly}` | {Status(IdentityResolution.SecOnly)} | yes |"));
        report.AppendLine(Universe.Inv($"| D | `{IdentityResolution.Provisional}` | {Status(IdentityResolution.Provisional)} | **no** |"));
        report.AppendLine(Universe.Inv($"| D2 | `{IdentityResolution.Ambiguous}` | {Status(IdentityResolution.Ambiguous)} | **no** |"));
        report.AppendLine(Universe.Inv($"| E | `{IdentityResolution.Unmatched}` | {Status(IdentityResolution.Unmatched)} | **no** |"));
        report.AppendLine(Universe.Inv($"| F | transport-failed | {rows.Count(r => r.TransportFailure is not null)} | **no** |"));
        report.AppendLine();
        report.AppendLine(Universe.Inv($"SEC-authoritative identities: **{rows.Count(r => r.SecAuthoritative)}** of {rows.Count}."));
        report.AppendLine();

        report.AppendLine("## Step 6 - acquisition readiness");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"**READY: {rows.Count(r => r.Ready)}**"));
        report.AppendLine();
        report.AppendLine(Universe.Inv($"**NOT READY: {rows.Count(r => !r.Ready)}**"));
        report.AppendLine();
        report.AppendLine("| Reason | Members |");
        report.AppendLine("| --- | ---: |");

        foreach (var group in rows
            .Where(r => !r.Ready)
            .GroupBy(r => r.NotReadyReason ?? "unstated", StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()))
        {
            report.AppendLine(Universe.Inv($"| {group.Key} | {group.Count()} |"));
        }

        report.AppendLine();
        report.AppendLine("### Every member without a safe acquisition symbol");
        report.AppendLine();
        report.AppendLine("| CIK | Name | Symbol held | Status | Confidence | Why not ready |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- |");

        foreach (var row in rows
            .Where(r => !r.Ready)
            .OrderBy(r => r.Status, StringComparer.Ordinal)
            .ThenBy(r => r.Cik, StringComparer.Ordinal))
        {
            report.AppendLine(Universe.Inv(
                $"| `{row.Cik}` | {row.Name ?? "-"} | {row.Ticker ?? "-"} | {row.Status} | {row.Confidence} | {row.NotReadyReason} |"));
        }

        report.AppendLine();
        report.AppendLine("## Step 3 - every SEC/EODHD conflict, SEC winning");
        report.AppendLine();

        // Selected on the status, not on "carries a conflict message". An ambiguity carries one
        // too, and including it would list 26 members with blank symbol columns as though EDGAR
        // had overruled a symbol it never named.
        var conflicts = rows
            .Where(r => IdentityResolution.IsConflict(r.Status))
            .OrderBy(r => r.Cik, StringComparer.Ordinal)
            .ToList();

        report.AppendLine(Universe.Inv(
            $"{conflicts.Count} genuine conflicts: EDGAR named a symbol, another source named a "));
        report.AppendLine(
            "different one, and EDGAR won. The ambiguous members are not here - EDGAR named");
        report.AppendLine("nothing for them, so there is no rejected symbol and no winner.");
        report.AppendLine();
        report.AppendLine("| CIK | Name | SEC symbol | Provisional symbol rejected |");
        report.AppendLine("| --- | --- | --- | --- |");

        foreach (var row in conflicts)
        {
            report.AppendLine(Universe.Inv(
                $"| `{row.Cik}` | {row.Name ?? "-"} | {row.Ticker} | {row.PreviousTicker} |"));
        }

        report.AppendLine();
        report.AppendLine("## Step 2 - transport failures");
        report.AppendLine();

        var failures = rows.Where(r => r.TransportFailure is not null).ToList();

        if (failures.Count == 0)
        {
            report.AppendLine("None outstanding.");
        }
        else
        {
            report.AppendLine("| CIK | Name | Retried this stage | Outcome |");
            report.AppendLine("| --- | --- | --- | --- |");

            foreach (var row in failures)
            {
                var triedNow = thisRunFailures.Exists(
                    f => f.StartsWith(row.Cik, StringComparison.Ordinal));

                var retried = triedNow ? "yes" : "no";

                var outcome = triedNow
                    ? "failed again; left explicitly unresolved"
                    : retryAuthorised
                        ? "no document held; not retried within the remaining budget"
                        : Universe.Inv(
                            $"failed in an earlier run; retry off (`{RetryVariable}` unset), left unresolved");

                report.AppendLine(Universe.Inv($"| `{row.Cik}` | {row.Name ?? "-"} | {retried} | {outcome} |"));
            }

            report.AppendLine();
            report.AppendLine(Universe.Inv(
                $"{priorFailures.Count} carried in from the previous pass. No ticker was invented"));
            report.AppendLine("and no fuzzy match was accepted for any of these. They remain");
            report.AppendLine("members of the sealed universe.");
        }

        report.AppendLine();
        report.AppendLine("## Step 7 - idempotency pre-check");
        report.AppendLine();
        report.AppendLine("| | Before | After |");
        report.AppendLine("| --- | ---: | ---: |");
        report.AppendLine(Universe.Inv($"| Financial observation rows | {before.FinancialRows} | {after.FinancialRows} |"));
        report.AppendLine(Universe.Inv($"| Distinct financial facts | {before.FinancialDistinct} | {after.FinancialDistinct} |"));
        report.AppendLine(Universe.Inv($"| Price rows (`security.close`) | {before.PriceRows} | {after.PriceRows} |"));
        report.AppendLine(Universe.Inv($"| Distinct price identities | {before.PriceDistinct} | {after.PriceDistinct} |"));
        report.AppendLine(Universe.Inv($"| Corporate-action rows (`security.split-ratio`) | {before.CorporateActionRows} | {after.CorporateActionRows} |"));
        report.AppendLine(Universe.Inv($"| Ingestion runs | {before.Runs} | {after.Runs} |"));
        report.AppendLine(Universe.Inv($"| Quarantined payloads | {before.Quarantine} | {after.Quarantine} |"));
        report.AppendLine(Universe.Inv($"| Opportunities | {before.Opportunities} | {after.Opportunities} |"));
        report.AppendLine();
        report.AppendLine("The sealed manifest and the strategy register were hashed before and");
        report.AppendLine("after and are byte-identical. Membership count and CIK ordering are");
        report.AppendLine("asserted unchanged. Ingestion runs may grow only by this stage's own");
        report.AppendLine("identity requests, which is the one audited write it is permitted.");
        report.AppendLine();

        report.AppendLine("## The twelve gates");
        report.AppendLine();
        report.AppendLine("| # | Gate | Result | Detail |");
        report.AppendLine("| ---: | --- | --- | --- |");

        foreach (var gate in gates)
        {
            var state = gate.State switch
            {
                GateState.Passed => "**PASS**",
                GateState.Failed => "**FAIL**",
                _ => "deferred",
            };

            report.AppendLine(Universe.Inv($"| {gate.Number} | {gate.Name} | {state} | {gate.Detail} |"));
        }

        report.AppendLine();
        report.AppendLine("The full four-hundred-row table, with source, confidence, authority and");
        report.AppendLine("readiness for every member, is at");
        report.AppendLine("`artifacts/universe/identity-sample400.json`.");
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Elapsed {watch.Elapsed.TotalSeconds:F0}s."));

        return report.ToString();
    }

    // ---- shapes ---------------------------------------------------------------------------------------

    private enum GateState
    {
        Failed = 0,
        Passed = 1,
        Deferred = 2,
    }

    private sealed record Gate(int Number, string Name, GateState State, string Detail);

    private sealed record SealedMember(string Cik, string? Name, string? Ticker, string TickerSource);

    private sealed record SecIdentity(
        string? Name,
        string? Ticker,
        string? Exchange,
        string? Sic,
        string? Sector);

    private sealed record Counts(
        int FinancialRows,
        int FinancialDistinct,
        int PriceRows,
        int PriceDistinct,
        int CorporateActionRows,
        int CorporateActionDistinct,
        int Runs,
        int Quarantine,
        int Opportunities);

    private sealed record ReadinessRow(
        string Cik,
        string? Name,
        string? Ticker,
        string Status,
        string Source,
        string Confidence,
        bool SecAuthoritative,
        bool Ready,
        string? NotReadyReason,
        string? PreviousTicker,
        string? PreviousTickerSource,
        string? Conflict,
        string? TransportFailure,
        string? Exchange,
        string? Sic,
        string? Sector);

    private sealed record ReadinessArtefact(
        string EvidenceBaseFingerprint,
        string CompletedAtUtc,
        string IdentityRules,
        int Ready,
        int NotReady,
        List<ReadinessRow> Members);
}
