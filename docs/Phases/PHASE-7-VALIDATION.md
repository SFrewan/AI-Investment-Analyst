# Phase 7 — Validation

**Status:** Implemented and verified by execution. The measured performance report exists, was
generated from the repository against a real database, and has been read. Its finding is that
nothing has been measured yet, which is the honest answer and is recorded as such.

**Canonical scope:** §P of `PHASE0_AUTONOMOUS_PLATFORM_ARCHITECTURE.md` — backtesting with a
point-in-time guard, hit rate, calibration curves, false positives and negatives, comparison against
a naive benchmark, and shadow-versus-actual comparison. Autonomy stays at **L3**; nothing in this
phase raises it, and an architecture test says so.

---

## 1. What this phase is for

The platform's central claim — that it produces useful analysis — has never been tested. §L.10 of the
architecture calls it an experiment whose hypothesis "must be *measured* before any capital is
committed", and this phase is the measuring apparatus. It is not the measurement: a measurement needs
data, and the repository has none yet.

That distinction runs through everything below. The apparatus is complete and exercised; the result
it produces today is "not established", and the machinery is built so that this is as easy to say as
a number would have been.

## 2. The one rule everything rests on

**A value is admissible at a past decision only if it became public at or before that decision.**

`Provenance` has carried three timestamps since Phase 2 — `AsOfUtc` (the period a value describes),
`PublishedAtUtc` (when it became public), `RetrievedAtUtc` (when this installation fetched it) — and
`KnowledgeCutoff` has admitted on the middle one since Phase 3. Phase 7 is where that becomes a
guard rather than a convention.

`PointInTimeGuard` judges one piece of evidence against one decision, and returns one of three
answers rather than two:

| Answer | Meaning |
|---|---|
| Admissible | public before the decision, and about a period that had ended |
| Refused | judged, and not usable — with the rule that refused it named |
| **Undeterminable** | the record cannot support a judgement either way |

The third is the one that matters. A refusal is a fact about the data; an undeterminable verdict is a
fact about the *record*, and a run that meets one has discovered that its own history is not good
enough to measure from. Collapsing the two would let a backtest proceed quietly over evidence nobody
can vouch for — which is how look-ahead bias enters a system that has a point-in-time guard: not
through the guard, but around it, on the rows the guard could not judge.

### What the guard refuses

- **Published after the cutoff.** Nobody knew it yet.
- **A fact describing a period that had not ended.** A "fact" about a quarter still in progress is a
  forecast wearing a fact's clothes.
- **A derived value whose inputs were not yet public.** A calculation launders its inputs;
  `MetricResult.EvidenceAvailableAtUtc` — the latest publication time among them — is when the result
  became knowable, not the moment the arithmetic ran.
- **A value computed under a later cutoff.** A number produced with a wider view of the world is not
  the number the decision had.
- **A value fetched before it was published** (undeterminable). One of the two timestamps is wrong,
  and neither can then place the value in time.
- **Missing provenance** (undeterminable). A run may not guess.

### Retrieval time

Retrieval time is a fact about this installation's fetch history and nothing else. Admitting on it
would make a historical result change when a source is backfilled — the same period, the same world,
a different answer — so no admission test in this phase reads it. The guard reads it in exactly one
place, to detect the impossible ordering above, and that reading can only ever make a verdict
stricter.

This is asserted three ways, because it is the rule most likely to be broken by somebody in a hurry:

1. A domain test sweeps retrieval time across two years while holding publication fixed and insists
   the verdict never moves.
2. An architecture test walks the IL of every member in the three validation namespaces and fails if
   any of them calls the `RetrievedAtUtc` getter. The guard is the one declared exception.
3. A second architecture test asserts the guard *does* still make that ordering check, so removing it
   fails rather than passing quietly.

## 3. What was built

### Domain — `AI.Investment.Domain/Validation`

| Type | What it is |
|---|---|
| `PointInTimeGuard`, `Admissibility` | The rule above. Pure, total, three-valued. |
| `EvaluationWindow` | Period, horizon and step, declared before the run and walked deterministically. |
| `PredictionRecord` | One prediction under test. Its constructor refuses evidence younger than itself. |
| `RealisedOutcome`, `OutcomeLabeller` | What happened, and the four cells plus the three non-judgements. |
| `ConfusionMatrix` | Hit rate, precision, recall, accuracy, false-positive and false-negative rates. |
| `CalibrationCurve` | Ten fixed bands, per-band availability, and a Brier score. |
| `BenchmarkDefinition` | The naive comparison, fingerprinted and dated. |
| `PerformanceCalculator` | The return arithmetic, shared by both sides of the comparison. |
| `ShadowComparisonResult` | Phase 6's measurements, counted. |
| `Measurement`, `MetricAvailability` | A number, or an honest statement that there is not one. |
| `ValidationReport` | The deliverable, with a verdict that includes "not established". |

### Application — `AI.Investment.Application/Validation`

