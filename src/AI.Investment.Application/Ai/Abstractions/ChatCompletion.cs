using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Application.Ai.Abstractions;

/// <summary>What a provider returned, or why it did not.</summary>
/// <remarks>
/// A provider failure is a value, not an exception. The orchestrator has to record the attempt, its
/// cost and its latency whichever way it went, and an exception thrown across the port loses all
/// three unless every call site remembers to catch it - which is exactly the kind of thing call
/// sites stop remembering.
/// </remarks>
public sealed record ChatCompletion
{
    private ChatCompletion(
        bool succeeded,
        string? json,
        string? error,
        int tokensIn,
        int tokensOut,
        decimal costUsd,
        int latencyMs)
    {
        Succeeded = succeeded;
        Json = json;
        Error = error;
        TokensIn = tokensIn;
        TokensOut = tokensOut;
        CostUsd = costUsd;
        LatencyMs = latencyMs;
    }

    public bool Succeeded { get; }

    /// <summary>The raw JSON answer. Present only when <see cref="Succeeded"/>.</summary>
    public string? Json { get; }

    /// <summary>Why the call failed. Present only when it did.</summary>
    public string? Error { get; }

    public int TokensIn { get; }

    public int TokensOut { get; }

    public decimal CostUsd { get; }

    public int LatencyMs { get; }

    public static ChatCompletion Ok(string json, int tokensIn, int tokensOut, decimal costUsd, int latencyMs)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new DomainValidationException(
                nameof(json),
                "A successful completion must carry its answer. An empty success is a failure that " +
                "will be discovered somewhere less convenient.");
        }

        return new ChatCompletion(true, json, null, tokensIn, tokensOut, costUsd, latencyMs);
    }

    public static ChatCompletion Failed(string error, int latencyMs = 0, decimal costUsd = 0m)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            throw new DomainValidationException(nameof(error), "A failed completion must say why.");
        }

        return new ChatCompletion(false, null, error.Trim(), 0, 0, costUsd, latencyMs);
    }
}
