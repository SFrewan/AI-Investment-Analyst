using System.Text.Json;
using AI.Investment.Application.Ai.Abstractions;
using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Ai.Groundedness;

namespace AI.Investment.Application.Ai;

/// <summary>
/// The machinery every agent shares: ask, validate, ground, or refuse.
/// </summary>
/// <typeparam name="TInput">What this agent reads.</typeparam>
/// <typeparam name="TOutput">Its structured answer.</typeparam>
/// <remarks>
/// <para>
/// A base class rather than a helper, because the sequence below is the part that must not vary
/// between agents. Schema validation, the bounded retry, the groundedness check and the refusal
/// path are the controls; an agent free to implement its own version of them is an agent free to
/// skip one, and the one it skips will be the one that mattered.
/// </para>
/// <para>
/// <strong>What is retried, and what is not.</strong> A schema failure or a provider error is
/// retried, because both are transient in the way that matters - the same question asked again may
/// parse. An ungrounded answer is not retried: the model was asked at temperature zero, so the
/// second answer is the first one, and an agent that keeps re-rolling until a fabricated figure
/// happens to land inside tolerance is precisely the failure mode the check exists to stop. A
/// refusal is not retried either, because it is a legitimate answer.
/// </para>
/// <para>
/// <strong>There is no free-text fallback.</strong> An answer that will not parse produces
/// <see cref="AgentStatus.SchemaFailed"/> and no output at all. Reading it as prose would mean
/// letting an unvalidated figure through under a heading that says it was checked.
/// </para>
/// </remarks>
public abstract class AnalysisAgent<TInput, TOutput> : IAnalysisAgent<TInput, TOutput>
    where TOutput : class, IGroundedOutput
{
    private readonly IChatModel _model;
    private readonly IPromptStore _prompts;

    protected AnalysisAgent(IChatModel model, IPromptStore prompts)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(prompts);

        _model = model;
        _prompts = prompts;
    }

    public abstract AgentId AgentId { get; }

    public abstract string Version { get; }

    public abstract PromptRef Prompt { get; }

    public virtual GroundednessPolicy GroundednessPolicy => GroundednessPolicy.Strict;

    /// <summary>The JSON schema sent to the provider for structured-output enforcement.</summary>
    protected abstract string ResponseSchema { get; }

    protected virtual int MaxOutputTokens => 1200;

    /// <summary>How many provider calls one run may make before giving up. Never unbounded.</summary>
    protected virtual int MaxAttempts => 3;

    /// <summary>How close a quoted figure must be to the claim behind it.</summary>
    protected virtual GroundednessTolerance Tolerance => GroundednessTolerance.Default;

    public async Task<AgentResult<TOutput>> AnalyseAsync(
        TInput input,
        AnalysisBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(budget);

        var bundle = EvidenceFor(input);
        var template = await _prompts.GetAsync(Prompt, cancellationToken).ConfigureAwait(false);

        var request = ChatRequest.Create(
            Prompt,
            template.Text,
            RenderInput(input, bundle),
            ResponseSchema,
            MaxOutputTokens);

        var tally = new RunTally();
        string? lastFailure = null;
        var lastStatus = AgentStatus.ProviderError;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (!budget.TryBeginCall(out var refusal))
            {
                return Failed(AgentStatus.BudgetExceeded, refusal!, tally);
            }

            var completion = await _model.CompleteAsync(request, cancellationToken).ConfigureAwait(false);

            tally.Record(completion);
            budget.RecordSpend(completion.CostUsd);

            if (!completion.Succeeded)
            {
                lastStatus = AgentStatus.ProviderError;
                lastFailure = completion.Error;
                continue;
            }

            AgentEnvelope envelope;
            TOutput output;

            try
            {
                envelope = AgentEnvelope.Parse(completion.Json!);

                if (envelope.Refused)
                {
                    return Failed(AgentStatus.Refused, envelope.RefusalReason!, tally, envelope.Limitations);
                }

                output = ReadAnalysis(envelope.Analysis, bundle);
            }
            catch (AgentSchemaException exception)
            {
                lastStatus = AgentStatus.SchemaFailed;
                lastFailure = exception.Message;
                continue;
            }

            var report = GroundednessValidator.Validate(bundle, output, Tolerance, GroundednessPolicy);

            if (!report.IsGrounded)
            {
                return Failed(AgentStatus.Ungrounded, report.Explain(), tally, envelope.Limitations);
            }

            if (report.MatchedClaims.Count == 0)
            {
                return Failed(
                    AgentStatus.Ungrounded,
                    "The answer cited no evidence at all. An analysis that rests on nothing in the " +
                    "bundle cannot be told apart from one written from the model's memory.",
                    tally,
                    envelope.Limitations);
            }

            return AgentResults.Ok(
                AgentId,
                Version,
                output,
                envelope.Confidence!,
                report.MatchedClaims,
                tally.ToDiagnostics(_model.Model, Prompt),
                envelope.Limitations);
        }

        return Failed(lastStatus, lastFailure ?? "The provider produced no usable answer.", tally);
    }

    /// <summary>The evidence this run is grounded against.</summary>
    protected abstract EvidenceBundle EvidenceFor(TInput input);

    /// <summary>The text the agent is asked to read, evidence block included.</summary>
    protected virtual string RenderInput(TInput input, EvidenceBundle bundle) =>
        EvidenceRenderer.Render(bundle);

    /// <summary>Reads the agent-specific payload, throwing <see cref="AgentSchemaException"/> if it cannot.</summary>
    protected abstract TOutput ReadAnalysis(JsonElement analysis, EvidenceBundle bundle);

    private AgentResult<TOutput> Failed(
        AgentStatus status,
        string explanation,
        RunTally tally,
        IEnumerable<string>? limitations = null) =>
        AgentResults.Failed<TOutput>(
            AgentId,
            Version,
            status,
            explanation,
            tally.ToDiagnostics(_model.Model, Prompt),
            evidence: null,
            limitations);

    /// <summary>Accumulates what the attempts cost, so a retried run reports its whole price.</summary>
    private sealed class RunTally
    {
        private int _tokensIn;
        private int _tokensOut;
        private decimal _costUsd;
        private int _latencyMs;
        private int _attempts;

        public void Record(ChatCompletion completion)
        {
            _tokensIn += completion.TokensIn;
            _tokensOut += completion.TokensOut;
            _costUsd += completion.CostUsd;
            _latencyMs += completion.LatencyMs;
            _attempts++;
        }

        public AgentDiagnostics ToDiagnostics(ModelRef model, PromptRef prompt) =>
            AgentDiagnostics.Create(
                model,
                prompt,
                _tokensIn,
                _tokensOut,
                _costUsd,
                _latencyMs,
                Math.Max(1, _attempts));
    }
}