`BacktestEngine` is a static pure function, like `LimitEngine`, `AdmissionControl` and
`AutonomyResolver` before it: it takes the window and the candidates and returns what was admitted,
what was refused, and why. `ValidationService` orchestrates — read history, judge, label, count,
compare, report — and `ValidationReportWriter` renders the result as Markdown.

New ports: `IValidationHistory` (point-in-time reads), `IPredictionCatalogue` (the predictions under
test), `IValidationRequestFactory` (the declared window and benchmark). `IShadowDecisionStore` gained
`GetBetweenAsync`, deliberately unpaged — a comparison over the most recent *N* measurements is a
comparison over a sample that selected itself by recency.

### Infrastructure — `AI.Investment.Infrastructure/Validation`

`EfValidationHistory` narrows every query on `published_at_utc`, which is already indexed for it, and
resolves restatements to the version current at the cutoff. `EfPredictionCatalogue` reads
opportunities as predictions. `ConfiguredValidationRequestFactory` builds the request from
configuration under change control.

### API

`GET /api/validation/report` and `GET /api/validation/report.md`. Read-only, and taking no parameters
at all — an endpoint that let a caller supply the window, horizon, threshold or benchmark would let a
caller search for a flattering result and publish it.

## 4. Measurement, not optimisation

Nothing in this phase changes the system it measures. There is no feedback path: no threshold is
adjusted from a result, no model is refitted, no prediction is re-scored, and an architecture test
asserts that the validation namespaces depend on neither the action seam nor autonomy administration.

Four decisions can be used to manufacture a favourable result — the window, the horizon, the event
threshold and the benchmark — so all four live in configuration under change control rather than
being supplied at the moment of running. The benchmark additionally carries the date it was declared
and a SHA-256 fingerprint over its own fields; a run that began before its benchmark was declared
**fails** rather than improves, and the fingerprint travels into the report so a later reader can
check that the benchmark described is the benchmark used.

## 5. Definitions, stated so they cannot drift

- **Hit rate is precision**: of the calls the system made to act, the share that turned out right.
  It is not accuracy, which a system that abstains from everything can drive arbitrarily high. All
  five rates are exposed separately, each named for what it is.
- **The event threshold is inclusive**: a realised move exactly at the threshold counts as the event
  occurring. Asserted by a test, because a boundary convention left implicit changes silently.
- **Abstentions, unresolved predictions and predictions with no outcome are counted apart from each
  other** and apart from the four cells. A sample that loses members silently selects itself, so the
  report requires admitted plus refused to equal considered and refuses to be constructed otherwise.
- **Returns are simple, equal-weighted across round trips, and not compounded.** Costs are a flat
  rate charged on both legs, to both sides, by the same function. The commonest way a backtest
  flatters itself is not a dramatic bug but a small asymmetry between the two sides.
- **Only calls to act become positions.** Counting an abstention as a flat trade would dilute the
  return towards zero and make a bad strategy look merely unexciting.

## 6. Shadow versus actual

Phase 6 recorded, for every gated action, what the same policy engine would have answered one
autonomy level higher. This phase counts those records and matches them to the outcomes of their own
proposals. It is arithmetic: no proposal is re-evaluated, no effect is invoked, and there is no code
path from a shadow record to an execution.

The agreement rate is not evidence of anything on its own — if a higher level agrees on every
occasion, the measurement has shown that raising autonomy would change nothing, which is a finding
about the policy rather than about the system. The number that bears on promotion is the hit rate of
the extra actions a higher level *would* have taken, and where those have no recorded outcomes the
result says so instead of reporting a rate. "A higher autonomy level would have acted more often"
reads like an argument for higher autonomy and is not one.

## 7. Verification

| Gate | Result |
|---|---|
| `dotnet build` (Release, whole solution) | **Succeeded** — 0 warnings, 0 errors |
| `dotnet test` (Release, whole solution) | **Passed** — `build_exit=0 test_exit=0` |
| Suite total | **1607 total, 1607 passed, 0 failed, 0 skipped** |
| Point-in-time enforcement, lookahead prevention, admissibility | **Passed** — `PointInTimeGuardTests`, `ValidationPredictionTests` |
| Bitemporal replay | **Passed** — in memory and against a real PostgreSQL |
| Hit rate, false positives, false negatives | **Passed** — `ValidationMetricsTests`, `ValidationServiceTests` |
| Calibration | **Passed** — perfect, overconfident, thin-band and empty cases |
| Benchmark calculation | **Passed** — `BenchmarkAndPerformanceTests` |
| Shadow / actual matching | **Passed** — `ShadowComparisonTests`, `ValidationServiceTests` |
| Deterministic replay | **Passed** — same history, same admissions, same numbers |
| Insufficient-data handling | **Passed** — every metric withholds rather than prints |
| Architecture rules | **Passed** — 46 |
| Secret scan | **Passed** — 0 findings |

Per assembly:

