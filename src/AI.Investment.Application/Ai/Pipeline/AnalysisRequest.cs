using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Common;

namespace AI.Investment.Application.Ai.Pipeline;

/// <summary>One request to analyse a subject, against a frozen bundle, within a stated budget.</summary>
/// <remarks>
/// The bundle arrives assembled rather than being gathered here. Evidence assembly is the data
/// plane's job and deterministic analytics is Phase 3's, and both must be reproducible without a
/// model anywhere near them - so the pipeline takes what they produced and does not reach back for
/// more. It also means a stored bundle can be replayed through the pipeline years later, which is
/// what the validation phase needs.
/// </remarks>
public sealed record AnalysisRequest
{
    private AnalysisRequest(CorrelationId correlationId, EvidenceBundle bundle, AnalysisBudget budget)
    {
        CorrelationId = correlationId;
        Bundle = bundle;
        Budget = budget;
    }

    public CorrelationId CorrelationId { get; }

    public EvidenceBundle Bundle { get; }

    public AnalysisBudget Budget { get; }

    public static AnalysisRequest Create(
        CorrelationId correlationId,
        EvidenceBundle bundle,
        AnalysisBudget budget)
    {
        ArgumentNullException.ThrowIfNull(correlationId);
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(budget);

        return new AnalysisRequest(correlationId, bundle, budget);
    }
}
