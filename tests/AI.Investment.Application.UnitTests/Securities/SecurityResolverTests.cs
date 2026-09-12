using System.Reflection;
using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Securities;
using AI.Investment.Domain.Securities;
using AI.Investment.Domain.Sources;
using Xunit;

namespace AI.Investment.Application.UnitTests.Securities;

/// <summary>
/// What the resolver answers, and - more often - what it refuses to answer.
/// </summary>
/// <remarks>
/// <para>
/// Most of these facts are about the boundaries. A point-in-time resolver that leaked across a
/// source, a kind or an interval would still pass a happy-path test and would be wrong exactly where
/// the platform relies on it: attaching a corporate action to the instrument that bore a symbol on
/// the day it happened.
/// </para>
/// <para>
/// Exercised against a fake so that the ambiguity case can be constructed at all. The database
/// refuses two assertions of one kind and value outright, so ambiguity cannot be produced there -
/// that invariant is verified separately, and the resolver's behaviour if it were ever breached is
/// verified here.
/// </para>
/// </remarks>
public sealed class SecurityResolverTests
{
    private static readonly SourceId Eodhd = SourceId.Create("eodhd");
    private static readonly SourceId Edgar = SourceId.Create("sec-edgar");

    private static readonly DateOnly From = new(2021, 9, 1);
    private static readonly DateOnly To = new(2025, 2, 4);

    private static readonly SecurityId Held = SecurityId.Create(
        Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static readonly SecurityId Other = SecurityId.Create(
        Guid.Parse("22222222-2222-2222-2222-222222222222"));

    /// <summary>A. The one shape that resolves.</summary>
    [Fact]
    public async Task An_exact_vendor_symbol_inside_its_interval_resolves()
    {
        var result = await Resolve("AE.US", Eodhd, At(2023, 1, 1));

        Assert.True(result.IsResolved);
        Assert.Equal(Held, result.SecurityId);
        Assert.Equal(SecurityResolutionFailure.None, result.Failure);
        Assert.Empty(result.Candidates);
    }

    /// <summary>B, V. The source is part of the question, not a label on it.</summary>
    [Fact]
    public async Task The_same_symbol_asked_of_a_different_source_does_not_resolve()
    {
        var result = await Resolve("AE.US", Edgar, At(2023, 1, 1));

        Assert.True(result.IsUnresolved);
        Assert.Equal(SecurityResolutionFailure.NoMatchingSource, result.Failure);
        Assert.Null(result.SecurityId);
    }

    /// <summary>C, W. A vendor key is not a ticker, and the resolver never converts one.</summary>
    [Fact]
    public async Task The_same_value_asked_as_a_different_kind_does_not_resolve()
    {
        var result = await Resolve("AE.US", Eodhd, At(2023, 1, 1), SecurityIdentifierKind.Ticker);

        Assert.True(result.IsUnresolved);

        // No ticker assertion is held at all, so the honest answer is that the question cannot be
        // answered from evidence - not that this particular symbol is unknown.
        Assert.Equal(SecurityResolutionFailure.NoTemporalIdentifierEvidence, result.Failure);
    }

    /// <summary>D. An unknown value, in a kind that is otherwise evidenced.</summary>
    [Fact]
    public async Task An_unknown_vendor_symbol_is_reported_as_not_persisted()
    {
        var result = await Resolve("NOPE.US", Eodhd, At(2023, 1, 1));

        Assert.True(result.IsUnresolved);
        Assert.Equal(SecurityResolutionFailure.IdentifierNotPersisted, result.Failure);
    }

    /// <summary>E, F, G. The interval boundaries, exactly as the domain defines them.</summary>
    /// <remarks>
    /// <c>CoversDate</c> is inclusive at both ends. These four cases pin that rather than restate
    /// it: an off-by-one here would silently move which instrument a corporate action attaches to on
    /// the first and last day of every series.
    /// </remarks>
    [Theory]
    [InlineData(2021, 8, 31, false)]  // one day before ValidFrom
    [InlineData(2021, 9, 1, true)]    // exactly ValidFrom
    [InlineData(2025, 2, 4, true)]    // exactly ValidTo
    [InlineData(2025, 2, 5, false)]   // one day after ValidTo
    public async Task The_validity_interval_is_inclusive_at_both_ends(
        int year, int month, int day, bool resolves)
    {
        var result = await Resolve("AE.US", Eodhd, At(year, month, day));

        Assert.Equal(resolves, result.IsResolved);

        if (!resolves)
        {
            Assert.Equal(SecurityResolutionFailure.OutsideIdentifierValidity, result.Failure);
        }
    }

    /// <summary>An open-ended assertion has no upper bound, and still has a lower one.</summary>
    [Fact]
    public async Task An_open_ended_assertion_covers_everything_after_its_start_and_nothing_before()
    {
        var open = new FakeSecurityRepository
        {
            Assertions =
            {
                (Held, SecurityIdentifier.Assert(
                    SecurityIdentifierKind.VendorSymbol, "AE.US", From, null, Eodhd)),
            },
        };

        var resolver = new SecurityResolver(open);

        Assert.True((await resolver.ResolveAsync(
            SecurityIdentifierKind.VendorSymbol, "AE.US", Eodhd, At(2099, 1, 1))).IsResolved);

        Assert.True((await resolver.ResolveAsync(
            SecurityIdentifierKind.VendorSymbol, "AE.US", Eodhd, At(2020, 1, 1))).IsUnresolved);
    }

    /// <summary>H. A ticker at a historical date, with nothing dated to answer from.</summary>
    /// <remarks>
    /// The case section 6 of the brief names. Nothing here resolves through
    /// <c>TBCH.US -&gt; Company -&gt; current security</c>, and nothing manufactures an interval to
    /// make the query answerable.
    /// </remarks>
    [Fact]
    public async Task A_ticker_at_a_historical_date_is_unresolved_for_want_of_dated_evidence()
    {
        var result = await Resolve("TBCH", Edgar, At(2021, 9, 1), SecurityIdentifierKind.Ticker);

        Assert.True(result.IsUnresolved);
        Assert.Equal(SecurityResolutionFailure.NoTemporalIdentifierEvidence, result.Failure);
        Assert.Null(result.SecurityId);
        Assert.Empty(result.Candidates);
    }

    /// <summary>K, L. Two matching assertions are reported, never narrowed to a winner.</summary>
    [Fact]
    public async Task Two_matching_assertions_are_ambiguous_and_every_candidate_is_returned()
    {
        var contradictory = new FakeSecurityRepository
        {
            Assertions =
            {
                (Other, SecurityIdentifier.Assert(
                    SecurityIdentifierKind.VendorSymbol, "AE.US", From, To, Eodhd)),
                (Held, SecurityIdentifier.Assert(
                    SecurityIdentifierKind.VendorSymbol, "AE.US", From, To, Eodhd)),
            },
        };

        var result = await new SecurityResolver(contradictory).ResolveAsync(
            SecurityIdentifierKind.VendorSymbol, "AE.US", Eodhd, At(2023, 1, 1));

        Assert.True(result.IsAmbiguous);
        Assert.Null(result.SecurityId);
        Assert.Equal(SecurityResolutionFailure.None, result.Failure);

        // Ordered by id, not by the order the store happened to hold them, so a repeated query
        // gives a caller the same list to reason about.
        Assert.Equal([Held, Other], result.Candidates);
    }

    /// <summary>M. The same question against the same state gives the same answer.</summary>
    [Fact]
    public async Task Resolution_is_deterministic_across_repeated_calls()
    {
        var repository = Repository();
        var resolver = new SecurityResolver(repository);

        var answers = new List<(bool, SecurityId?, SecurityResolutionFailure)>();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var result = await resolver.ResolveAsync(
                SecurityIdentifierKind.VendorSymbol, "AE.US", Eodhd, At(2023, 1, 1));

            answers.Add((result.IsResolved, result.SecurityId, result.Failure));
        }

        Assert.Single(answers.Distinct());
    }

