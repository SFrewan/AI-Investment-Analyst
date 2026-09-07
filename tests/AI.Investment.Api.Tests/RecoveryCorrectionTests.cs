using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Investment.Api.Tests;

/// <summary>
/// Corrects the recovery: refuses the non-equities, folds one ticker's case, retries one member.
/// </summary>
/// <remarks>
/// <para>
/// The recovery produced 89 issuer-authoritative identities. Four of them named something other
/// than a tradable equity - three bonds and a placeholder - which were correct readings of the
/// filings and wrong to price. This stage applies <see cref="SecurityClassification"/> to all of
/// them, retries the single member whose request never reached SEC, and merges what survives into
/// the identity table.
/// </para>
/// <para>
/// <strong>Nothing is deleted.</strong> The refused rows stay in the identity table, in the sealed
/// universe, carrying the symbol their filing stated and the reason it cannot be priced. A row that
/// is wrong to acquire is not the same as a row that should disappear, and the difference is the
/// whole discipline of this evidence base.
/// </para>
/// <para>
/// <strong>One call, at most.</strong> Only the transport-failed member is retried. If it fails
/// again it stays transport-failed - never silently converted to unmatched.
/// </para>
/// </remarks>
public sealed class RecoveryCorrectionTests : IClassFixture<UniverseApiFactory>
{
    private const string GateVariable = "AIINV_RECOVERY_CORRECTION";

    /// <summary>One retry, for one member, and nothing else.</summary>
    private const int CallBudget = 1;

    private const int PrefixBytes = 8 * 1024 * 1024;

    private const string Host = "www.sec.gov";

    private const string ArchiveBase = "https://" + Host + "/Archives/edgar/data/";

