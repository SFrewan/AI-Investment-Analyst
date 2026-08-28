using AI.Investment.Domain.Approvals;
using AI.Investment.Domain.Exceptions;
using Xunit;

namespace AI.Investment.Safety.Tests;

/// <summary>
/// Parsing a stored fingerprint back into one that can be compared.
/// </summary>
/// <remarks>
/// <para>
/// The parse guard is a safety control, not input validation. A fingerprint that cannot be compared
/// must not be treated as a match, so anything that is not exactly sixty-four hexadecimal characters
/// is refused before it can be compared with anything.
/// </para>
/// <para>
/// The short-but-valid-hex case is the one that matters and the one nothing was covering: a value
/// made only of hexadecimal characters passes the character check, so the length check is the only
/// thing standing between a truncated column and a token that silently authorises whatever it is
/// shown.
/// </para>
/// </remarks>
public sealed class ActionFingerprintParseTests
{
    [Fact]
    public void A_hexadecimal_value_of_the_wrong_length_is_refused_by_the_length_check()
    {
        var error = Assert.Throws<DomainValidationException>(() => ActionFingerprint.Parse("abc"));

        Assert.Contains("64 hexadecimal characters", error.Message, StringComparison.Ordinal);
        Assert.Contains("must not be treated as a match", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_of_the_right_length_that_is_not_hexadecimal_names_what_it_received()
    {
        var value = new string('z', 64);

        var error = Assert.Throws<DomainValidationException>(() => ActionFingerprint.Parse(value));

        Assert.Contains("only hexadecimal characters", error.Message, StringComparison.Ordinal);
        Assert.Contains(value, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_value_is_refused()
    {
        Assert.Throws<DomainValidationException>(() => ActionFingerprint.Parse(null!));
        Assert.Throws<DomainValidationException>(() => ActionFingerprint.Parse("   "));
    }
}
