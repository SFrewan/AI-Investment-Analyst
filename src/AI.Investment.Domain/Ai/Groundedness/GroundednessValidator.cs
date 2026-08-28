using System.Globalization;
using AI.Investment.Domain.Evidence;

namespace AI.Investment.Domain.Ai.Groundedness;

/// <summary>
/// Stage 4 of the analysis pipeline: the check that every figure an agent states came from the
/// evidence it was given.
/// </summary>
/// <remarks>
/// <para>
/// This is the mechanical implementation of "never fabricate financial data". It is deterministic,
/// it runs after every agent and before anything is scored, and it has no model in it. An output
/// that fails is not corrected, softened or partially accepted - it is excluded, because a
/// half-trusted analysis is one that will be read as a whole one.
/// </para>
/// <para>
/// Dates are admissible without being claims. A sentence that mentions the period a filing covers
/// is quoting the bundle's own provenance, not inventing a figure, so the year, month and day of
/// every claim's three timestamps are accepted alongside the claim values. Nothing else is.
/// </para>
/// </remarks>
public static class GroundednessValidator
{
    public static GroundednessReport Validate(
        EvidenceBundle bundle,
        IGroundedOutput output,
        GroundednessTolerance? tolerance = null,
        GroundednessPolicy policy = GroundednessPolicy.Strict)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(output);

        var applied = tolerance ?? GroundednessTolerance.Default;
        var numericClaims = bundle.NumericClaims();

        var figures = new List<FigureFinding>();
        var matched = new List<ClaimId>();

        foreach (var figure in output.AssertedFigures())
        {
            var finding = Check(bundle, numericClaims, figure, applied);

            figures.Add(finding);

            if (finding.MatchedClaimId is { } claimId && !matched.Contains(claimId))
            {
                matched.Add(claimId);
            }
        }

        List<NumericMention> ungroundedMentions = policy == GroundednessPolicy.Structural
            ? []
            : ScanNarrative(bundle, numericClaims, output, applied);

        return new GroundednessReport(policy, figures, ungroundedMentions, matched);
    }

    /// <summary>
    /// Checks one asserted figure. A cited claim is checked against that claim alone; an uncited
    /// figure may match any numeric claim, which is a weaker guarantee and is reported as such by
    /// the fact that the prompts require citations.
    /// </summary>
    private static FigureFinding Check(
        EvidenceBundle bundle,
        List<Claim> numericClaims,
        AssertedFigure figure,
        GroundednessTolerance tolerance)
    {
        var candidates = figure.Candidates();

        if (figure.CitedClaimId is null && figure.CitedLabel is not null)
        {
            return FigureFinding.Ungrounded(
                figure,
                $"cites '{figure.CitedLabel}', which is not a label in the evidence bundle");
        }

        if (figure.CitedClaimId is { } citedId)
        {
            if (!bundle.Contains(citedId))
            {
                return FigureFinding.Ungrounded(
                    figure,
                    $"cites {citedId}, which is not in the evidence bundle");
            }

            var cited = numericClaims.Find(claim => claim.Id.Equals(citedId));

            if (cited is null)
            {
                return FigureFinding.Ungrounded(
                    figure,
                    $"cites {citedId}, which carries no numeric value");
            }

            if (!EvidenceBundle.TryReadNumber(cited, out var citedValue))
            {
                return FigureFinding.Ungrounded(figure, $"cites {citedId}, whose value is not a number");
            }

            return candidates.Exists(candidate => tolerance.Matches(candidate, citedValue))
                ? FigureFinding.Grounded(figure, citedId)
                : FigureFinding.Ungrounded(
                    figure,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"stated {figure.Value} but the cited claim holds {citedValue}"));
        }

        foreach (var claim in numericClaims)
        {
            if (!EvidenceBundle.TryReadNumber(claim, out var claimed))
            {
                continue;
            }

            if (candidates.Exists(candidate => tolerance.Matches(candidate, claimed)))
            {
                return FigureFinding.Grounded(figure, claim.Id);
            }
        }

        return FigureFinding.Ungrounded(
            figure,
            string.Create(
                CultureInfo.InvariantCulture,
                $"no claim in the bundle holds {figure.Value} within {tolerance}"));
    }

    private static List<NumericMention> ScanNarrative(
        EvidenceBundle bundle,
        List<Claim> numericClaims,
        IGroundedOutput output,
        GroundednessTolerance tolerance)
    {
        var admissible = AdmissibleValues(bundle, numericClaims);
        var ungrounded = new List<NumericMention>();

        foreach (var fragment in output.NarrativeFragments())
        {
            foreach (var mention in NumericTextScanner.Scan(fragment))
            {
                if (!IsAdmissible(mention, admissible, tolerance))
                {
                    ungrounded.Add(mention);
                }
            }
        }

        return ungrounded;
    }

    private static bool IsAdmissible(
        NumericMention mention,
        List<decimal> admissible,
        GroundednessTolerance tolerance)
    {
        foreach (var candidate in mention.Candidates)
        {
            foreach (var value in admissible)
            {
                if (tolerance.Matches(candidate, value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Every number a sentence may legitimately contain: the claim values themselves, and the
    /// calendar components of the dates those claims carry.
    /// </summary>
    private static List<decimal> AdmissibleValues(EvidenceBundle bundle, List<Claim> numericClaims)
    {
        var values = new List<decimal>();

        foreach (var claim in numericClaims)
        {
            if (EvidenceBundle.TryReadNumber(claim, out var value))
            {
                values.Add(value);
            }
        }

        foreach (var claim in bundle.Claims)
        {
            AddDateComponents(values, claim.Provenance.AsOfUtc);
            AddDateComponents(values, claim.Provenance.PublishedAtUtc);
        }

        AddDateComponents(values, bundle.Cutoff.AsOfUtc);

        return values;
    }

    private static void AddDateComponents(List<decimal> values, DateTime moment)
    {
        values.Add(moment.Year);
        values.Add(moment.Month);
        values.Add(moment.Day);
    }
}
