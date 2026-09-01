using System.Globalization;
using AI.Investment.Domain.Ingestion;
using AI.Investment.Domain.Opportunities;
using AI.Investment.Domain.Opportunities.Equity;
using AI.Investment.Domain.Sources;

namespace AI.Investment.Application.Opportunities;

/// <summary>
/// Screens one instrument's stored closing prices and drafts an equity candidate when the shipped
/// rule holds.
/// </summary>
/// <remarks>
/// <para>
/// The first registered <see cref="IOpportunityDiscoverer"/>. Until now the platform had a full
/// opportunity lifecycle, a policy seam, a validation run and a promotion gate, and nothing that
/// ever produced an opportunity - so the validation report measured an empty repository and the
/// promotion gate refused for want of evidence rather than for want of performance. This is the
/// piece that lets the observation window collect something.
/// </para>
/// <para>
/// <strong>Every number in the draft is measured from the cited observations.</strong> The entry
/// price is the latest close, the target is the highest close in the window - a price this
/// instrument traded at - the success probability is the base rate of the same condition in the same
/// series, and the horizon is the calendar span the rule's sessions occupied. There is no forecast,
/// no model and no default anywhere in this class. If the series cannot support one of those
/// numbers, the rule refuses and this returns nothing.
/// </para>
/// <para>
/// <strong>It cites what it read, by the identifiers the store holds.</strong> Every close the base
/// rate was counted over is cited, and every citation is a stored observation's identifier - which
/// is what lets the validation run establish when the opportunity became knowable and refuse it if
/// it cannot. A discoverer that minted fresh claim identifiers would produce candidates that are
/// permanently inadmissible, and the symptom would be a smaller, quietly better-looking sample.
/// </para>
/// <para>
/// <strong>It produces drafts and nothing else.</strong> No evaluation, no score, no proposal, no
/// approval. A draft cannot leave that state until the type's evidence requirement is satisfied, and
/// the work plan does that under the cycle's controls rather than here.
/// </para>
/// </remarks>
public sealed class PriceRecoveryDiscoverer : IOpportunityDiscoverer
{
    private readonly PriceSeriesReader _prices;
    private readonly DiscoverySettings _settings;

    public PriceRecoveryDiscoverer(PriceSeriesReader prices, DiscoverySettings settings)
    {
        _prices = prices ?? throw new ArgumentNullException(nameof(prices));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <inheritdoc />
    public OpportunityType Type => EquityOpportunity.Type;

    /// <inheritdoc />
    public SourceId DiscovererId { get; } = SourceId.Create(PriceRecoveryRule.DiscovererId);

    /// <summary>
    /// The last verdict this instance reached, for the caller that wants to record why nothing was
    /// found.
    /// </summary>
    /// <remarks>
    /// An empty list is the interface's answer for "no candidate", and it is the same answer for a
    /// series that is too short, one that never fell, and one whose condition has never occurred
    /// often enough to count. Those are different facts about an installation - the first says the
    /// data plane is not full yet - and a cycle that recorded them identically would leave an
    /// operator guessing which. Read immediately after a call; not thread-safe, and the discoverer
    /// is registered scoped for that reason.
    /// </remarks>
    public PriceRecoveryRefusal LastRefusal { get; private set; }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Opportunity>> DiscoverAsync(
        IngestionSubject subject,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        LastRefusal = PriceRecoveryRefusal.NotEnoughHistory;

        if (!subject.IsSpecific)
        {
            // A sweep names no instrument, and an equity opportunity that cannot be ordered,
            // position-sized or reconciled against a fill is one the evidence requirement would
            // refuse anyway. Refusing here says so with a reason instead.
            return [];
        }

        // One session more than the screen reads. The extra one is not screened; it is what lets
        // the rule tell a drawdown that has just begun from one that has been open for a fortnight.
        var series = await _prices
            .ReadAsync(subject, _settings.PriceAttribute, _settings.MaxSessions + 1, nowUtc, cancellationToken)
            .ConfigureAwait(false);

        var verdict = PriceRecoveryRule.EvaluateEpisode(
            series.Select(price => price.ToClosingPrice()).ToList(),
            _settings.Rule,
            _settings.MaxSessions);

        LastRefusal = verdict.Refusal;

        if (!verdict.HasCandidate)
        {
            return [];
        }

        // The opportunity cites the window the screen actually read, not the extra session beside
        // it. Evidence that includes a price the conclusion did not rest on is evidence nobody can
        // check.
        return [Draft(subject, Screened(series), verdict.Candidate!, nowUtc)];
    }

    /// <summary>The window the screen read, without the extra session read beside it.</summary>
    private IReadOnlyList<PricedObservation> Screened(IReadOnlyList<PricedObservation> series) =>
        series.Count <= _settings.MaxSessions
            ? series
            : series.Skip(series.Count - _settings.MaxSessions).ToList();

    private Opportunity Draft(
        IngestionSubject subject,
        IReadOnlyList<PricedObservation> series,
        PriceRecoveryCandidate candidate,
        DateTime nowUtc)
    {
        var instrument = subject.Identifier!;

        var detail = OpportunityDetail.Create(
            EquityOpportunity.Type,
            EquityDetail.ToJson(
                instrument,

                // One unit. Position sizing is a decision about capital, made by the limit engine
                // and by whoever approves the action - not by the screen that noticed the price.
                quantity: 1m,
                candidate.EntryPrice,
                candidate.TargetPrice,
                _settings.CurrencyCode,
                candidate.SuccessProbability,
                candidate.HorizonDays));

        return Opportunity.Draft(
            EquityOpportunity.Type,
            subject,
            OpportunitySource.Create(DiscovererId, nowUtc),
            Title(instrument, candidate),
            Description(instrument, candidate, series.Count),
            detail,
            nowUtc,
            series.Select(price => price.Citation));
    }

    private static string Title(string instrument, PriceRecoveryCandidate candidate) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{instrument} closed {candidate.Drawdown:P1} below its highest close in the window");

    /// <summary>
    /// What the candidate is, said in the terms it was measured in.
    /// </summary>
    /// <remarks>
    /// It names the counts as well as the rate. "Recovered 4 times out of 6" and "recovered 40 times
    /// out of 60" are the same two thirds and are not the same claim, and a description that printed
    /// only the rate would let the reader supply the difference themselves.
    /// </remarks>
    private string Description(string instrument, PriceRecoveryCandidate candidate, int sessions) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{instrument} last closed at {candidate.EntryPrice}, which is {candidate.Drawdown:P1} " +
            $"below the highest close in the {sessions} sessions read ({candidate.TargetPrice}). ") +
        string.Create(
            CultureInfo.InvariantCulture,
            $"In this same series that condition has occurred {candidate.Trials} times with a full " +
            $"horizon after it, and the price returned to its own prior high within " +
            $"{candidate.HorizonDays} days on {candidate.Successes} of them - a base rate of " +
            $"{candidate.SuccessProbability:P1}. ") +
        string.Create(
            CultureInfo.InvariantCulture,
            $"The target is the prior high itself, which is a price this instrument traded at rather " +
            $"than a forecast, and the horizon is the calendar span the rule's " +
            $"{_settings.Rule.HorizonSessions} sessions occupied at the end of the window. Every " +
            $"close read is cited as evidence.");
}
