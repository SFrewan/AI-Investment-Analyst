using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using AI.Investment.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AI.Investment.Integration.Tests.Ingestion;

/// <summary>
/// The credential arrives from the environment, and nothing here ever says what it is.
/// </summary>
/// <remarks>
/// <para>
/// Every assertion in this file is about presence, shape, provenance or a fingerprint. None
/// compares a credential to an expected value, and none writes one to test output. That is a
/// deliberate constraint rather than an accident of what was convenient: a test that asserts a
/// secret equals a literal has put the secret in the repository, and a test that prints one on
/// failure has put it in a CI log, where it will outlive the branch.
/// </para>
/// <para>
/// The values used here are synthetic, so the constraint costs nothing today. It is kept anyway,
/// because the day it stops being free is the day somebody points these tests at a real
/// installation - which is exactly what <c>scripts/check-eodhd-credential.cmd</c> does.
/// </para>
/// </remarks>
public sealed class EodhdCredentialSourcingTests
{
    /// <summary>The synthetic stand-in. A sentence with spaces, of a shape no vendor issues.</summary>
    private const string Synthetic = EodhdTestOptions.SyntheticKey;

    // ---- the two names, derived rather than restated -----------------------------------------

    /// <summary>
    /// The configuration path is the section plus the property, and nothing else.
    /// </summary>
    /// <remarks>
    /// Assembled here the same way the options class assembles it, so this fails if either half
    /// moves - which is the point. A hand-written expectation would only fail if somebody
    /// remembered to update it.
    /// </remarks>
    [Fact]
    public void The_configuration_path_is_the_section_plus_the_property() =>
        Assert.Equal(
            EodhdOptions.SectionName + ":" + nameof(EodhdOptions.ApiKey),
            EodhdOptions.ApiKeyPath);

    /// <summary>
    /// The environment-variable name is the path with .NET's level separator.
    /// </summary>
    /// <remarks>
    /// The single fact an operator has to get right at a PowerShell prompt. If this drifts from
    /// the path, the documented variable is a variable nothing reads, and the failure presents as
    /// a missing credential rather than a misspelt one - which sends the operator to the vendor.
    /// </remarks>
    [Fact]
    public void The_environment_variable_is_the_path_with_double_underscores() =>
        Assert.Equal(
            EodhdOptions.ApiKeyPath.Replace(":", "__", StringComparison.Ordinal),
            EodhdOptions.ApiKeyEnvironmentVariable);

    [Fact]
    public void The_environment_variable_names_no_credential() =>
        Assert.DoesNotContain(
            Synthetic,
            EodhdOptions.ApiKeyEnvironmentVariable,
            StringComparison.Ordinal);

    // ---- resolution through the real configuration pipeline ------------------------------------

