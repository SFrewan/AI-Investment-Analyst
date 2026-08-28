using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Application.Ai.Evaluation;

/// <summary>One named scenario the harness runs an agent against.</summary>
public sealed record EvaluationCase
{
    public const int MaxNameLength = 120;

    private EvaluationCase(string name, EvidenceBundle bundle, EvaluationExpectation expectation)
    {
        Name = name;
        Bundle = bundle;
        Expectation = expectation;
    }

    public string Name { get; }

    public EvidenceBundle Bundle { get; }

    public EvaluationExpectation Expectation { get; }

    public static EvaluationCase Create(
        string name,
        EvidenceBundle bundle,
        EvaluationExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainValidationException(
                nameof(name),
                "An evaluation case must be named. An unnamed failure in a report tells nobody which " +
                "scenario broke.");
        }

        if (expectation == EvaluationExpectation.Unknown)
        {
            throw new DomainValidationException(
                nameof(expectation),
                "An evaluation case must state what it expects. A case with no expectation passes " +
                "whatever happens, which is worse than not running it.");
        }

        var trimmed = name.Trim();

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainValidationException(
                nameof(name),
                $"An evaluation case name may not exceed {MaxNameLength} characters.");
        }

        return new EvaluationCase(trimmed, bundle, expectation);
    }

    public override string ToString() => $"{Name} (expects {Expectation})";
}
