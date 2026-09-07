using Xunit;

namespace AI.Investment.Api.Tests;

/// <summary>
/// The authorisation, held to the four things it exists to guarantee.
/// </summary>
/// <remarks>
/// <para>
/// No database, no container, no network - these run in the ordinary suite, which is the point. An
/// authorisation that could only be checked by a stage nobody runs twice is a comment with a
/// hash on it.
/// </para>
/// <para>
/// The four: the ceiling cannot be exceeded; nothing outside the authorised set can dispatch at
/// all; a suppressed request consumes none of the ceiling; and an edited file is refused rather
/// than obeyed.
/// </para>
/// </remarks>
public sealed class AcquisitionAuthorizationLedgerTests
{
    private const string EvidenceBase = "us-pit-sample400-2021-09-to-2026-08@f78cfe45b962";

    private static readonly DateOnly From = new(2021, 9, 1);
    private static readonly DateOnly To = new(2026, 8, 31);

    private static AcquisitionAuthorization Load() =>
        AcquisitionAuthorization.Load(
            Universe.RepositoryPath("declarations", "acquisition-eodhd-sample400.json"),
            EvidenceBase);

    [Fact]
    public void The_authorisation_on_disk_says_exactly_what_was_approved()
    {
        var authorization = Load();

        Assert.Equal(EvidenceBase, authorization.EvidenceBaseFingerprint);
        Assert.Equal("eodhd", authorization.Vendor);
        Assert.Equal(From, authorization.WindowFrom);
        Assert.Equal(To, authorization.WindowTo);

        Assert.True(authorization.Symbols.Count == 355, "the authorisation does not name 355 symbols");
        Assert.True(authorization.Sources.Count == 2, "the authorisation does not name two sources");
        Assert.Equal(710, authorization.PlannedRequests);
        Assert.Equal(6, authorization.AlreadySatisfied);
        Assert.Equal(704, authorization.DispatchCeiling);
        Assert.Equal(704, authorization.Remaining);
        Assert.Equal(0, authorization.Consumed);

        Assert.Contains("eodhd-eod", authorization.Sources);
        Assert.Contains("eodhd-splits", authorization.Sources);
    }

    /// <summary>
    /// The names in the authorisation are the names in the corrected identity table, and no others.
    /// </summary>
    [Fact]
    public async Task The_authorised_symbols_are_the_355_acquisition_ready_identities()
    {
        var authorization = Load();
        var ready = await ReadySymbolsAsync();

        Assert.True(
            authorization.Symbols.Count == ready.Count,
            "the authorisation names a different number of symbols than the identity table calls ready");

        Assert.All(authorization.Symbols, s => Assert.Contains(s, ready));

        // And every one is a symbol this platform can normalise rather than quarantine.
        Assert.All(authorization.Symbols, s => Assert.EndsWith(AcquisitionPlanning.UsSuffix, s, StringComparison.Ordinal));
    }

