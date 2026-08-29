using System.ComponentModel.DataAnnotations;
using System.Reflection;
using AI.Investment.Application.Abstractions;
using AI.Investment.Domain.Sources;
using AI.Investment.Infrastructure.Configuration;
using AI.Investment.Infrastructure.Ingestion.Providers;
using Microsoft.Extensions.Options;
using Xunit;

namespace AI.Investment.Integration.Tests.Ingestion;

/// <summary>
/// The EODHD connector's configuration, and the credential it must never leak.
/// </summary>
public sealed class EodhdConfigurationTests
{
    private static readonly DateTime Now = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The stand-in credential these tests prove never escapes. Unmistakably synthetic - a
    /// sentence with spaces in it, of a shape no vendor issues.
    /// </summary>
    private const string Secret = EodhdTestOptions.SyntheticKey;

    /// <summary>A connector nobody switched on validates, and is simply absent.</summary>
    [Fact]
    public void A_disabled_connector_needs_nothing() =>
        Assert.Empty(Validate(new EodhdOptions()));

    [Fact]
    public void An_enabled_connector_without_a_key_is_refused() =>
        Assert.Contains(
            Validate(Enabled(key: null)),
            problem => problem.MemberNames.Contains(nameof(EodhdOptions.ApiKey)));

    [Fact]
    public void An_enabled_connector_without_stated_licensing_is_refused() =>
        Assert.Contains(
            Validate(Enabled(licensing: null)),
            problem => problem.MemberNames.Contains(nameof(EodhdOptions.LicensingNotes)));

    /// <summary>
    /// Without a stated session there are no instants, and without instants there is no
    /// point-in-time correctness. Refused at configuration rather than once per payload.
    /// </summary>
    [Fact]
    public void An_enabled_connector_with_no_exchange_is_refused() =>
        Assert.Contains(
            Validate(Enabled(exchanges: [])),
            problem => problem.MemberNames.Contains(nameof(EodhdOptions.Exchanges)));

    /// <summary>The key travels in the query string, so plaintext http would put it on the wire.</summary>
    [Fact]
    public void A_plaintext_base_address_is_refused() =>
        Assert.Contains(
            Validate(Enabled(baseAddress: "http://eodhd.com/")),
            problem => problem.MemberNames.Contains(nameof(EodhdOptions.BaseAddress)));

    [Theory]
    [InlineData(-1)]
    [InlineData(200)]
    public void An_impossible_publication_delay_is_refused(int hours) =>
        Assert.NotEmpty(Validate(Enabled(exchanges:
        [
            new ExchangeSessionOptions
            {
                Code = "US",
                PublicationDelay = TimeSpan.FromHours(hours),
            },
        ])));

    [Fact]
    public void The_same_exchange_cannot_be_stated_twice() =>
        Assert.NotEmpty(Validate(Enabled(exchanges:
        [
            new ExchangeSessionOptions { Code = "US" },
            new ExchangeSessionOptions { Code = "us" },
        ])));

    [Fact]
    public void An_exchange_with_no_code_is_refused() =>
        Assert.NotEmpty(Validate(Enabled(exchanges: [new ExchangeSessionOptions()])));

    [Fact]
    public void A_session_is_found_by_exchange_code_regardless_of_case()
    {
        var options = Enabled();

        Assert.NotNull(options.Session("US"));
        Assert.NotNull(options.Session("us"));
        Assert.Null(options.Session("LSE"));
        Assert.Null(options.Session(""));
    }

    // ---- the registry entry -----------------------------------------------------------------

    /// <summary>A connector shipping in the box does not get to switch itself on.</summary>
    [Fact]
    public void The_definition_is_registered_inactive() =>
        Assert.False(Definition().IsActive);

    [Fact]
    public void The_definition_is_a_secondary_vendor_needing_corroboration()
    {
        var source = Definition();

        Assert.Equal(SourceType.DataVendor, source.Type);
        Assert.Equal(SourceAuthority.Secondary, source.Authority);
        Assert.Equal(VerificationPolicy.RequiresCorroboration, source.Verification);
        Assert.Contains(DataCategory.MarketPrices, source.Categories);
    }

    /// <summary>
    /// The operator states the terms. A registry entry that guessed would record a licensing claim
    /// nobody made.
    /// </summary>
    [Fact]
    public void Unstated_terms_are_recorded_as_unstated()
    {
        var source = new EodhdSource(Options.Create(Enabled(licensing: null))).Definition(Now);

        Assert.Equal(EodhdSource.UnstatedTerms, source.Licensing.Notes);
        Assert.False(source.Licensing.RedistributionAllowed);
    }

