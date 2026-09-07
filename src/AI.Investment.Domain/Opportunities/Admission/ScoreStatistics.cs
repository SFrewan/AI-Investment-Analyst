using AI.Investment.Domain.Exceptions;

namespace AI.Investment.Domain.Opportunities.Admission;

/// <summary>
/// The two measurements the sample bar depends on, computed once so two callers cannot disagree.
/// </summary>
/// <remarks>
/// <para>
/// Both of these were first written inside a rehearsal, which is the wrong place for them. The
/// derived sample size is proportional to the square of the component spread and inversely
/// proportional to the design effect, so two rehearsals with slightly different arithmetic would be
/// applying two different bars while reporting the same one. They live here, they are unit-tested
/// once, and every measurement uses the same code.
/// </para>
/// <para>
/// Neither is a parameter. Both are read off the scored predictions, which is the whole point:
/// a bar derived from an assumed spread or an assumed correlation is an assumed bar, and the
/// direction of the assumption would decide verdicts nobody had decided.
/// </para>
/// </remarks>
public static class ScoreStatistics
{
    /// <summary>
    /// The standard deviation of the individual Brier components.
    /// </summary>
    /// <remarks>
    /// The component of one prediction is <c>(p - o)^2</c>, the same quantity whose mean is the
    /// Brier score. Its spread is what turns "how far apart are two scores" into "how many
    /// observations tell them apart", which is the question the sample requirement asks.
    /// </remarks>
    public static decimal ComponentSpread(IEnumerable<(decimal Probability, bool Occurred)> predictions)
    {
        ArgumentNullException.ThrowIfNull(predictions);

        var components = new List<decimal>();

        foreach (var (probability, occurred) in predictions)
        {
            if (probability is < 0m or > 1m)
            {
                throw new DomainValidationException(
                    nameof(predictions),
                    $"A stated probability of {probability} is not a probability, so the spread of " +
                    "its Brier component is not a spread of anything.");
            }

            var error = probability - (occurred ? 1m : 0m);

            components.Add(error * error);
        }

        if (components.Count < 2)
        {
            return 0m;
        }

        var mean = components.Sum() / components.Count;
        var sumOfSquares = 0m;

        foreach (var component in components)
        {
            sumOfSquares += (component - mean) * (component - mean);
        }

        return (decimal)Math.Sqrt((double)(sumOfSquares / (components.Count - 1)));
    }

    /// <summary>
    /// How strongly outcomes inside one cluster move together, by the ANOVA estimator.
    /// </summary>
    /// <param name="clusters">
    /// One entry per cluster: how many predictions it holds, and how many of them came true.
    /// </param>
    /// <remarks>
    /// <para>
    /// Twenty large caps falling in the same month is one market event seen twenty times. This is
    /// the standard moment estimator for a binary outcome: compare the variation between clusters
    /// with the variation inside them, and if the first is larger the observations are not
    /// independent. Clamped into [0,1], because a negative estimate is sampling noise around zero
    /// rather than evidence that predictions repel each other, and treating it as a discount below
    /// one would inflate the sample instead of discounting it.
    /// </para>
    /// <para>
    /// Returns zero - no discount - when there is nothing to estimate from: fewer than two clusters,
    /// or no more predictions than clusters. That is the conservative direction here only because
    /// the alternative would be inventing a correlation, and every other guard in this file exists
    /// to stop exactly that.
    /// </para>
    /// </remarks>
    public static decimal IntraClusterCorrelation(IEnumerable<(int Size, int Successes)> clusters)
    {
        ArgumentNullException.ThrowIfNull(clusters);

        var material = clusters.ToList();
        var groups = material.Count;
        var total = 0;
        var successes = 0;

        foreach (var (size, won) in material)
        {
            if (size < 1)
            {
                throw new DomainValidationException(
                    nameof(clusters),
                    "A cluster holding nothing is not a cluster. Whatever grouped the predictions " +
                    "produced an entry with no members.");
            }

            if (won < 0 || won > size)
            {
                throw new DomainValidationException(
                    nameof(clusters),
                    $"A cluster of {size} cannot have {won} successes.");
            }

            total += size;
            successes += won;
        }

        if (groups < 2 || total <= groups)
        {
            return 0m;
        }

        var grand = (double)successes / total;
        var between = 0d;
        var within = 0d;
        var sumOfSquaredSizes = 0d;

        foreach (var (size, won) in material)
        {
            var rate = (double)won / size;

            between += size * (rate - grand) * (rate - grand);
            within += size * rate * (1d - rate);
            sumOfSquaredSizes += (double)size * size;
        }

        var meanSquareBetween = between / (groups - 1);
        var meanSquareWithin = within / (total - groups);
        var typicalSize = (total - (sumOfSquaredSizes / total)) / (groups - 1);
        var denominator = meanSquareBetween + ((typicalSize - 1d) * meanSquareWithin);

        if (denominator <= 0d)
        {
            return 0m;
        }

        var rho = (meanSquareBetween - meanSquareWithin) / denominator;

        return rho <= 0d ? 0m : rho >= 1d ? 1m : (decimal)rho;
    }
}
