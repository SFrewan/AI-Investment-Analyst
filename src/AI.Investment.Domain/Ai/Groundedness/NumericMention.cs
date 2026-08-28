using System.Globalization;

namespace AI.Investment.Domain.Ai.Groundedness;

/// <summary>
/// A number found in an agent's prose, with every value it could reasonably mean.
/// </summary>
/// <remarks>
/// One literal can legitimately denote several values. <c>18.4%</c> may be checked against a stored
/// ratio of <c>0.184</c> or against <c>18.4</c> itself; <c>1.2 billion</c> against
/// <c>1200000000</c>. Carrying the alternatives rather than picking one keeps the validator from
/// rejecting a correct figure because it guessed the wrong reading - which would train everyone to
/// widen the tolerance, and a widened tolerance is how this check stops working.
/// </remarks>
public sealed record NumericMention
{
    private readonly List<decimal> _candidates;

    internal NumericMention(string text, List<decimal> candidates)
    {
        Text = text;
        _candidates = candidates;
    }

    /// <summary>The literal exactly as it appeared, including any suffix.</summary>
    public string Text { get; }

    /// <summary>Every value the literal could denote.</summary>
    public IReadOnlyList<decimal> Candidates => _candidates;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"'{Text}' ({string.Join(" | ", _candidates)})");
}
