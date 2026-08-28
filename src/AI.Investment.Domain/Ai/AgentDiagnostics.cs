using System.Globalization;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Ai;

/// <summary>
/// What a single agent run cost and how it was produced.
/// </summary>
/// <remarks>
/// <para>
/// These fields are not telemetry decoration. Model and prompt identity are what make a stored
/// analysis reproducible; attempts and latency are what make a degrading provider visible before
/// it shows up as a quality problem; cost is what the orchestrator's budget is enforced against,
/// and spending money is itself an action this platform gates rather than assumes.
/// </para>
/// <para>
/// Cost is a plain <c>decimal</c> in US dollars rather than the domain's <c>Money</c> type. Money
/// in this system means exposure in a market - it is risk-tiered, policy-gated and denominated per
/// position. A provider invoice is neither, and conflating the two would put an API bill through
/// the machinery built for trades.
/// </para>
/// </remarks>
public sealed record AgentDiagnostics
{
    private AgentDiagnostics(
        ModelRef model,
        PromptRef prompt,
        int tokensIn,
        int tokensOut,
        decimal costUsd,
        int latencyMs,
        int attempts)
    {
        Model = model;
        Prompt = prompt;
        TokensIn = tokensIn;
        TokensOut = tokensOut;
        CostUsd = costUsd;
        LatencyMs = latencyMs;
        Attempts = attempts;
    }

    public ModelRef Model { get; }

    public PromptRef Prompt { get; }

    public int TokensIn { get; }

    public int TokensOut { get; }

    /// <summary>Estimated cost of this run, in US dollars.</summary>
    public decimal CostUsd { get; }

    public int LatencyMs { get; }

    /// <summary>How many provider calls were made, including retries. Always at least one.</summary>
    public int Attempts { get; }

    public static AgentDiagnostics Create(
        ModelRef model,
        PromptRef prompt,
        int tokensIn,
        int tokensOut,
        decimal costUsd,
        int latencyMs,
        int attempts)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(prompt);

        EnsureNotNegative(tokensIn, nameof(tokensIn));
        EnsureNotNegative(tokensOut, nameof(tokensOut));
        EnsureNotNegative(latencyMs, nameof(latencyMs));

        if (costUsd < 0m)
        {
            throw new DomainValidationException(
                nameof(costUsd),
                "A run's cost may not be negative.");
        }

        if (attempts < 1)
        {
            throw new DomainValidationException(
                nameof(attempts),
                "A completed run made at least one attempt. Recording zero would hide a retry loop " +
                "that never actually called the provider.");
        }

        return new AgentDiagnostics(model, prompt, tokensIn, tokensOut, costUsd, latencyMs, attempts);
    }

    /// <summary>Diagnostics for a run that never reached a provider.</summary>
    public static AgentDiagnostics NotAttempted(PromptRef prompt) =>
        Create(ModelRef.None, prompt, 0, 0, 0m, 0, 1);

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Model} {Prompt} in={TokensIn} out={TokensOut} cost={CostUsd:0.####} {LatencyMs}ms x{Attempts}");

    private static void EnsureNotNegative(int value, string parameterName)
    {
        if (value < 0)
        {
            throw new DomainValidationException(parameterName, "A recorded measurement may not be negative.");
        }
    }
}