    /// <summary>T. The instant is mandatory and must be UTC.</summary>
    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task A_non_utc_instant_is_refused_rather_than_answered(DateTimeKind kind)
    {
        var resolver = new SecurityResolver(Repository());

        var instant = DateTime.SpecifyKind(new DateTime(2023, 1, 1, 0, 0, 0), kind);

        var thrown = await Assert.ThrowsAsync<ArgumentException>(
            () => resolver.ResolveAsync(
                SecurityIdentifierKind.VendorSymbol, "AE.US", Eodhd, instant));

        Assert.Contains("UTC", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>An unusable kind or a blank value says so rather than searching.</summary>
    [Fact]
    public async Task An_unusable_question_is_refused_with_its_own_reason()
    {
        var resolver = new SecurityResolver(Repository());

        var unknownKind = await resolver.ResolveAsync(
            SecurityIdentifierKind.Unknown, "AE.US", Eodhd, At(2023, 1, 1));

        Assert.Equal(SecurityResolutionFailure.UnsupportedIdentifierKind, unknownKind.Failure);

        var blank = await resolver.ResolveAsync(
            SecurityIdentifierKind.VendorSymbol, "   ", Eodhd, At(2023, 1, 1));

        Assert.Equal(SecurityResolutionFailure.NoIdentifier, blank.Failure);
    }

    /// <summary>I, J. The resolver cannot reach a company, so it cannot resolve through one.</summary>
    /// <remarks>
    /// Structural rather than promised. Its constructor names every collaborator it has, and a
    /// company repository is not among them - so <c>CIK -&gt; Company -&gt; whichever security</c>
    /// is not an implementation choice that could be revisited, it is unavailable.
    /// </remarks>
    [Fact]
    public void The_resolver_cannot_reach_a_company_a_provider_or_anything_that_writes()
    {
        var dependencies = typeof(SecurityResolver)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(p => p.ParameterType.Name)
            .ToList();

        Assert.Equal(["ISecurityRepository"], dependencies);

        Assert.DoesNotContain(
            dependencies,
            d => d.Contains("Company", StringComparison.OrdinalIgnoreCase)
                || d.Contains("Provider", StringComparison.OrdinalIgnoreCase)
                || d.Contains("Connector", StringComparison.OrdinalIgnoreCase)
                || d.Contains("Gateway", StringComparison.OrdinalIgnoreCase)
                || d.Contains("Authorization", StringComparison.OrdinalIgnoreCase)
                || d.Contains("UnitOfWork", StringComparison.OrdinalIgnoreCase)
                || d.Contains("Observation", StringComparison.OrdinalIgnoreCase)
                || d.Contains("CorporateAction", StringComparison.OrdinalIgnoreCase)
                || d.Contains("Listing", StringComparison.OrdinalIgnoreCase)
                || d.Contains("Venue", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>U. No method on the resolver reads the clock.</summary>
    /// <remarks>
    /// The IL of every method is walked for a call to <c>DateTime.Now</c> or <c>DateTime.UtcNow</c>,
    /// the same technique the validation rules use for retrieval time. Searching the source text
    /// would have been easier and would have missed a read arriving through a local function or an
    /// expression tree. A resolver that asked what time it was could not replay a past decision.
    /// </remarks>
    [Fact]
    public void No_method_on_the_resolver_reads_the_clock()
    {
        var clocks = new[]
        {
            typeof(DateTime).GetProperty(nameof(DateTime.Now))!.GetGetMethod()!,
            typeof(DateTime).GetProperty(nameof(DateTime.UtcNow))!.GetGetMethod()!,
            typeof(DateTimeOffset).GetProperty(nameof(DateTimeOffset.Now))!.GetGetMethod()!,
            typeof(DateTimeOffset).GetProperty(nameof(DateTimeOffset.UtcNow))!.GetGetMethod()!,
        };

        var offenders = typeof(SecurityResolver)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.DeclaringType == typeof(SecurityResolver))
            .Where(m => clocks.Any(clock => Calls(m, clock)))
            .Select(m => m.Name)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "the resolver reads ambient time, so the same question would answer differently "
                + "tomorrow: " + string.Join(", ", offenders));
    }

    private static bool Calls(MethodBase method, MethodBase target)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();

        if (il is null)
        {
            return false;
        }

        for (var index = 0; index + 4 < il.Length; index++)
        {
            // call (0x28) and callvirt (0x6F) are the two ways a property getter is reached.
            if (il[index] is not (0x6F or 0x28))
            {
                continue;
            }

            try
            {
                if (method.Module.ResolveMethod(BitConverter.ToInt32(il, index + 1)) == target)
                {
                    return true;
                }
            }
#pragma warning disable CA1031 // A token that is not a method token is simply not this call.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Scanning byte by byte means operands of other instructions are read as tokens.
            }
        }

        return false;
    }

    private static DateTime At(int year, int month, int day) =>
        new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static Task<SecurityResolution> Resolve(
        string value,
        SourceId source,
        DateTime asOfUtc,
        SecurityIdentifierKind kind = SecurityIdentifierKind.VendorSymbol) =>
        new SecurityResolver(Repository()).ResolveAsync(kind, value, source, asOfUtc);

    /// <summary>One held vendor-symbol assertion, which is G2's shape exactly.</summary>
    private static FakeSecurityRepository Repository() =>
        new()
        {
            Assertions =
            {
                (Held, SecurityIdentifier.Assert(
                    SecurityIdentifierKind.VendorSymbol, "AE.US", From, To, Eodhd)),
            },
        };

    /// <summary>
    /// A store of assertions, answering the three resolution queries the way the database does.
    /// </summary>
    private sealed class FakeSecurityRepository : ISecurityRepository
    {
        public List<(SecurityId Security, SecurityIdentifier Identifier)> Assertions { get; } = [];

        public Task<IReadOnlyList<SecurityId>> FindByIdentifierAsAtAsync(
            SecurityIdentifierKind kind,
            string value,
            SourceId source,
            DateOnly asOf,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SecurityId>>(
                Assertions
                    .Where(a => a.Identifier.Kind == kind
                        && a.Identifier.Value == value
                        && a.Identifier.SourceId == source
                        && a.Identifier.CoversDate(asOf))
                    .Select(a => a.Security)
                    .Distinct()
                    .OrderBy(id => id.Value)
                    .ToList());

        public Task<(int OfKind, int OfKindAndValue)> CountIdentifierAssertionsAsync(
            SecurityIdentifierKind kind,
            string value,
            CancellationToken cancellationToken = default) =>
            Task.FromResult((
                Assertions.Count(a => a.Identifier.Kind == kind),
                Assertions.Count(a => a.Identifier.Kind == kind && a.Identifier.Value == value)));

        public Task<bool> AnyIdentifierFromSourceAsync(
            SecurityIdentifierKind kind,
            string value,
            SourceId source,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Assertions.Any(a =>
                a.Identifier.Kind == kind
                && a.Identifier.Value == value
                && a.Identifier.SourceId == source));

        public Task<Security?> GetByIdAsync(SecurityId id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The resolver does not load securities.");

        public Task<Security?> FindByIdentifierAsync(
            SecurityIdentifierKind kind,
            string value,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The resolver resolves as at an instant, never currently.");

        public void Add(Security security) =>
            throw new NotSupportedException("Resolution never writes.");
    }
}