    private const string SealedFingerprint =
        "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private readonly UniverseApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public RecoveryCorrectionTests(UniverseApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task The_recovery_is_corrected_and_only_equities_become_acquisition_ready()
    {
        Skip.IfNot(
            string.Equals(
                Environment.GetEnvironmentVariable(GateVariable),
                "1",
                StringComparison.Ordinal),
            $"The recovery correction is off. Set {GateVariable}=1 to run it. It makes at most " +
            $"{CallBudget} free SEC request and no billable call.");

        var recoveryPath = Universe.RepositoryPath(
            "artifacts", "universe", "identity-recovery.json");

        Skip.If(!File.Exists(recoveryPath), "The recovery artefact is not on disk.");

        var manifestPath = Universe.RepositoryPath(
            "declarations", "universe-sample400-2021-2026.json");

        var manifestBefore = await File.ReadAllBytesAsync(manifestPath);

        Assert.True(IdentityResolution.IsSealedAs(
            Encoding.UTF8.GetString(manifestBefore), SealedFingerprint));

        var rows = Load(await File.ReadAllTextAsync(recoveryPath));

        Assert.NotEmpty(rows);

        // ---- the retry: one member, one call ----------------------------------------------------

        var stranded = rows
            .Where(r => r.Outcome == RecoveryPartition.TransportFailed)
            .ToList();

        var attempted = 0;
        var succeeded = 0;
        var failed = 0;
        var retryNote = "no member was transport-failed, so no retry was needed";

        if (stranded.Count > 0)
        {
            var dns = await ResolveAsync();

            if (!dns.Resolved)
            {
                retryNote = "not retried: " + dns.Summary + " No call was spent.";
            }
            else
            {
                var member = stranded[0];
                var filing = await FilingAsync(_factory.Services, member.Cik);

                if (filing is null)
                {
                    retryNote = "not retried: no archived annual filing names a document to read";
                }
                else
                {
                    using var budget = new BoundedCalls(CallBudget)
                    {
                        InnerHandler = new SocketsHttpHandler
                        {
                            ConnectTimeout = TimeSpan.FromSeconds(30),
                        },
                    };

                    using var client = new HttpClient(budget) { Timeout = TimeSpan.FromMinutes(2) };

                    client.DefaultRequestHeaders.UserAgent.ParseAdd(
                        "AI-Investment-Analyst/1.0 ("
                        + Environment.GetEnvironmentVariable("AIINV_SEC_CONTACT") + ")");

                    attempted++;

                    try
                    {
                        var document = await PrefixAsync(
                            client, Url(member.Cik, filing.Value.Accession, filing.Value.Document));

                        succeeded++;

                        var symbol = Value(document, "TradingSymbol");

                        rows[rows.IndexOf(member)] = symbol is null
                            ? member with
                            {
                                Outcome = RecoveryPartition.Unresolved,
                                Evidence = Universe.Inv(
                                    $"retried once and reached SEC; no trading symbol in {document.Length} characters of the filing"),
                            }
                            : member with
                            {
                                Outcome = RecoveryPartition.Recovered,
                                Ticker = symbol,
                                Exchange = Value(document, "SecurityExchangeName"),
                                SecurityTitle = Value(document, "Security12bTitle"),
                                Evidence = Universe.Inv(
                                    $"retried once; issuer-stated on the cover page of a {filing.Value.Form} filed {filing.Value.FilingDate}"),
                            };

                        retryNote = symbol is null
                            ? "retried once, reached SEC, and the filing states no trading symbol"
                            : Universe.Inv($"retried once and recovered `{symbol}`");
                    }
                    catch (Exception exception) when (IsTransport(exception))
                    {
                        failed++;

                        rows[rows.IndexOf(member)] = member with
                        {
                            Evidence = member.Evidence + "; retried once and failed again: "
                                + Short(exception),
                        };

                        retryNote = "retried once and failed again; preserved as transport-failed";
                    }
                }
            }
        }

        // ---- the classification: every recovered row, by evidence --------------------------------

        var corrected = new List<Corrected>(rows.Count);

        foreach (var row in rows)
        {
            if (row.Outcome != RecoveryPartition.Recovered)
            {
                corrected.Add(new Corrected(row, null, false, row.Evidence));

                continue;
            }

            var classification = SecurityClassification.Classify(row.Ticker, row.SecurityTitle);

            corrected.Add(new Corrected(
                row with { Ticker = classification.Symbol },
                classification,
                SecurityClassification.IsAcquirable(classification),
                classification.Evidence));
        }

        var acquirable = corrected.Where(c => c.Acquirable).ToList();
        var refused = corrected
            .Where(c => !c.Acquirable && c.Row.Outcome == RecoveryPartition.Recovered)
            .ToList();

        // The four that made this stage necessary are refused, by name.
        foreach (var cik in new[] { "0000038009", "0000083246", "0000804269", "0001662382" })
        {
            var row = corrected.SingleOrDefault(c =>
                string.Equals(c.Row.Cik, cik, StringComparison.Ordinal));

            if (row is null)
            {
                continue;
            }

            Assert.False(row.Acquirable, cik + " is still acquisition-ready.");

            // Preserved, not deleted: it keeps the symbol its filing stated.
            Assert.False(string.IsNullOrWhiteSpace(row.Row.Ticker));
        }

        // Nothing acquirable is anything but an equity.
        Assert.All(acquirable, c => Assert.Equal(
            SecurityClassification.Equity, c.Classification!.Kind));

        // LGIQ, folded.
        var logiq = corrected.SingleOrDefault(c =>
            string.Equals(c.Row.Ticker, "LGIQ", StringComparison.Ordinal));

        if (logiq is not null)
        {
            Assert.True(logiq.Acquirable);
        }

        // ---- the merge into the identity table ----------------------------------------------------

        var identityPath = Universe.RepositoryPath(
            "artifacts", "universe", "identity-sample400.json");

        using var identity = JsonDocument.Parse(await File.ReadAllTextAsync(identityPath));

        var byCik = corrected.ToDictionary(c => c.Row.Cik, StringComparer.Ordinal);

        var merged = new List<Dictionary<string, object?>>();
        var order = new List<string>();

        foreach (var member in identity.RootElement.GetProperty("Members").EnumerateArray())
        {
            var cik = member.GetProperty("Cik").GetString() ?? string.Empty;
            var entry = new Dictionary<string, object?>(StringComparer.Ordinal);

            order.Add(cik);

            foreach (var property in member.EnumerateObject())
            {
                entry[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => property.Value.ToString(),
                };
            }

            if (byCik.TryGetValue(cik, out var correction))
            {
                entry["RecoveryOutcome"] = correction.Row.Outcome;
                entry["RecoveryEvidence"] = correction.Reason;
                entry["SecurityKind"] = correction.Classification?.Kind;
                entry["SecurityConfidence"] = correction.Classification?.Confidence;

                if (correction.Acquirable)
                {
                    entry["Ticker"] = correction.Row.Ticker;
                    entry["Exchange"] = correction.Row.Exchange;
                    entry["Status"] = "sec-filing-recovered";
                    entry["Source"] = "SEC filing cover page, issuer-stated";
                    entry["Confidence"] =
                        "high - the issuer's own filing, which survives deregistration";
                    entry["SecAuthoritative"] = true;
                    entry["Ready"] = true;
                    entry["NotReadyReason"] = null;
                }
                else if (correction.Row.Outcome == RecoveryPartition.Recovered)
                {
                    // Preserved with its stated symbol, and explicitly not acquirable.
                    entry["RecoveredSymbol"] = correction.Row.Ticker;
                    entry["NotReadyReason"] = correction.Reason;
                }
            }

            merged.Add(entry);
        }

        var ready = merged.Count(m => m.TryGetValue("Ready", out var r) && r is true);

        await Universe.WriteAsync(
            identityPath,
            JsonSerializer.Serialize(
                new
                {
                    EvidenceBaseFingerprint = SealedFingerprint,
                    CompletedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    IdentityRules = identity.RootElement.TryGetProperty("IdentityRules", out var r)
                        ? r.GetString()
                        : null,
                    Ready = ready,
                    NotReady = merged.Count - ready,
                    Members = merged,
                },
                Universe.Json) + "\n");

        // The corrected rows, persisted. The first run of this stage lost its retry result when a
        // later stage rewrote the identity table, and the retry had to be spent again. Writing the
        // corrected partition to its own file makes the merge reproducible without a provider call.
        await Universe.WriteAsync(
            Universe.RepositoryPath("artifacts", "universe", "identity-recovery-corrected.json"),
            JsonSerializer.Serialize(
                new
                {
                    EvidenceBaseFingerprint = SealedFingerprint,
                    CompletedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    Acquirable = acquirable.Count,
                    RefusedAsNonEquity = refused.Count,
                    Results = corrected.Select(c => new
                    {
                        c.Row.Cik,
                        c.Row.Name,
                        c.Row.Outcome,
                        c.Row.Ticker,
                        c.Row.Exchange,
                        c.Row.SecurityTitle,
                        SecurityKind = c.Classification?.Kind,
                        SecurityConfidence = c.Classification?.Confidence,
                        c.Acquirable,
                        c.Reason,
                        c.Row.IsDropout,
                    }).ToList(),
                },
                Universe.Json) + "\n");

        // ---- the report ------------------------------------------------------------------------------

        var report = Compose(
            corrected, acquirable, refused, merged.Count, ready,
            attempted, succeeded, failed, retryNote);

        await Universe.WriteAsync(
            Universe.RepositoryPath(
                "artifacts", "verify", "full-survivorship-recovery-corrected.md"),
            report);

        _output.WriteLine(report);

        // ---- nothing moved that must not ---------------------------------------------------------------

        var manifestAfter = await File.ReadAllBytesAsync(manifestPath);

        Assert.Equal(manifestBefore.Length, manifestAfter.Length);
        Assert.True(manifestBefore.SequenceEqual(manifestAfter));

        Assert.Equal(400, merged.Count);
        Assert.Equal(400, order.Count);
        Assert.Equal(order, order.Distinct(StringComparer.Ordinal).ToList());

        Assert.True(attempted <= CallBudget, $"{attempted} calls against a ceiling of {CallBudget}.");
        Assert.Equal(attempted, succeeded + failed);

        // Every acquisition-ready member has a well-formed equity symbol and nothing else does.
        var readyRows = merged.Where(m => m.TryGetValue("Ready", out var v) && v is true).ToList();

        Assert.All(readyRows, m => Assert.Matches(
            "^[A-Z]{1,5}$", (m["Ticker"] as string) ?? string.Empty));
    }