    /// <summary>
    /// An environment variable reaches the bound options, and the value is never asserted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole mechanism in one test: a process environment variable named for the level
    /// separator, read by the ordinary <c>AddEnvironmentVariables</c> provider, bound by the
    /// ordinary section binder into the ordinary options class. No test-only configuration source
    /// and no test-only binding, because those would prove that a different pipeline works.
    /// </para>
    /// <para>
    /// The assertion is a fingerprint match rather than an equality check on the value. Both would
    /// pass today; only one of them still keeps the secret out of an assertion message when this
    /// is pointed at a real installation.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_environment_variable_supplies_the_credential()
    {
        var previous = Environment.GetEnvironmentVariable(EodhdOptions.ApiKeyEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(
                EodhdOptions.ApiKeyEnvironmentVariable,
                Synthetic);

            var options = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build()
                .GetSection(EodhdOptions.SectionName)
                .Get<EodhdOptions>() ?? new EodhdOptions();

            var resolved = CredentialPresence.Of(options.ApiKey);

            Assert.True(resolved.IsConfigured);
            Assert.False(resolved.IsPadded);
            Assert.Equal(CredentialPresence.Of(Synthetic).Fingerprint, resolved.Fingerprint);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                EodhdOptions.ApiKeyEnvironmentVariable,
                previous);
        }
    }

    /// <summary>
    /// An installation that has set nothing binds to an empty credential rather than to null.
    /// </summary>
    /// <remarks>
    /// Worth pinning because the connectors branch on <c>IsNullOrWhiteSpace</c>. A binder that
    /// started producing null for an absent key would still take that branch; one that started
    /// producing a placeholder would not, and the connector would send it to the vendor.
    /// </remarks>
    [Fact]
    public void An_unset_variable_leaves_the_credential_empty()
    {
        var previous = Environment.GetEnvironmentVariable(EodhdOptions.ApiKeyEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(EodhdOptions.ApiKeyEnvironmentVariable, null);

            var options = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build()
                .GetSection(EodhdOptions.SectionName)
                .Get<EodhdOptions>() ?? new EodhdOptions();

            Assert.False(CredentialPresence.Of(options.ApiKey).IsConfigured);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                EodhdOptions.ApiKeyEnvironmentVariable,
                previous);
        }
    }

    /// <summary>
    /// Configuration sources added later win, which is what makes an environment variable an
    /// override rather than a suggestion.
    /// </summary>
    /// <remarks>
    /// This is the rule the backfill fixture was getting wrong: it appended the user-secrets store
    /// after the host's own sources, so the store outranked the environment in the one composition
    /// that spends the subscription. The fixture now re-adds the environment after it. Pinning the
    /// rule here means the fixture's fix is anchored to a stated behaviour rather than to a
    /// comment.
    /// </remarks>
    [Fact]
    public void A_later_source_outranks_an_earlier_one()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [EodhdOptions.ApiKeyPath] = EodhdTestOptions.SyntheticKey,
            })
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [EodhdOptions.ApiKeyPath] = EodhdTestOptions.OtherSyntheticKey,
            })
            .Build();

        Assert.Equal(
            CredentialPresence.Of(EodhdTestOptions.OtherSyntheticKey).Fingerprint,
            CredentialPresence.Of(configuration[EodhdOptions.ApiKeyPath]).Fingerprint);
    }

    // ---- a padded credential is refused, not repaired -------------------------------------------

    [Theory]
    [InlineData(" " + Synthetic)]
    [InlineData(Synthetic + " ")]
    [InlineData(Synthetic + "\n")]
    [InlineData("\t" + Synthetic + "\r\n")]
    public void A_credential_carrying_whitespace_is_refused(string padded) =>
        Assert.Contains(
            Validate(EodhdTestOptions.Build(credential: padded)),
            problem => problem.MemberNames.Contains(nameof(EodhdOptions.ApiKey)));

    [Fact]
    public void A_clean_credential_is_not_refused_for_whitespace() =>
        Assert.DoesNotContain(
            Validate(EodhdTestOptions.Build(credential: Synthetic)),
            problem => problem.ErrorMessage == EodhdOptions.PaddedApiKeyMessage);

    /// <summary>
    /// Both refusals name the environment variable, so the message is the instruction.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(" " + Synthetic)]
    public void A_refusal_names_the_environment_variable(string? credential) =>
        Assert.Contains(
            Validate(EodhdTestOptions.Build(credential: credential)),
            problem => problem.ErrorMessage is not null &&
                problem.ErrorMessage.Contains(
                    EodhdOptions.ApiKeyEnvironmentVariable,
                    StringComparison.Ordinal));

    /// <summary>
    /// And neither of them names the credential. A validation message is printed to a console and
    /// pasted into an issue.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(" " + Synthetic)]
    public void A_refusal_names_no_credential(string? credential)
    {
        foreach (var problem in Validate(EodhdTestOptions.Build(credential: credential)))
        {
            Assert.DoesNotContain(Synthetic, problem.ErrorMessage!, StringComparison.Ordinal);
        }
    }

    // ---- the presence report ---------------------------------------------------------------------

    [Fact]
    public void An_absent_credential_reports_absent()
    {
        var presence = CredentialPresence.Of(null);

        Assert.False(presence.IsConfigured);
        Assert.False(presence.IsPadded);
        Assert.Equal(0, presence.Length);
        Assert.Equal(CredentialPresence.AbsentFingerprint, presence.Fingerprint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    public void A_blank_credential_is_not_a_credential(string blank) =>
        Assert.False(CredentialPresence.Of(blank).IsConfigured);

    [Fact]
    public void Padding_is_reported_separately_from_absence()
    {
        var presence = CredentialPresence.Of(Synthetic + "\n");

        Assert.True(presence.IsConfigured);
        Assert.True(presence.IsPadded);
    }

    /// <summary>The same value fingerprints the same, or the report cannot be compared to itself.</summary>
    [Fact]
    public void The_same_credential_fingerprints_the_same() =>
        Assert.Equal(
            CredentialPresence.Of(Synthetic).Fingerprint,
            CredentialPresence.Of(Synthetic).Fingerprint);

    /// <summary>Two different values fingerprint differently, or the report proves nothing.</summary>
    [Fact]
    public void Two_credentials_fingerprint_differently() =>
        Assert.NotEqual(
            CredentialPresence.Of(Synthetic).Fingerprint,
            CredentialPresence.Of(EodhdTestOptions.OtherSyntheticKey).Fingerprint);

    /// <summary>A value and the same value with a stray newline are different credentials.</summary>
    [Fact]
    public void Padding_changes_the_fingerprint() =>
        Assert.NotEqual(
            CredentialPresence.Of(Synthetic).Fingerprint,
            CredentialPresence.Of(Synthetic + "\n").Fingerprint);

    /// <summary>
    /// The report discloses nothing of the value, including in the line meant to be printed.
    /// </summary>
    [Fact]
    public void The_report_never_contains_the_credential()
    {
        var report = CredentialPresence.Of(Synthetic).ToString();

        Assert.DoesNotContain(Synthetic, report, StringComparison.Ordinal);

        // Nor any run of it long enough to be worth guessing from.
        Assert.DoesNotContain(Synthetic[..8], report, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fingerprint is not a plain digest of the credential.
    /// </summary>
    /// <remarks>
    /// The domain separator is what stops a published fingerprint being checkable against a digest
    /// computed anywhere else, which for a credential drawn from a small space would turn a safe
    /// diagnostic into a confirmation oracle. Pinning it here means removing the separator - an
    /// easy simplification to make while tidying - fails a test that says why it is there.
    /// </remarks>
    [Fact]
    public void The_fingerprint_is_domain_separated()
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(Synthetic));

        var plain = Convert.ToHexString(digest)[..CredentialPresence.FingerprintCharacters]
            .ToUpperInvariant();

        Assert.NotEqual(plain, CredentialPresence.Of(Synthetic).Fingerprint);
    }

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
}
