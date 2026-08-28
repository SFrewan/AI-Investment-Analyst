<!--
    GENERATED, NOT WRITTEN.

    Produced by ValidationPersistenceTests running the real ValidationService against a real
    PostgreSQL, and committed from that run's output. Nothing below was composed by hand, and
    nothing below was edited after the run. Regenerate it with the integration suite, or read the
    live version at GET /api/validation/report.md.

    Read on 2026-08-28. Its finding is "not established": the repository holds no opportunities, no
    price history and no shadow measurements, so no prediction survived the point-in-time guard.
    See docs/Phases/PHASE-7-VALIDATION.md §8 and §9.
-->

# Validation report

**Run:** `96e1bc91e04549c48bf61b069612218b`  
**Generated:** 2026-08-28T15:09:05.8931931Z  
**Verdict:** **not established** — there is not enough evidence to say

This report measures the platform. It does not tune it: no threshold, model or ranking was adjusted from anything below, and the benchmark was fixed before the run began.

Autonomy is unchanged by this phase and remains **L3**.

## 1. Methodology

| Item | Value |
|---|---|
| Evaluation period | 2026-01-01T00:00:00.0000000Z to 2026-07-01T00:00:00.0000000Z |
| Horizon | 30.00:00:00 |
| Step | 1.00:00:00 |
| Event threshold | a realised move at or above 0% |
| Method version | v1.0 |
| Benchmark | index buy-and-hold — BuyAndHold of Security:SPY |
| Benchmark declared | 2026-08-28T00:00:00.0000000Z |
| Benchmark fingerprint | `87272b45ebbe6d2e657cccb8b581f875683fa8b064d28a5d49831d27180bba04` |
| Trading cost | 0.1% per leg, charged to both sides |

### Point-in-time rule

A value is admissible at a decision only if it became **public** at or before that decision, judged on `Provenance.PublishedAtUtc`. Retrieval time is never used to admit anything: it records when this installation happened to fetch a value, so admitting on it would make a historical result change when a source is backfilled. A value whose publication time cannot be established is excluded rather than assumed sound, and a derived value is admissible only if every input behind it was.

### Data sources

None. No observation from any registered source falls in this window.

## 2. Sample

| | Count |
|---|---:|
| Predictions considered | 0 |
| Admitted by the point-in-time guard | 0 |
| Refused by the point-in-time guard | 0 |
| Scored | 0 |
| Unresolved (horizon not elapsed) | 0 |
| Unavailable (no outcome data) | 0 |
| Abstained (no call made) | 0 |

## 3. Hit rate, false positives and false negatives

**Hit rate here means precision**: of the calls the system made to act, the share that turned out right. It is not accuracy, which a system that abstains from everything can drive arbitrarily high.

| Cell | Count |
|---|---:|
| True positives | 0 |
| False positives | 0 |
| True negatives | 0 |
| False negatives | 0 |

| Metric | Result |
|---|---|
| Hit rate (precision) | _hit rate (precision): there were no observations in its denominator, so the question it answers was never put to the system._ |
| False positive rate | _false positive rate: there were no observations in its denominator, so the question it answers was never put to the system._ |
| False negative rate | _false negative rate: there were no observations in its denominator, so the question it answers was never put to the system._ |
| Recall | _recall: there were no observations in its denominator, so the question it answers was never put to the system._ |
| Accuracy | _accuracy: there were no observations in its denominator, so the question it answers was never put to the system._ |

## 4. Calibration

Whether the stated probabilities mean anything. A well-calibrated system that says seventy per cent is right about seventy per cent of the time; an overconfident one is dangerous in proportion to how sure it sounds.

**Brier score:** _no resolved prediction carried a stated probability, so there is nothing to score._ 
(0 is perfect; 0.25 is what always saying fifty per cent scores, and is the number to beat.)

Resolved predictions carrying a stated probability: 0. Resolved without one, and therefore uncalibratable: 0.