    /// <summary>
    /// The guarantee the whole artefact is for: it stops, and it stops at the approved number.
    /// </summary>
    [Fact]
    public void The_ceiling_cannot_be_exceeded()
    {
        var authorization = Load();
        var symbols = authorization.Symbols.Order(StringComparer.Ordinal).ToList();

        var granted = 0;

        // Ask for far more than the ceiling: both endpoints for every symbol is 710 against 704.
        foreach (var source in authorization.Sources.Order(StringComparer.Ordinal))
        {
            foreach (var symbol in symbols)
            {
                if (authorization.TryConsume(source, symbol, From, To).Allowed)
                {
                    granted++;
                }
            }
        }

        Assert.Equal(704, granted);
        Assert.Equal(704, authorization.Consumed);
        Assert.Equal(0, authorization.Remaining);

        // And the next one is refused in terms an operator can act on.
        var refused = authorization.TryConsume("eodhd-eod", symbols[0], From, To);

        Assert.False(refused.Allowed);
        Assert.Contains("ceiling of 704", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_outside_the_authorised_set_can_dispatch()
    {
        var authorization = Load();
        var symbol = authorization.Symbols.Order(StringComparer.Ordinal).First();

        // The declared validation benchmark, which is deliberately not authorised here.
        var benchmark = authorization.TryConsume("eodhd-eod", "SPY.US", From, To);

        Assert.False(benchmark.Allowed);
        Assert.Contains("not in the 355", benchmark.Reason, StringComparison.Ordinal);

        // Dividends have no source, and asking for one is refused rather than ignored.
        var dividends = authorization.TryConsume("eodhd-dividends", symbol, From, To);

        Assert.False(dividends.Allowed);
        Assert.Contains("not authorised", dividends.Reason, StringComparison.Ordinal);

        // A window one day wider is a different request and is not covered by this approval.
        var wider = authorization.TryConsume("eodhd-eod", symbol, From, To.AddDays(1));

        Assert.False(wider.Allowed);
        Assert.Contains("is not the authorised", wider.Reason, StringComparison.Ordinal);

        // None of the three consumed anything.
        Assert.Equal(0, authorization.Consumed);
        Assert.Equal(704, authorization.Remaining);
    }

    /// <summary>
    /// A request that never leaves the process must not spend an authorisation it never used.
    /// </summary>
    [Fact]
    public void A_suppressed_request_consumes_no_authorisation()
    {
        var authorization = Load();
        var symbols = authorization.Symbols.Order(StringComparer.Ordinal).Take(10).ToList();

        // Six of these are already in the ledger in the real plan; here, ten are suppressed.
        foreach (var _ in symbols)
        {
            authorization.RecordSuppressed();
        }

        Assert.Equal(10, authorization.Suppressed);
        Assert.Equal(0, authorization.Consumed);
        Assert.Equal(704, authorization.Remaining);

        // A dispatch after them consumes exactly one.
        Assert.True(authorization.TryConsume("eodhd-eod", symbols[0], From, To).Allowed);
        Assert.Equal(1, authorization.Consumed);
        Assert.Equal(703, authorization.Remaining);
    }

    /// <summary>
    /// A batch loads the declaration afresh, so the ceiling has to survive the process that ended.
    /// </summary>
    /// <remarks>
    /// Without this the approved total is spendable once per batch: three runs of seventy, each
    /// starting from a counter at zero, each certain it had 704 left.
    /// </remarks>
    [Fact]
    public void Dispatches_made_by_an_earlier_process_still_count_against_the_ceiling()
    {
        var authorization = Load();

        authorization.RecordPriorConsumption(700);

        Assert.Equal(700, authorization.Consumed);
        Assert.Equal(4, authorization.Remaining);

        var symbols = authorization.Symbols.Order(StringComparer.Ordinal).Take(6).ToList();
        var granted = 0;

        foreach (var symbol in symbols)
        {
            if (authorization.TryConsume("eodhd-eod", symbol, From, To).Allowed)
            {
                granted++;
            }
        }

        // Four, not six: the counter carried the earlier process's spending across.
        Assert.Equal(4, granted);
        Assert.Equal(704, authorization.Consumed);
        Assert.Equal(0, authorization.Remaining);

        // Headroom is never given back, and over-spending is refused rather than clamped.
        var refused = Assert.Throws<InvalidOperationException>(
            () => Load().RecordPriorConsumption(705));

        Assert.Contains("cannot be over-spent", refused.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => Load().RecordPriorConsumption(-1));
    }

    /// <summary>
    /// Scope is judged before the counter, so an unauthorised request never reads as "in budget".
    /// </summary>
    [Fact]
    public void Scope_is_judged_before_the_ceiling()
    {
        var authorization = Load();

        var verdict = authorization.Covers("eodhd-eod", "SPY.US", From, To);

        Assert.False(verdict.Allowed);
        Assert.DoesNotContain("ceiling", verdict.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_authorisation_for_another_universe_is_refused()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AcquisitionAuthorization.Load(
                Universe.RepositoryPath("declarations", "acquisition-eodhd-sample400.json"),
                "some-other-universe@0000deadbeef"));

        Assert.Contains("does not carry across a reseal", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An edited file is refused, which is what makes the ceiling worth writing down.
    /// </summary>
    [Fact]
    public void An_edited_authorisation_is_refused_rather_than_obeyed()
    {
        var original = File.ReadAllText(
            Universe.RepositoryPath("declarations", "acquisition-eodhd-sample400.json"));

        // A plausible edit: someone raises the ceiling and leaves the digest alone.
        var tampered = original.Replace(
            "\"DispatchCeiling\": 704", "\"DispatchCeiling\": 7040", StringComparison.Ordinal);

        Assert.NotEqual(original, tampered);

        var path = Path.Combine(Path.GetTempPath(), $"authorization-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, tampered);

            var exception = Assert.Throws<InvalidOperationException>(
                () => AcquisitionAuthorization.Load(path, EvidenceBase));

            Assert.Contains("digest does not match", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }


    // ---- supersession, and the two fields that had to become evidence ---------------------------

    /// <summary>
    /// The authorisation already on disk is the original schema and carries neither new field.
    /// </summary>
    /// <remarks>
    /// The price authorisation is kept as an immutable historical record rather than amended, so
    /// this is also the assertion that the second schema did not disturb the first: if adding it
    /// had changed the '@1' canonical form, this file's stated digest would no longer match and
    /// <see cref="AcquisitionAuthorization.Load"/> would refuse it.
    /// </remarks>
    [Fact]
    public void The_price_authorisation_is_the_original_schema_and_supersedes_nothing()
    {
        var authorization = Load();

        Assert.Equal(AcquisitionAuthorization.Schema, authorization.SchemaVersion);
        Assert.Null(authorization.SupersedesAuthorizationId);
        Assert.Equal(0, authorization.AlreadyConsumed);
    }

    /// <summary>A superseding authorisation reads back exactly what it declares.</summary>
    [Fact]
    public void A_superseding_authorisation_carries_what_it_replaces_and_what_that_cost()
    {
        var authorization = LoadTemporary(Superseding());

        Assert.Equal(AcquisitionAuthorization.SupersedingSchema, authorization.SchemaVersion);
        Assert.Equal("the-original", authorization.SupersedesAuthorizationId);
        Assert.Equal(414, authorization.AlreadyConsumed);

        // Evidence, not a charge: the ceiling is the work that remains, not the budget less what
        // the previous authorisation spent.
        Assert.Equal(2, authorization.DispatchCeiling);
        Assert.Equal(2, authorization.Remaining);
        Assert.Equal(0, authorization.Consumed);
    }

    /// <summary>
    /// Editing what it supersedes invalidates it. This is the property the change exists for.
    /// </summary>
    [Theory]
    [InlineData("\"SupersedesAuthorizationId\": \"the-original\"", "\"SupersedesAuthorizationId\": \"something-else\"")]
    [InlineData("\"AlreadyConsumed\": 414", "\"AlreadyConsumed\": 0")]
    [InlineData("\"AlreadyConsumed\": 414", "\"AlreadyConsumed\": 9999")]
    public void Editing_a_superseding_field_makes_the_declaration_refuse_to_load(
        string original,
        string tampered)
    {
        var edited = Superseding().Replace(original, tampered, StringComparison.Ordinal);

        Assert.NotEqual(Superseding(), edited);

        var exception = Assert.Throws<InvalidOperationException>(() => LoadTemporary(edited));

        Assert.Contains("digest does not match", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The original schema may not carry those fields at all, rather than carrying them unprotected.
    /// </summary>
    /// <remarks>
    /// The failure mode this closes: a declaration that names what it replaces and how much was
    /// spent, reads as an auditable chain, and can have either value rewritten without the digest
    /// noticing. Absent is honest; present-and-unprotected is not.
    /// </remarks>
    [Theory]
    [InlineData("\"SupersedesAuthorizationId\": \"the-original\",")]
    [InlineData("\"AlreadyConsumed\": 414,")]
    public void The_original_schema_refuses_to_carry_a_field_its_digest_cannot_protect(string field)
    {
        var smuggled = Superseding()
            .Replace(AcquisitionAuthorization.SupersedingSchema, AcquisitionAuthorization.Schema, StringComparison.Ordinal)
            .Replace("\"SupersedesAuthorizationId\": \"the-original\",", string.Empty, StringComparison.Ordinal)
            .Replace("\"AlreadyConsumed\": 414,", string.Empty, StringComparison.Ordinal)
            .Replace("\"Vendor\":", field + "\n  \"Vendor\":", StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidOperationException>(() => LoadTemporary(smuggled));

        Assert.Contains("digest does not cover them", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A successor that names no predecessor is an original pretending to be one.</summary>
    /// <remarks>
    /// Re-digested rather than edited, so the file is internally consistent and the refusal is
    /// about what it means rather than about the digest catching a change.
    /// </remarks>
    [Fact]
    public void A_superseding_authorisation_must_name_what_it_supersedes()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => LoadTemporary(Superseding(supersedes: string.Empty)));

        Assert.Contains("must name the authorisation it", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Negative prior spending would be a credit-back this platform does not have.</summary>
    [Fact]
    public void A_superseding_authorisation_cannot_claim_a_negative_prior_spend()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => LoadTemporary(Superseding(alreadyConsumed: -1)));

        Assert.Contains("cannot be negative", exception.Message, StringComparison.Ordinal);
    }

    // ---- a minimal, internally consistent superseding declaration --------------------------------

    private static string Superseding(
        string supersedes = "the-original",
        int alreadyConsumed = 414)
    {
        string[] symbols = ["AAA.US", "BBB.US"];

        var digest = AcquisitionAuthorization.DigestFor(
            AcquisitionAuthorization.SupersedingSchema,
            EvidenceBase,
            "eodhd",
            ["eodhd-splits"],
            From,
            To,
            symbols.Length,
            0,
            symbols.Length,
            symbols,
            supersedes,
            alreadyConsumed);

        return $$"""
            {
              "Schema": "{{AcquisitionAuthorization.SupersedingSchema}}",
              "AuthorizationId": "the-successor",
              "EvidenceBaseFingerprint": "{{EvidenceBase}}",
              "SupersedesAuthorizationId": "{{supersedes}}",
              "AlreadyConsumed": {{alreadyConsumed}},
              "Vendor": "eodhd",
              "Sources": [ "eodhd-splits" ],
              "WindowFromUtc": "2021-09-01",
              "WindowToUtc": "2026-08-31",
              "PlannedRequests": 2,
              "AlreadySatisfied": 0,
              "DispatchCeiling": 2,
              "AuthorizationDigest": "{{digest}}",
              "Symbols": [ "AAA.US", "BBB.US" ]
            }
            """;
    }

    private static AcquisitionAuthorization LoadTemporary(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"authorization-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, content);

            return AcquisitionAuthorization.Load(path, EvidenceBase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<HashSet<string>> ReadySymbolsAsync()
    {
        using var identity = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(
            Universe.RepositoryPath("artifacts", "universe", "identity-sample400.json")));

        return identity.RootElement
            .GetProperty("Members")
            .EnumerateArray()
            .Where(m => m.TryGetProperty("Ready", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.True)
            .Select(m => AcquisitionPlanning.SymbolFor(m.GetProperty("Ticker").GetString()!))
            .ToHashSet(StringComparer.Ordinal);
    }
}
