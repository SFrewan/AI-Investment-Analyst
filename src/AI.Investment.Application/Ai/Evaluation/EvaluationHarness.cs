using System.Globalization;
using System.Text;
using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Ai.Groundedness;

namespace AI.Investment.Application.Ai.Evaluation;

/// <summary>
/// Runs an agent against a fixed set of scenarios and measures whether it behaves.
/// </summary>
/// <remarks>
/// <para>
/// The exit criterion of the AI phase in executable form. Without it, "the agents work" rests on
/// somebody having read some output and been satisfied, which is not a claim that survives a prompt
/// edit, a model upgrade, or six months.
/// </para>
/// <para>
/// Stability is measured by running each case more than once and comparing fingerprints. At
/// temperature zero against a deterministic provider this is a tautology, and that is fine: the
/// measurement is here so that the day a sampling provider is introduced, the number moves and
/// somebody has to decide what to do about it. A metric that only starts existing once it starts
/// failing is a metric nobody trusts.
/// </para>
/// </remarks>
public static class EvaluationHarness
{
    public const int MinimumRepeats = 2;

    public static async Task<EvaluationReport> RunAsync<TOutput>(
        IAnalysisAgent<EvidenceBundle, TOutput> agent,
        IEnumerable<EvaluationCase> cases,
        Func<AnalysisBudget> budgetFactory,
        int repeats = MinimumRepeats,
        CancellationToken cancellationToken = default)
        where TOutput : class, IGroundedOutput
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(budgetFactory);

        if (repeats < MinimumRepeats)
        {
            throw new ArgumentOutOfRangeException(
                nameof(repeats),
                repeats,
                $"Stability cannot be measured from fewer than {MinimumRepeats} runs of a case.");
        }

        var outcomes = new List<CaseOutcome>();
        var totalRuns = 0;
        var providerFailures = 0;
        var schemaFailures = 0;
        var ungroundedRuns = 0;

        foreach (var evaluationCase in cases)
        {
            var statuses = new List<AgentStatus>();
            var fingerprints = new List<string>();

            for (var repeat = 0; repeat < repeats; repeat++)
            {
                var result = await agent
                    .AnalyseAsync(evaluationCase.Bundle, budgetFactory(), cancellationToken)
                    .ConfigureAwait(false);

                totalRuns++;
                statuses.Add(result.Status);
                fingerprints.Add(Fingerprint(result));

                switch (result.Status)
                {
                    case AgentStatus.ProviderError:
                    case AgentStatus.BudgetExceeded:
                        providerFailures++;
                        break;

                    case AgentStatus.SchemaFailed:
                        schemaFailures++;
                        break;

                    case AgentStatus.Ungrounded:
                        ungroundedRuns++;
                        break;

                    default:
                        break;
                }
            }

            var stable = fingerprints.TrueForAll(
                fingerprint => string.Equals(fingerprint, fingerprints[0], StringComparison.Ordinal));

            var metExpectation = MeetsExpectation(evaluationCase.Expectation, statuses);

            outcomes.Add(new CaseOutcome(
                evaluationCase,
                statuses,
                stable,
                metExpectation,
                Note(stable, metExpectation, evaluationCase, statuses)));
        }

        return new EvaluationReport(
            agent.AgentId.Value,
            repeats,
            totalRuns,
            providerFailures,
            schemaFailures,
            ungroundedRuns,
            outcomes);
    }

    /// <summary>
    /// A stable description of what a run produced, for comparing one repeat against another.
    /// </summary>
    /// <remarks>
    /// Deliberately excludes diagnostics. Tokens, latency and cost vary between identical answers,
    /// and including them would report every run as unstable, which would make the number useless in
    /// exactly the situation it exists for.
    /// </remarks>
    public static string Fingerprint(AgentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var builder = new StringBuilder();

        builder.Append(result.Status).Append('|');
        builder.Append(result.Confidence?.Value.ToString("0.####", CultureInfo.InvariantCulture) ?? "-").Append('|');
        builder.Append(result.Explanation ?? "-").Append('|');
        builder.AppendJoin(',', result.Evidence.Select(id => id.ToString())).Append('|');

        if (result.UntypedOutput is IGroundedOutput output)
        {
            foreach (var figure in output.AssertedFigures())
            {
                builder.Append(figure).Append(';');
            }

            builder.Append('|');
            builder.AppendJoin(' ', output.NarrativeFragments());
        }

        return builder.ToString();
    }

    private static bool MeetsExpectation(EvaluationExpectation expectation, List<AgentStatus> statuses) =>
        expectation switch
        {
            EvaluationExpectation.Grounded => statuses.TrueForAll(status => status == AgentStatus.Ok),
            EvaluationExpectation.Rejected => statuses.TrueForAll(status => status != AgentStatus.Ok),
            _ => false,
        };

    private static string? Note(
        bool stable,
        bool metExpectation,
        EvaluationCase evaluationCase,
        List<AgentStatus> statuses)
    {
        if (stable && metExpectation)
        {
            return null;
        }

        var observed = string.Join(", ", statuses);

        return metExpectation
            ? $"repeats disagreed: {observed}"
            : $"expected {evaluationCase.Expectation} but observed {observed}";
    }
}
