using AI.Investment.Application.Abstractions;
using AI.Investment.Application.Ai.Agents;
using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Auditing;

namespace AI.Investment.Application.Ai.Pipeline;

/// <summary>
/// The orchestrator: deterministic C# that decides what runs, in what order, and what is allowed
/// through.
/// </summary>
/// <remarks>
/// <para>
/// No agent decides what happens next. The sequence below is fixed code - fan out to the
/// specialists, keep only what passed validation, synthesise from that, audit everything - and the
/// only thing an agent contributes is data. That is the difference between a system with agents in
/// it and an agentic system, and it is the reason this one can be reasoned about.
/// </para>
/// <para>
/// Stages 1 and 2 of the architecture's pipeline - evidence assembly and deterministic analytics -
/// happen before this class is called and arrive as the bundle. Stages 6 to 8 - scoring,
/// opportunity assembly and the policy decision - happen after it, and are deliberately not
/// reachable from here: this class has no repository, no gateway and no way to cause anything.
/// </para>
/// </remarks>
public sealed class AnalysisPipeline
{
    private readonly IAnalysisAgent<EvidenceBundle, FinancialReading> _financial;
    private readonly IAnalysisAgent<EvidenceBundle, NewsReading> _news;
    private readonly IAnalysisAgent<EvidenceBundle, RiskAssessment> _risk;
    private readonly IAnalysisAgent<SynthesisInput, AnalysisSynthesis> _synthesis;
    private readonly IAuditSink _audit;
    private readonly IClock _clock;

    public AnalysisPipeline(
        IAnalysisAgent<EvidenceBundle, FinancialReading> financial,
        IAnalysisAgent<EvidenceBundle, NewsReading> news,
        IAnalysisAgent<EvidenceBundle, RiskAssessment> risk,
        IAnalysisAgent<SynthesisInput, AnalysisSynthesis> synthesis,
        IAuditSink audit,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(financial);
        ArgumentNullException.ThrowIfNull(news);
        ArgumentNullException.ThrowIfNull(risk);
        ArgumentNullException.ThrowIfNull(synthesis);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);

        _financial = financial;
        _news = news;
        _risk = risk;
        _synthesis = synthesis;
        _audit = audit;
        _clock = clock;
    }

    public async Task<AnalysisOutcome> RunAsync(
        AnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var bundle = request.Bundle;

        var financialTask = _financial.AnalyseAsync(bundle, request.Budget, cancellationToken);
        var newsTask = _news.AnalyseAsync(bundle, request.Budget, cancellationToken);
        var riskTask = _risk.AnalyseAsync(bundle, request.Budget, cancellationToken);

        await Task.WhenAll(financialTask, newsTask, riskTask).ConfigureAwait(false);

        var financial = await financialTask.ConfigureAwait(false);
        var news = await newsTask.ConfigureAwait(false);
        var risk = await riskTask.ConfigureAwait(false);

        var specialists = new List<AgentResult> { financial, news, risk };

        foreach (var result in specialists)
        {
            await RecordAsync(request, result, cancellationToken).ConfigureAwait(false);
        }

        var findings = Findings(financial, news, risk);

        if (findings.Count == 0)
        {
            return new AnalysisOutcome(bundle, specialists, null, request.Budget);
        }

        var synthesis = await _synthesis
            .AnalyseAsync(SynthesisInput.Create(bundle, findings), request.Budget, cancellationToken)
            .ConfigureAwait(false);

        await RecordAsync(request, synthesis, cancellationToken).ConfigureAwait(false);

        return new AnalysisOutcome(bundle, specialists, synthesis, request.Budget);
    }

    /// <summary>
    /// Reduces the successful specialist runs to what synthesis may see.
    /// </summary>
    /// <remarks>
    /// Only successes. A specialist that failed schema validation, failed the groundedness check,
    /// refused, or ran out of budget contributes nothing at all - not a summary, not a caveat, not
    /// its figures. Passing along a failed answer with a warning attached would put the warning in a
    /// prompt, where it is a suggestion, and the figure in a narrative, where it is quoted.
    /// </remarks>
    private static List<SpecialistFinding> Findings(
        AgentResult<FinancialReading> financial,
        AgentResult<NewsReading> news,
        AgentResult<RiskAssessment> risk)
    {
        var findings = new List<SpecialistFinding>();

        if (financial.Succeeded)
        {
            var reading = financial.RequireOutput();

            findings.Add(SpecialistFinding.Create(
                financial.AgentId,
                financial.Confidence!,
                reading.Summary,
                reading.Strengths.Concat(reading.Concerns),
                reading.Figures));
        }

        if (news.Succeeded)
        {
            var reading = news.RequireOutput();

            findings.Add(SpecialistFinding.Create(
                news.AgentId,
                news.Confidence!,
                reading.Summary,
                reading.Themes,
                reading.Figures));
        }

        if (risk.Succeeded)
        {
            var assessment = risk.RequireOutput();

            findings.Add(SpecialistFinding.Create(
                risk.AgentId,
                risk.Confidence!,
                assessment.Summary,
                assessment.Risks.Select(identified => identified.ToString()),
                assessment.Figures));
        }

        return findings;
    }

    private Task RecordAsync(AnalysisRequest request, AgentResult result, CancellationToken cancellationToken) =>
        _audit.RecordAsync(
            AuditRecord.ForAgentRun(
                request.CorrelationId,
                request.Bundle.Subject,
                request.Bundle.Hash,
                result,
                _clock.UtcNow),
            cancellationToken);
}