    [Fact]
    public void The_definition_and_the_connector_agree_on_the_source_id() =>
        Assert.Equal(EodhdProvider.Id, Definition().Id);

    /// <summary>
    /// The existing admission rules, unchanged, on the new source. Activation is still an
    /// operator's act through the seam; this only shows the definition is admissible once it is.
    /// </summary>
    [Fact]
    public void The_definition_is_admissible_for_market_prices_once_activated()
    {
        var source = Definition();
        source.Activate(Now);

        var admission = SourceAdmission.Evaluate(source, DataCategory.MarketPrices, Region.Global);

        Assert.True(admission.IsAdmitted);
    }

    /// <summary>A vendor feed does not outrank the venue that produced the print.</summary>
    [Fact]
    public void The_definition_is_not_authoritative() =>
        Assert.False(Definition().IsAuthoritative);

    // ---- secret non-disclosure ---------------------------------------------------------------

    /// <summary>
    /// The registry entry is what the operator console and every source listing read. The
    /// credential is not part of it, and nothing about the connector's description names it.
    /// </summary>
    [Fact]
    public void The_registry_entry_never_names_the_key()
    {
        var source = Definition();

        Assert.DoesNotContain(Secret, source.Description!, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, source.Licensing.Notes!, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, source.Name, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, source.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Neither the connector nor its source definition exposes the credential through a public
    /// member. Anything that did would be one <c>JsonSerializer.Serialize</c> away from a response
    /// body.
    /// </summary>
    [Fact]
    public void No_public_member_of_the_connector_returns_the_key()
    {
        var provider = Provider();
        var source = new EodhdSource(Options.Create(Enabled()));

        Assert.DoesNotContain(Secret, Readable(provider), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, Readable(source), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, provider.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shipped defaults carry no credential. Asserting the shape the repository commits means
    /// an edit that pastes a real value has to delete a test to do it.
    /// </summary>
    [Fact]
    public void The_shipped_defaults_carry_no_credential()
    {
        var shipped = new EodhdOptions();

        Assert.False(shipped.Enabled);
        Assert.Empty(shipped.ApiKey);
        Assert.Empty(shipped.Exchanges);
        Assert.Empty(shipped.LicensingNotes);
        Assert.False(shipped.RedistributionAllowed);
    }

    /// <summary>
    /// A redaction that handled only the raw form would miss the escaped one - which is the form
    /// that actually appears in a URI.
    /// </summary>
    [Fact]
    public void The_connector_redacts_both_the_raw_and_the_escaped_key()
    {
        const string awkward = "key with spaces/and+symbols";

        var provider = Provider(awkward);

        var raw = provider.Redact($"failed for api_token={awkward}");
        var escaped = provider.Redact($"failed for api_token={Uri.EscapeDataString(awkward)}");

        Assert.DoesNotContain(awkward, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(Uri.EscapeDataString(awkward), escaped, StringComparison.Ordinal);
        Assert.Contains(EodhdProvider.Redaction, raw, StringComparison.Ordinal);
        Assert.Contains(EodhdProvider.Redaction, escaped, StringComparison.Ordinal);
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>Every public instance property's value, as text.</summary>
    private static string Readable(object instance)
    {
        var text = new System.Text.StringBuilder();

        foreach (var property in instance.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            text.Append(property.GetValue(instance)).Append('|');
        }

        return text.ToString();
    }

    private static EodhdProvider Provider(string key = Secret) =>
        new(
            new HttpClient { BaseAddress = new Uri("https://eodhd.test/") },
            Options.Create(Enabled(key: key)),
            new StoppedClock());

    private static DataSource Definition() =>
        new EodhdSource(Options.Create(Enabled())).Definition(Now);

    /// <summary>
    /// The options under test, bound from configuration exactly as the application binds them.
    /// </summary>
    /// <remarks>
    /// A null key means the setting is absent from configuration altogether, which is the state an
    /// installation that has configured nothing is actually in - a likelier mistake than an empty
    /// string, and the one these tests are about.
    /// </remarks>
    private static EodhdOptions Enabled(
        string? key = Secret,
        string? licensing = "Personal plan; storage permitted, redistribution not.",
        string baseAddress = EodhdOptions.DefaultBaseAddress,
        IReadOnlyList<ExchangeSessionOptions>? exchanges = null) =>
        EodhdTestOptions.Build(
            enabled: true,
            credential: key,
            licensing: licensing,
            baseAddress: baseAddress,
            exchanges: exchanges);

    private static List<ValidationResult> Validate(EodhdOptions options)
    {
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(
            options,
            new ValidationContext(options),
            results,
            validateAllProperties: true);

        return results;
    }

    private sealed class StoppedClock : IClock
    {
        public DateTime UtcNow => Now;
    }
}