| Assembly | Total | Passed | Failed | Skipped |
|---|---:|---:|---:|---:|
| AI.Investment.Domain.UnitTests | 890 | 890 | 0 | 0 |
| AI.Investment.Application.UnitTests | 281 | 281 | 0 | 0 |
| AI.Investment.Safety.Tests | 247 | 247 | 0 | 0 |
| AI.Investment.Integration.Tests | 122 | 122 | 0 | 0 |
| AI.Investment.Architecture.Tests | 46 | 46 | 0 | 0 |
| AI.Investment.Api.Tests | 21 | 21 | 0 | 0 |
| **Total** | **1607** | **1607** | **0** | **0** |

### 7.1 What the gates caught

Two defects, both found by running the thing rather than by reading it.

1. **An owned entity shared between rows.** The integration test seeded several observations from one
   `IngestionSubject` instance. An owned entity belongs to exactly one owner, so the change tracker
   attributed it to the first observation and left the rest with nothing — which arrives as a
   not-null violation on `subject_kind` rather than as anything that names the cause. Each
   observation now gets its own instance.
2. **A test asserting a sentence the report does not contain.** The integrity test looked for "not
   investment advice" where the report says "is investment advice". The assertion was corrected to
   the text that is actually rendered; the report was not reworded to match the test.

### 7.2 The mutation gate

Not run, and not extended, and this is a deliberate choice rather than an omission. The gate covers
seventeen files that decide whether something is allowed to happen; Phase 7 changed none of them, so
the Phase 6 result — 73.53 % against a break threshold of 70 % — stands unaffected, and re-running it
would be repeating a completed verification.

**Extending it to the validation domain is the recommended follow-up.** `PointInTimeGuard`,
`ConfusionMatrix`, `CalibrationCurve` and `PerformanceCalculator` are exactly the kind of pure,
boundary-heavy code the gate exists for, and they are currently outside it.

## 8. The report

`docs/Reports/VALIDATION-REPORT.md` was produced by `ValidationPersistenceTests` running the real
`ValidationService` against a real PostgreSQL, and committed from that run's output. Nothing in it was
composed by hand.

Its finding: **not established.** No prediction survived the point-in-time guard, because the
repository holds no opportunities, no price history and no shadow measurements. Every metric reports
its own absence with a reason. That is what this phase's honest result looks like today.

## 9. What would make the report say something

Three things, in this order:

1. **Ingested price history** for the subjects and for the index proxy, so entries, exits and the
   benchmark can be priced. Without prices, nothing resolves.
2. **Opportunities that cite their evidence by the identifiers of stored observations.**
   `EfPredictionCatalogue` establishes when a prediction became knowable from the publication times
   of the observations it cites. A discoverer that mints fresh claim identifiers instead of citing
   what it read leaves that unestablishable, and every one of its predictions is refused — correctly,
   and visibly, as a data gap rather than as a smaller and quietly better sample.
3. **Time.** A thirty-day horizon needs thirty days to elapse before anything is judgeable.

## 10. Known limitations

1. **One window is not a track record.** A result over a single period says nothing reliable about
   the next one, and the report says so in every rendering.
2. **Survivorship is not corrected for.** If the subjects measured are the ones the repository still
   holds, a subject that failed and was removed would not appear.
3. **Costs are a flat rate on both legs.** Slippage, market impact, borrow costs and taxes are not
   modelled at all — for either side.
4. **The decision time is the opportunity's creation time**, because the aggregate keeps one creation
   time and one status-change time and cannot date its later transitions individually. Taking the
   earliest defensible moment admits the least evidence, which is the direction a measurement should
   err in.
5. **No walk-forward or out-of-sample split.** The apparatus measures one declared window. Splitting
   it is a Phase 8 concern and would be meaningless before there is data.

## 11. Safety boundary

Unchanged. Autonomy remains **L3**. The only execution venue in the solution reports itself
simulated, `Capability.FinancialExecution` is still refused unconditionally and structurally, and no
grant can be issued for it. Phase 7 introduced no live credential, no live venue and no real-money
path, and its own namespaces are structurally unable to reach the action seam, the venue, the write
authorisation window or autonomy administration.

## 12. Dependencies on previous phases

Phase 2's bitemporal `Provenance` and `Observation` are what make any of this possible; Phase 3's
`KnowledgeCutoff` and `MetricResult.EvidenceAvailableAtUtc` are reused unchanged; Phase 5's
opportunity lifecycle supplies the predictions; Phase 6's `ShadowDecision` supplies the
shadow-versus-actual half. Nothing was reimplemented.

## 13. Recommended next phase

§P puts **Phase 8 — bounded autonomy** next, and marks it *only if Phase 7 justifies it*. It does not.
The report establishes nothing, so there is no evidence on which to raise any capability to L4, and
the correct reading of this phase is that the prerequisite for Phase 8 has not been met rather than
that Phase 8 is ready to begin.