    // ---- reading ------------------------------------------------------------------------------------

    private static List<Row> Load(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement
            .GetProperty("Results")
            .EnumerateArray()
            .Select(r => new Row(
                r.GetProperty("Cik").GetString() ?? string.Empty,
                r.GetProperty("Name").GetString() ?? string.Empty,
                r.GetProperty("Outcome").GetString() ?? string.Empty,
                Text(r, "Ticker"),
                Text(r, "Exchange"),
                Text(r, "SecurityTitle"),
                Text(r, "PreviousStatus") ?? "unknown",
                Text(r, "LastCohort") ?? "-",
                r.TryGetProperty("IsDropout", out var d) && d.ValueKind == JsonValueKind.True,
                Text(r, "Evidence") ?? string.Empty))
            .ToList();
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task<(string Accession, string Document, string Form, string FilingDate)?>
        FilingAsync(IServiceProvider root, string cik)
    {
        using var scope = root.CreateScope();

        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<AppDbContext>();
        var archive = services.GetRequiredService<IRawResponseArchive>();

        var runs = await context.IngestionRuns
            .AsNoTracking()
            .Where(r => r.Request.Category == DataCategory.CompanyProfile)
            .ToListAsync();

        var run = runs
            .Where(r => r.Outcome == IngestionOutcome.Succeeded)
            .LastOrDefault(r => string.Equals(
                r.Request.Subject.Identifier, cik, StringComparison.Ordinal));

        if (run is null)
        {
            return null;
        }

        foreach (var hash in run.Artifacts)
        {
            var payload = await archive.RetrieveAsync(hash);

            if (payload is null)
            {
                continue;
            }

            using var document = JsonDocument.Parse(payload);

            if (!document.RootElement.TryGetProperty("filings", out var filings) ||
                !filings.TryGetProperty("recent", out var recent))
            {
                continue;
            }

            var forms = Column(recent, "form");
            var accessions = Column(recent, "accessionNumber");
            var documents = Column(recent, "primaryDocument");
            var dates = Column(recent, "filingDate");

            var count = new[] { forms.Count, accessions.Count, documents.Count, dates.Count }.Min();

            for (var i = 0; i < count; i++)
            {
                if (forms[i].StartsWith("10-K", StringComparison.OrdinalIgnoreCase) &&
                    documents[i].Length > 0)
                {
                    return (accessions[i], documents[i], forms[i], dates[i]);
                }
            }
        }

        return null;
    }

    private static List<string> Column(JsonElement recent, string name) =>
        recent.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList()
            : [];

    private static string Url(string cik, string accession, string document) =>
        ArchiveBase
        + cik.TrimStart('0')
        + "/"
        + accession.Replace("-", string.Empty, StringComparison.Ordinal)
        + "/"
        + document;

    private static async Task<string> PrefixAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();

        var buffer = new byte[PrefixBytes];
        var read = 0;

        while (read < buffer.Length)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read));

            if (got == 0)
            {
                break;
            }

            read += got;
        }

        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static string? Value(string document, string tag)
    {
        foreach (var pattern in new[]
        {
            "name\\s*=\\s*\"(?:dei:)?" + tag + "\"[^>]*>(?<value>.{0,400}?)</",
            "<(?:\\w+:)?" + tag + "[^>]*>(?<value>.{0,400}?)</",
        })
        {
            var match = Regex.Match(
                document, pattern,
                RegexOptions.IgnoreCase | RegexOptions.Singleline,
                TimeSpan.FromSeconds(10));

            if (!match.Success)
            {
                continue;
            }

            var text = Regex.Replace(
                match.Groups["value"].Value, "<[^>]*>", string.Empty,
                RegexOptions.None, TimeSpan.FromSeconds(5));

            text = text.Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase).Trim();

            if (text.Length is > 0 and <= 128)
            {
                return text;
            }
        }

        return null;
    }

    private static async Task<Dns> ResolveAsync()
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(Host);

                if (addresses.Length > 0)
                {
                    return new Dns(true, string.Create(
                        CultureInfo.InvariantCulture,
                        $"{Host} resolved on attempt {attempt} of 3."));
                }
            }
            catch (SocketException)
            {
                // Retried below; a resolver failure costs no SEC request.
            }

            await Task.Delay(2000);
        }

        return new Dns(false, Host + " did not resolve in 3 attempts.");
    }

    private static bool IsTransport(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or SocketException;

    private static string Short(Exception exception) =>
        exception.Message.Length > 110 ? exception.Message[..110] : exception.Message;

    // ---- reporting -------------------------------------------------------------------------------------

    private static string Compose(
        List<Corrected> corrected,
        List<Corrected> acquirable,
        List<Corrected> refused,
        int members,
        int ready,
        int attempted,
        int succeeded,
        int failed,
        string retryNote)
    {
        var report = new StringBuilder();

        var recoveredDropouts = acquirable.Count(c => c.Row.IsDropout);
        var panelReady = ready;
        var panelDropouts = 5 + recoveredDropouts;

        report.AppendLine("# Full historical identity recovery - corrected");
        report.AppendLine();
        report.AppendLine("**No EODHD call. No price, split, dividend or corporate action. No");
        report.AppendLine("opportunity, score or order.** The sealed manifest is byte-identical and");
        report.AppendLine("its fingerprint is unchanged. **Price acquisition has NOT been started.**");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| SEC calls this stage | **{attempted}** of {CallBudget} |"));
        report.AppendLine(Universe.Inv($"| Succeeded / failed | {succeeded} / {failed} |"));
        report.AppendLine(Universe.Inv($"| EODHD calls | 0 |"));
        report.AppendLine(Universe.Inv($"| Price / split / dividend calls | 0 / 0 / 0 |"));
        report.AppendLine();
        report.AppendLine(Universe.Inv($"Retry: {retryNote}"));
        report.AppendLine();

        report.AppendLine("## The four non-equity identities, preserved and refused");
        report.AppendLine();
        report.AppendLine("Each is a correct reading of the filing and the wrong security to price.");
        report.AppendLine("None is deleted, none leaves the sealed universe, and each keeps the");
        report.AppendLine("symbol its cover page stated.");
        report.AppendLine();
        report.AppendLine("| CIK | Company | Symbol stated | Kind | Why it cannot be priced |");
        report.AppendLine("| --- | --- | --- | --- | --- |");

        foreach (var c in refused.OrderBy(c => c.Row.Cik, StringComparer.Ordinal))
        {
            report.AppendLine(Universe.Inv(
                $"| `{c.Row.Cik}` | {c.Row.Name} | `{c.Row.Ticker}` | {c.Classification?.Kind} | {c.Reason} |"));
        }

        report.AppendLine();
        report.AppendLine("## The corrected partition");
        report.AppendLine();
        report.AppendLine("| Outcome | Members |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Recovered **and acquirable** (equity) | **{acquirable.Count}** |"));
        report.AppendLine(Universe.Inv($"| Recovered but **not acquirable** (debt or not a symbol) | **{refused.Count}** |"));
        report.AppendLine(Universe.Inv($"| No listed security | {corrected.Count(c => c.Row.Outcome == RecoveryPartition.NoListedSecurity)} |"));
        report.AppendLine(Universe.Inv($"| Unresolved | {corrected.Count(c => c.Row.Outcome == RecoveryPartition.Unresolved)} |"));
        report.AppendLine(Universe.Inv($"| Transport failed | {corrected.Count(c => c.Row.Outcome == RecoveryPartition.TransportFailed)} |"));
        report.AppendLine(Universe.Inv($"| Total | {corrected.Count} |"));
        report.AppendLine();

        report.AppendLine(Universe.Inv($"Of the {acquirable.Count} acquirable, {acquirable.Count(c => c.Classification?.Confidence == "high")} carry a corroborating security title and {acquirable.Count(c => c.Classification?.Confidence == "moderate")} rest on the symbol alone, which is recorded on each row."));
        report.AppendLine();

        report.AppendLine("## The identity table, merged");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("| --- | ---: |");
        report.AppendLine(Universe.Inv($"| Members | {members} |"));
        report.AppendLine(Universe.Inv($"| **Acquisition-ready** | **{ready}** |"));
        report.AppendLine(Universe.Inv($"| Not ready | {members - ready} |"));
        report.AppendLine();
        report.AppendLine("## Gate 2 on the corrected panel");
        report.AppendLine();
        report.AppendLine("| | Members | Dropouts | Share | Gate 2 (floor 5%) |");
        report.AppendLine("| --- | ---: | ---: | ---: | --- |");
        report.AppendLine(Universe.Inv($"| Baseline before recovery | 269 | 5 | 1.9 % | **FAIL** |"));
        report.AppendLine(Universe.Inv(
            $"| **Corrected panel** | **{panelReady}** | **{panelDropouts}** | **{RecoveryPartition.PanelDropoutShare(panelReady, panelDropouts):P1}** | {(RecoveryPartition.ClearsGate2(panelReady, panelDropouts) ? "**PASS**" : "**FAIL**")} |"));
        report.AppendLine(Universe.Inv($"| Sealed universe | 400 | 115 | 28.8 % | PASS |"));
        report.AppendLine();
        report.AppendLine("Every member the recovery could not identify remains a member. The four");
        report.AppendLine("non-equity rows are recorded, not removed, and cannot be sent to a price");
        report.AppendLine("provider. Gate 2's floor is unchanged at 5% and the sealed manifest was");
        report.AppendLine("not touched.");

        return report.ToString();
    }

    private sealed record Dns(bool Resolved, string Summary);

    private sealed record Row(
        string Cik,
        string Name,
        string Outcome,
        string? Ticker,
        string? Exchange,
        string? SecurityTitle,
        string PreviousStatus,
        string LastCohort,
        bool IsDropout,
        string Evidence);

    private sealed record Corrected(
        Row Row,
        SecurityClassification.Classification? Classification,
        bool Acquirable,
        string Reason);
}