| Stated band | n | Mean stated | Observed | Gap |
|---|---:|---|---|---|
| 0.0-0.1 | 0 | _mean stated probability: the band is empty._ | _observed frequency: the band is empty._ | _the band does not have enough resolved predictions to compare its claim with reality._ |
| 0.1-0.2 | 0 | _mean stated probability: the band is empty._ | _observed frequency: the band is empty._ | _the band does not have enough resolved predictions to compare its claim with reality._ |
| 0.2-0.3 | 0 | _mean stated probability: the band is empty._ | _observed frequency: the band is empty._ | _the band does not have enough resolved predictions to compare its claim with reality._ |
| 0.3-0.4 | 0 | _mean stated probability: the band is empty._ | _observed frequency: the band is empty._ | _the band does not have enough resolved predictions to compare its claim with reality._ |
| 0.4-0.5 | 0 | _mean stated probability: the band is empty._ | _observed frequency: the band is empty._ | _the band does not have enough resolved predictions to compare its claim with reality._ |
| 0.5-0.6 | 0 | _mean stated probability: the band is empty._ | _observed frequency: the band is empty._ | _the band does not have enough resolved predictions to compare its claim with reality._ |
| 0.6-0.7 | 0 | _mean stated probability: the band is empty._ | _observed frequency: the band is empty._ | _the band does not have enough resolved predictions to compare its claim with reality._ |
| 0.7-0.8 | 0 | _mean stated probability: the band is empty._ | _observed frequency: the band is empty._ | _the band does not have enough resolved predictions to compare its claim with reality._ |
| 0.8-0.9 | 0 | _mean stated probability: the band is empty._ | _observed frequency: the band is empty._ | _the band does not have enough resolved predictions to compare its claim with reality._ |
| 0.9-1.0 | 0 | _mean stated probability: the band is empty._ | _observed frequency: the band is empty._ | _the band does not have enough resolved predictions to compare its claim with reality._ |

## 5. Against the benchmark

Both sides are priced by the same function, over the same window, with the same cost model. Returns are simple and equal-weighted across round trips rather than compounded.

| | Return |
|---|---|
| System | _the strategy took no positions in the window, so it has no return to compare._ |
| Benchmark (index buy-and-hold) | _buy-and-hold needs a price at each end of the window and there are 0._ |
| **Excess** | _one side of the comparison could not be measured, so the difference between them is not a result. It is two absences subtracted from each other._ |

## 6. Shadow versus actual

Phase 6 recorded, for every gated action, what the same policy engine would have answered one autonomy level higher. Nothing here executed anything then and nothing does now.

| | Value |
|---|---|
| Measurements in window | 0 |
| Agreements | 0 |
| Divergences | 0 |
| Would have acted where the platform did not | 0 |
| Platform acted where a higher level would not | 0 |
| Agreement rate | _shadow/actual agreement rate: no shadow measurements were recorded in the window._ |
| Hit rate of the extra actions | _a higher autonomy level would not have acted on any occasion the real one declined, so there is nothing to judge._ |

Only the last row bears on whether autonomy should rise. "A higher level would have acted more often" describes the policy, not the quality of the decisions.

## 7. Data gaps and limitations

- **benchmark** — the repository holds 0 admissible price(s) for Security:SPY on 'security.close' in this window, and buy-and-hold needs one at each end. The comparison could not be made.
- **shadow versus actual** — no shadow measurements were recorded in this window, so there is nothing to compare against what the platform actually decided.
- **evidence** — no observations from any registered source fall in this window.

- One window is not a track record. A result over a single period, however measured, says nothing reliable about the next one.
- Returns are simple and equal-weighted across round trips rather than compounded or position-sized, and the same convention is applied to both the system and the benchmark.
- Trading costs are modelled as a flat rate charged on both legs, identically to both sides. Slippage, market impact, borrow costs and taxes are not modelled at all.
- Survivorship is not corrected for. If the subjects measured are the ones the repository still holds, a subject that failed and was removed would not appear here.
- There are no shadow measurements in this window, so nothing here bears on whether a higher autonomy level would be justified. Autonomy remains L3 either way.

## 8. Conclusion

No prediction survived the point-in-time guard over this window, so nothing was measured. The platform's central claim - that it produces useful analysis - remains an untested hypothesis, and this report is the record of that rather than a result.

Nothing in this report is investment advice, and nothing in it should be read as a prediction of future returns.

