namespace AI.Investment.Application.Abstractions;

/// <summary>
/// Thrown when something attempts to persist a change without an authorised action execution
/// in progress.
/// </summary>
/// <remarks>
/// Seeing this exception means a write path bypassed the Action/Policy seam. It is a defect in
/// the calling code, not a runtime condition to be handled: the fix is to route the write
/// through <c>IActionGateway</c>, never to catch this and continue.
/// </remarks>
public sealed class UnauthorizedWriteException : InvalidOperationException
{
    public UnauthorizedWriteException(string details)
        : base(
            "A write was attempted without an authorised action execution in progress. " +
            "Every side effect must pass through IActionGateway, which requires a PolicyDecision " +
            $"permitting execution. Details: {details}")
    {
        Details = details;
    }

    public string Details { get; }
}
