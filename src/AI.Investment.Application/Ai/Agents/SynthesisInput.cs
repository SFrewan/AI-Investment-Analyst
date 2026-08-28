using AI.Investment.Domain.Ai;
using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Application.Ai.Agents;

/// <summary>What the synthesis agent reads: the evidence, and the validated specialist findings.</summary>
/// <remarks>
/// The bundle travels with the findings because synthesis is checked for groundedness against the
/// same evidence as everything else. Without it, the one agent whose job is to write the summary a
/// human will actually read would be the one agent free to invent a number.
/// </remarks>
public sealed record SynthesisInput
{
    private readonly List<SpecialistFinding> _findings;

    private SynthesisInput(EvidenceBundle bundle, List<SpecialistFinding> findings)
    {
        Bundle = bundle;
        _findings = findings;
    }

    public EvidenceBundle Bundle { get; }

    public IReadOnlyList<SpecialistFinding> Findings => _findings;

    public static SynthesisInput Create(EvidenceBundle bundle, IEnumerable<SpecialistFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(findings);

        var materialised = findings.ToList();

        if (materialised.Count == 0)
        {
            throw new DomainRuleViolationException(
                "SynthesisInput.NothingToSynthesise",
                "Synthesis requires at least one validated specialist finding. Summarising an empty " +
                "set would produce a narrative with nothing behind it, which is the most convincing " +
                "kind of fabrication this system can emit.");
        }

        return new SynthesisInput(bundle, materialised);
    }
}
