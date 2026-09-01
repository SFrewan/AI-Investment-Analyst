# Block 3 — read-only rehearsal result and recommendation

**Recommendation: NO-GO on Block 3 as scoped.** Not because the machinery cannot be built, but
because the evidence engine at its centre cannot produce evidence that clears the promotion bar —
and building the rest would produce a validated-looking number that means nothing.

Raw output: `artifacts/verify/rehearsal.md`. Ran in 12 seconds. Created nothing, wrote nothing,
fetched nothing; asserted unchanged opportunity and observation counts at the end.

## What was run

The production path, not a re-implementation: `PriceSeriesReader.ReadAdjustedAsync` at each pinned
instant, then `PriceRecoveryRule.Evaluate` over what it returned, with the parameters read from the
same `DiscoverySettings` the cycle uses — drawdown ≥ 10%, 60-session warm-up, 21-session horizon,
minimum 5 past occurrences, 120-session window.

Decision points run from the 60th session to 21 before the last, so every decision can be both made
and judged inside the stored year. Each is read as at the moment that session's close became public.

**170 decision points per instrument × 20 = 3,400.** All 3,400 were eligible.

## The headline, and why it is not the good news it looks like

| | |
| --- | ---: |
| Decision points | 3,400 |
| Opportunities that would have fired | 1,108 |
| Firing rate | **32.59%** |
| **Distinct drawdown episodes** | **77** |
| Firings per episode | **14.4** |

I estimated 3%. The actual is 32.6% — wrong by a factor of ten, which is why the rehearsal was worth
running before anything was built on the estimate.

**But 1,108 is not 1,108 predictions.** A drawdown persists for many consecutive sessions, so the
same episode fires again every day it lasts. MSFT produced 140 firings from **2** episodes. PEP
produced 56 from **1**. WMT, 22 from **1**.

**The effective sample is 77 independent episodes — below the 100 the promotion gate requires.**

## Against the promotion bar

| Criterion | Required | Observed | |
| --- | ---: | ---: | --- |
| Scored predictions | 100 | 1,108 firings / **77 episodes** | **not met on any honest reading** |
| Hit rate — return beat zero | 0.60 | 0.5560 | **NOT met** |
| Hit rate — reached the stated target | 0.60 | 0.0794 | **NOT met** |
| Brier — probability vs "return beat zero" | ≤ 0.20 | 0.5538 | **NOT met** |
| Brier — probability vs its own event | ≤ 0.20 | 0.1126 | met |
| Calibration bins with ≥ 10 samples | 10 of 10 | **2 of 10** | **NOT met** |

## Four findings, in order of how much they matter

### 1. The stated probability and the validation event are different events

`SuccessProbability` is the probability that the close **returns to its prior peak within the
horizon**. The validation run's event, at the configured `EventThresholdRatio: 0.00`, is that the
**return beat zero**. These are not the same question.

The two Brier rows measure exactly that gap: **0.5538** against the validation's event, **0.1126**
against the rule's own. Left as it is, a real validation run would compute the first number and
present it as the model's calibration.

**This must be fixed before any measurement means anything**, and it is cheap: either the validation
event becomes "reached the target", or the rule states a probability of the event validation
measures.

### 2. The rule is well calibrated and has no discriminating power

Against its own event the rule scores 0.1126 — inside the 0.20 ceiling. It earns that by saying
"approximately zero" and being right: the target was reached **7.94%** of the time.

A screen that correctly predicts its own event will almost never happen is calibrated and useless.
Worse, it is a *negative* signal being used to open *positive* positions: the discoverer drafts an
opportunity on a probability that says recovery is very unlikely.

### 3. Calibration can never be established, structurally

**1,051 of 1,108 firings — 95% — land in the lowest bin.** Only 2 of 10 bins reach the 10 samples
`CalibrationCurve` requires.

This is not a sample-size problem that more data fixes. The base rate is near zero almost every
time, so the probability distribution has no spread to bin. More instruments and more years would
produce more points in the same one bin.

### 4. No demonstrated edge over simply holding

A 55.6% hit rate on "did it rise over 21 sessions", with a mean realised return of 2.63% per firing,
is close to the unconditional base rate for large-cap equities in a rising market — and the months
table shows most months rising. The benchmark is not measured here (that is blocker B1), but the
excess-return criterion exists to catch precisely this, and nothing in these numbers suggests it
would be cleared.

## Coverage and data quality

**No blockers, and one constraint that did not bite.**

| Refusal | Count | Share |
| --- | ---: | ---: |
| Candidate produced | 1,108 | 32.59% |
| `NoDrawdown` | 1,880 | 55.29% |
| `NotEnoughOccurrences` | 412 | 12.12% |
| `NotEnoughHistory` | 0 | 0% |
| `MalformedSeries` | 0 | 0% |
| Series refused as unscreenable | **0** | 0% |

**Zero unexplained discontinuities across all 3,400 decision points.** Since this platform holds no
split observations at all, any instrument that had split inside the window would have surfaced here
as a refusal. None did — so constraint C2 (no corporate-actions entitlement) cost nothing on this
particular year and this particular universe. That is a measured result, not an assumption, and it
would need re-measuring on any new window.

**Instruments that never fired (3):** `JNJ.US`, `KO.US`, `MRK.US` — defensive names that never fell
10% below their 120-session peak. Expected, and not a defect. No instrument fired between one and
four times; the distribution is either zero or many, which is itself the episode-persistence effect.

## Recommendation

**NO-GO on Block 3 as scoped.** Proceeding would build ranking, prediction generation, paper trading
and a validation run on top of a screen that cannot produce admissible evidence, and the first
honest validation report would say `NotEstablished` after all of it was built.

**GO on this instead — three pieces of corrective work, all cheap, all measurable against the same
stored year:**

1. **Make the stated probability and the measured event the same event.** Until they are, every
   calibration and Brier number the platform produces is measuring a mismatch. This is a small,
   well-bounded change and it is a prerequisite to everything else.

2. **One opportunity per drawdown episode, not one per session.** Suppress re-drafting while an
   episode is open. This makes the sample honest, cuts 1,108 to 77, and incidentally removes the
   `MaxActionsPerCapabilityPerDay` pressure the plan worried about.

3. **Then re-measure, and decide about the rule.** At 77 episodes per year from 20 names, 100
   independent predictions needs either more instruments or more time — and the universe is frozen
   by decision. A screen firing on a third of all sessions is not selective, so the rule's
   parameters deserve a deliberate revisit. **But note the trap:** tuning them against this same
   year is fitting to the only data there is, which is exactly what the platform's point-in-time
   discipline exists to prevent. Any change to the rule should be declared before it is measured,
   the way a benchmark must be.

The rehearsal itself is repeatable at any time, costs nothing, and now serves as the measurement
that tells you whether corrective work has actually moved the numbers.

**No Block 3 production code has been written.**
