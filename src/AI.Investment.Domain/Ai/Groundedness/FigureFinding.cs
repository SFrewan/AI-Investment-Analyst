using AI.Investment.Domain.Evidence;

namespace AI.Investment.Domain.Ai.Groundedness;

/// <summary>What the validator concluded about one asserted figure.</summary>
public sealed record FigureFinding
{
    private FigureFinding(AssertedFigure figure, ClaimId? matchedClaimId, string? reason)
    {
        Figure = figure;
        MatchedClaimId = matchedClaimId;
        Reason = reason;
    }

    public AssertedFigure Figure { get; }

    /// <summary>The claim the figure was matched to. Null when it was not matched.</summary>
    public ClaimId? MatchedClaimId { get; }

    /// <summary>Why the figure failed. Null when it did not.</summary>
    public string? Reason { get; }

    public bool IsGrounded => MatchedClaimId is not null;

    public static FigureFinding Grounded(AssertedFigure figure, ClaimId matchedClaimId) =>
        new(figure, matchedClaimId, null);

    public static FigureFinding Ungrounded(AssertedFigure figure, string reason) =>
        new(figure, null, reason);

    public override string ToString() =>
        IsGrounded ? $"{Figure} -> {MatchedClaimId}" : $"{Figure} UNGROUNDED: {Reason}";
}
