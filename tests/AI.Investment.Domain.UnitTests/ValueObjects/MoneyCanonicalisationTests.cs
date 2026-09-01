using System.Globalization;
using AI.Investment.Domain.Actions;
using AI.Investment.Domain.Approvals;
using AI.Investment.Domain.Common;
using AI.Investment.Domain.Enums;
using AI.Investment.Domain.ValueObjects;
using Xunit;

namespace AI.Investment.Domain.UnitTests.ValueObjects;

/// <summary>
/// That the text of an amount is a function of the amount, and not of its scale.
/// </summary>
/// <remarks>
/// <para>
/// A .NET decimal remembers how it was written. <c>0m</c> and <c>0.0000m</c> are equal, hash
/// equal, and used to render differently - and the rendering is what <c>ActionFingerprint</c>
/// hashes. A money column declared <c>numeric(18,4)</c> returns every amount at scale four, so
/// the moment proposals are persisted, a token issued against the proposal a human was shown
/// would stop verifying against the same proposal reloaded.
/// </para>
/// <para>
/// The last test here is the one that matters. The others describe the formatting rule; that one
/// states the property the rule exists to give: <strong>two proposals that differ only in the
/// scale of their amounts have the same fingerprint.</strong>
/// </para>
/// </remarks>
public sealed class MoneyCanonicalisationTests
{
    private static readonly DateTime Now = new(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(10)]
    public void Padding_a_zero_does_not_change_its_text(int scale)
    {
        var text = scale == 0 ? "0" : "0." + new string('0', scale);
        var padded = decimal.Parse(text, CultureInfo.InvariantCulture);

        Assert.Equal("0", CanonicalNumber.Text(padded));
    }

    [Theory]
    [InlineData("1050", "1050")]
    [InlineData("1050.00", "1050")]
    [InlineData("1050.0000", "1050")]
    [InlineData("10.5", "10.5")]
    [InlineData("10.50", "10.5")]
    [InlineData("10.5001", "10.5001")]
    [InlineData("-3.20", "-3.2")]
    [InlineData("0.0001", "0.0001")]
    public void Significant_digits_survive_and_padding_does_not(string input, string expected)
    {
        var value = decimal.Parse(input, CultureInfo.InvariantCulture);

        Assert.Equal(expected, CanonicalNumber.Text(value));
    }

    /// <summary>
    /// The round trip this exists for: what a numeric(18,4) column hands back reads the same as
    /// what went in.
    /// </summary>
    [Fact]
    public void An_amount_reads_the_same_before_and_after_a_scale_four_round_trip()
    {
        var before = Money.Create(1050m, Currency.Usd);
        var afterStorage = Money.Create(decimal.Round(1050m, 4), Currency.Usd);

        // Round to four places the way the column does, then pad the way Npgsql does.
        var padded = Money.Create(1050.0000m, Currency.Usd);

        Assert.Equal(before.ToString(), afterStorage.ToString());
        Assert.Equal(before.ToString(), padded.ToString());
        Assert.Equal("1050 USD", before.ToString());
    }

    [Fact]
    public void Zero_reads_the_same_however_it_was_constructed()
    {
        Assert.Equal(Money.ZeroUsd.ToString(), Money.Create(0.0000m, Currency.Usd).ToString());
        Assert.Equal("0 USD", Money.ZeroUsd.ToString());
    }

    /// <summary>
    /// <strong>The property the whole change exists for.</strong>
    /// </summary>
    /// <remarks>
    /// Two proposals identical in every respect except the scale their amounts carry must produce
    /// one fingerprint. Before canonicalisation they produced two, which meant an approval token
    /// bound to an in-memory proposal could never verify against the stored one - and the cheap
    /// way out of that is to weaken the fingerprint, which is exactly what must not happen.
    /// </remarks>
    [Fact]
    public void A_proposal_fingerprint_does_not_move_when_an_amount_is_padded()
    {
        var unpadded = Proposal(Money.Create(1050m, Currency.Usd), Money.Create(0m, Currency.Usd));
        var padded = Proposal(Money.Create(1050.0000m, Currency.Usd), Money.Create(0.0000m, Currency.Usd));

        Assert.Equal(ActionFingerprint.Of(unpadded), ActionFingerprint.Of(padded));
    }

    /// <summary>A different amount must still produce a different fingerprint.</summary>
    /// <remarks>
    /// The guard on the test above. A canonicalisation that collapsed genuinely different values
    /// would satisfy it and destroy the fingerprint's only purpose.
    /// </remarks>
    [Fact]
    public void A_proposal_fingerprint_still_moves_when_an_amount_changes()
    {
        var one = Proposal(Money.Create(1050m, Currency.Usd), Money.ZeroUsd);
        var other = Proposal(Money.Create(1050.0001m, Currency.Usd), Money.ZeroUsd);

        Assert.NotEqual(ActionFingerprint.Of(one), ActionFingerprint.Of(other));
    }

    private static ActionProposal Proposal(Money exposure, Money cost) =>
        ActionProposal.Create(
            CorrelationId.Create("canonicalisation-test"),
            Capability.SimulatedExecution,
            ActionType.Create("execution.simulate"),
            ActionTarget.Create("Security", "AAPL.US"),
            new ScaleParameters(),
            ActionEconomics.Create(cost, exposure, ReversibilityClass.Reversible),
            ProposedBy.Service("tests"),
            "canonicalisation-test",
            Now);

    /// <summary>A payload whose text is fixed, so only the economics vary between proposals.</summary>
    private sealed record ScaleParameters : IActionParameters
    {
        public string Describe() => "scale-canonicalisation";
    }
}
