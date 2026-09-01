# Block 3 — plan and prerequisite check

**Status: plan only. No production code written. Block 3 implementation has not started.**

Grounded in a read-only inspection of the current tree, not in what the roadmap says should exist.
Every claim below cites the file it came from.

---

## The shape of Block 3

Block 3 turns a screen that fires on one instrument into a system that produces *many* judged
predictions, ranks them, acts on them in simulation, and measures whether any of it worked. The
promotion gate at the end already exists and is heavily tested; Block 3's job is to produce evidence
it will accept, and to leave it refusing until that evidence clears the bar.

**Almost all of the machinery is already built.** What is missing is smaller than it looks, and what
is broken is more important than what is missing.

---

## Prerequisite check

### Blockers — these make Block 3 non-functional or unsafe as it stands

**B1. The benchmark has no data, so the promotion gate can never open.**
`Validation:BenchmarkSubjectIdentifier` is `"SPY"` (`appsettings.json`). SPY is not in the twenty-name
universe and has no observations. `BenchmarkReturn` therefore measures as `Unavailable`, so
`ExcessReturn` is unavailable, so `ValidationReport.Decide` returns `NotEstablished`, so
`PromotionAssessment` refuses with `PerformanceNotEstablished` — permanently, no matter how good the
predictions are. Either ingest SPY, or change the benchmark to something derivable from the twenty
(an equal-weight basket of the universe). **Settle this before the first validation run**, because
`BenchmarkDefinition.EnsureDeclaredBefore` throws if the benchmark was declared after the run began —
adjusting the benchmark once results are visible is structurally refused, and correctly so.

**B2. Equity is zero, so the first paper trade is refused.**
`LedgerAccount.ContributedCapital` has **no production write path anywhere in `src/`**.
`LimitEngine.CheckConcentration` measures share against `CurrentEquity` and refuses rather than
divides when equity is zero. With `MaxConcentration: 0.25` configured, every trade is refused for
un-measurable concentration. A deliberate, audited capital-seeding action is a prerequisite to paper
trading, not a feature of it.

**B3. `OpportunityExecutor` is orphaned.**
Registered at `Infrastructure/DependencyInjection.cs:181` and called from nowhere.
`SimulatedExecutionProposal` has zero references outside its own file; `VenueOrder.Create` and
`ExecutionRequest.Create` have no callers. The cycle's `ExecuteOrEscalate` stage falls through to
`Nothing()`. Paper trading is not a path that exists — the position tables are permanently empty in
production because nothing can write to them.

**B4. `ActionProposal` is not persisted, so approve-then-execute cannot cross a request.**
No `DbSet`, no EF configuration, no entry in the model snapshot, and the type is not shaped for EF
(no private parameterless constructor, get-only properties, `IActionParameters` unmapped). Only the
bare `Guid ProposalId` is stored, as a correlation column with no foreign key.
`IApprovalTokenStore.ConsumeAsync` and `ApprovalWorkflow.ApproveAsync` both require a live
`ActionProposal` object, and there is no way to rebuild one from the database. At
`PrepareForApproval` — the mode Block 3 runs in — a human approves and something later executes. If
those are two requests, the flow cannot be built on what exists. This is the prerequisite already
scoped in `artifacts/audit/ACTIONPROPOSAL-PREREQUISITE.md`.

**B5. A replayed fill posts duplicate ledger entries.**
In `OpportunityExecutor.ExecuteAsync` the ledger append is unconditional while the position append is
idempotent on the venue-reference unique constraint — and its `bool` return is discarded. A fill that
the position store correctly rejects as a duplicate still posts a second full set of ledger entries,
silently corrupting cash, exposure and every limit computed from them. Fix before any paper trade.

**B6. A ranked opportunity cannot be re-ranked.**
`Opportunity.Rank` requires status `Evaluated`, and `MoveTo` refuses a backward transition. Once
`Ranked`, a stored candidate can never be re-scored. Any ranking design that wants to compare stored
candidates and re-score them needs a deliberate domain change; the alternative is to score once at
creation and *select* later, which is what this plan proposes.

**B7. There is no cross-instrument query surface.**
`IOpportunityRepository` offers `AddAsync`, `GetAsync(id)` and `ListAsync(status, limit)`. No query by
score, by subject set, or by window. Ranking and selection both need one.

**B8. Five cycle stages are unimplemented.**
`Identify`, `ExecuteOrEscalate`, `Monitor`, `MeasureOutcome` and `Record` all fall through to
`Nothing()` in `EquityReviewWorkPlan` — the only registered work plan. The cycle advances through
them and completes. This is honest today, but Block 3's monitor-and-measure arm does not exist in the
cycle.

**B9. The circuit breaker never runs, so promotion would be a ratchet.**
`AutonomyCircuitBreaker.SweepAsync` is registered at `DependencyInjection.cs:143` and is not called
from any hosted service, controller or cycle. Automatic demotion does not happen. A promotion
mechanism whose demotion counterpart is dead should not be exercised.

**B10. Production configuration runs nothing.**
`appsettings.json` has `Safety.Capabilities: []` and a `Limits` section containing only a currency.
Everything Block 3 does works only under `appsettings.Development.json`. Decide deliberately that
Block 3 is a Development-environment exercise — which is almost certainly right — rather than
discovering it at deployment.

### Constraints — accepted, and designed around

| | |
| --- | --- |
| **C1. One year of history.** | 250 sessions. After a 60-session warm-up and a 21-session horizon, **169 decision points per instrument, 3,380 across the universe.** |
| **C2. No corporate-actions data.** | Any instrument that split inside the window is refused as `UnexplainedDiscontinuity` and drops out of the sample. This must be counted and reported, not discovered later. |
| **C3. Twenty API calls per day.** | Exactly one price call per instrument per day, with no headroom. **Block 3's evidence must therefore come from a historical sweep over stored data, costing zero calls — not from a forward run.** |
| **C4. One cycle handles one instrument**, and `CycleMaxActions = 1`. | Multi-candidate work cannot be a single cycle. |
| **C5. `MaxActionsPerCapabilityPerDay = 25`.** | A sweep creating a hundred candidates breaches this on day one unless it is batched into one action. |
| **C6. `ContinuousBounded` (L5) is unreachable.** | `PromotionAssessment.MaximumPromotableMode` caps at `AutoExecuteBounded`. Block 3's ceiling is L4, for `SimulatedExecution` only. |

---

## Step 0 — the measurement that decides whether Block 3 is viable

**Before any production code**, a read-only rehearsal. It costs nothing, changes nothing, and answers
the only question that matters.

Replay `PriceRecoveryRule.Evaluate` as at each of the 169 decision dates for each of the 20
instruments, over the stored series, and report:

1. how many of the 3,380 decision points were **evaluable** (not refused for `NotEnoughHistory` or
   `UnexplainedDiscontinuity`);
2. how many produced a **candidate**;
3. the **distribution of stated probabilities** — do they fill ten calibration bins with at least ten
   samples each, or do they cluster in two?
4. how many would **resolve inside the stored window** (a prediction whose horizon runs past
   2026-08-28 cannot be scored);
5. **per-instrument dropout** from split refusals (constraint C2, measured).

The bar it is testing: `PromotionCriteria.Standard` requires **100 scored predictions**, a hit rate
of 0.60, a Brier score of 0.20 or better, and **30 shadow divergences** with known outcomes.
`ConfusionMatrix` and `CalibrationCurve` each need at least 20 samples, with 10 per calibration bin.

**100 scored predictions out of 3,380 decision points is a 3.0% firing rate.** Whether a 10%-drawdown
rule fires that often over one year is an empirical question with an answer already sitting in the
database. If the answer is materially below 100, the validation and promotion arms of Block 3 are
non-functional however well they are built, and the rule parameters or the universe have to change
first — which is a very different piece of work, and much cheaper to discover now.

---

## 3A — Multi-candidate detection

**What exists.** `IOpportunityDiscoverer.DiscoverAsync(subject, nowUtc)` already returns a *list*.
`PriceRecoveryDiscoverer` is the only implementation and returns at most one draft, for one subject.
There is no universe abstraction and no batch discoverer.

**What to build.** A `UniverseSweep` application service: takes an instrument set and an as-at
instant, calls every registered `IOpportunityDiscoverer` for every subject, returns all drafts.

**Design decisions, stated:**

- **The interface does not change.** It is already list-returning and per-subject; a sweep is a
  caller, not a new abstraction.
- **The universe comes from the watch store** — the `Target.Identifier` of enabled watches — so it
  stays the deliberate twenty and does not become a second source of truth that can drift from the
  baseline.
- **The cycle is not touched.** One cycle stays one instrument. The sweep is a separate deliberate
  operation, gated like the backfill was. This respects C4 without redesigning the cycle.
- **One action, not N.** The sweep records its drafts under a single `opportunity.record-batch`
  proposal whose parameters name the instrument set and the count, so the fingerprint covers what was
  actually recorded and `MaxActionsPerCapabilityPerDay` (C5) is not breached by volume.

**Safety.** Draft creation is `OpportunityManagement` with `ActionEconomics.NoFinancialEffect()` —
already gated, already audited, no financial effect. Nothing here can move money.

---

## 3B — Ranking and priority

**What exists.** `Opportunity.Rank(OpportunityScore, nowUtc)` is a per-aggregate status transition
that attaches a number. `OpportunityScore.From(MetricResult)` requires a versioned metric in
`UnitOfMeasure.Ratio`. **There is no comparison of any kind anywhere in the codebase** — no
comparator, no top-N, no ordering by score.

**What to build.** Two separable things, and keeping them separate is the point.

**Score** is a claim about the instrument. It already exists:
`score.price-recovery-base-rate`, successes over trials, version 1.0.

**Priority** is a claim about the book. Ranking is ordering, and it must be deterministic and
explainable:

1. score descending;
2. then stated confidence descending (`trials / (trials + 10)`);
3. then drawdown descending;
4. then instrument ordinal — so two runs over identical data produce an identical order, and a
   backtest is reproducible.

Then a `SelectionPolicy` applies the portfolio constraints to the ranked list — one open position per
instrument, concentration headroom, available cash — to produce a shortlist. A score that cannot be
acted on is still a valid prediction; it just is not a trade.

**Because of B6**, score at creation and select later. Add `ListRankedAsync(window, limit)` to
`IOpportunityRepository` (B7), ordered by score, with the tie-breaks above applied in the query so
ordering is not re-derived differently in two places.

---

## 3C — Prediction generation

**What exists — and the thing worth understanding first.** `PredictionRecord` exists but is **not
persisted**. There is no predictions table and no prediction store. `EfPredictionCatalogue` derives
predictions from `Opportunity` rows at validation time:

| Prediction field | Derived from |
| --- | --- |
| `PredictionId` | `OpportunityId` |
| `DecidedAtUtc` | `Opportunity.CreatedAtUtc` — discovery time |
| `ResolvesAtUtc` | `Economics.TimeHorizon.EndUtc` |
| `Direction` | `OpportunityStatus` — Proposed/Approved/Executing/Active/Closed → Positive; Rejected/Expired → Negative; Draft/Evaluated/Ranked → **Abstain** |
| `StatedProbability` | `Economics.SuccessProbability` |
| `EvidenceAvailableAtUtc` | max `PublishedAtUtc` over cited claims, or **null** if any claim does not resolve — in which case the prediction is refused |

**So: a prediction *is* an opportunity.** To generate a hundred predictions you create a hundred
opportunities with historical `CreatedAtUtc`.

**What to build.** Run 3A's sweep as at each decision date across the stored window, stamping
`CreatedAtUtc` at the decision instant and reading the series with `asAtUtc` at that same instant.
The point-in-time machinery already supports this exactly — `PriceSeriesReader.ReadAdjustedAsync`
takes an `asAtUtc`, and `BacktestEngine.Replay` refuses anything that used evidence published after
its own decision. This costs zero provider calls, satisfying C3.

**Two decisions to make explicitly rather than discover:**

- **A swept candidate that is never proposed stays `Ranked`, which maps to `Abstain`, which is not
  scored.** For candidates to count as calls they must be carried through to at least `Proposed`.
  Decide where that transition happens and record it; do not let the sample size be an accident of
  which status rows happened to reach.
- **Do not add a `Predictions` table yet.** The derived path works, and a second source of truth for
  the same fact is how two sources of truth start disagreeing. Revisit only if direction genuinely
  needs to be stated independently of status — which is a real possibility once shorts or abstentions
  matter, but is not needed now.

---

## 3D — Paper trading and simulation

**What exists.** The venue, the executor, the ledger, the position store, the portfolio read model
and the endpoints are all present and registered. `SimulatedVenue` models commission
(`notional × 0.001`) and a `$1` minimum fee, rejects only on currency mismatch, and **fills at the
caller's stated price** — no slippage, no partial fills, no rejection for liquidity or market hours.
`PositionCalculator.Replay` derives holdings and realised PnL from append-only `PositionEvent`s;
`CapitalLedger.Balance` derives cash. Mark-to-market exists in `PortfolioReader` and uses the latest
stored close, never a fabricated price.

**The flow, once the blockers are closed:**

shortlist → `VenueOrder` → `SimulatedExecutionProposal.For(...)` under `Capability.SimulatedExecution`
→ approval token issued (at L3 a human approves each one) → `OpportunityExecutor.ExecuteAsync` →
kill switch re-read → limits evaluated against ledger exposure → policy gate → **approval consumed
inside the effect** → venue fill → ledger and position writes in one transaction →
`Opportunity.BeginExecution` → `Activate`.

**What has to be decided or added:**

- **Position sizing does not exist anywhere.** It needs a deliberate rule — fixed fractional of
  equity, capped by `MaxPositionSize` and `MaxConcentration`. State it as a *versioned calculation*,
  like the score, so a backtest can be re-run against the sizing that was actually used rather than
  today's.
- **Exits.** The rule states a `TargetPrice` and a 21-session horizon, but nothing monitors a
  position. `Monitor` is one of the unimplemented stages (B8). Sweep-driven exits are consistent with
  everything else here and cost no provider calls.
- **Slippage is not modelled**, and for a record that will be compared against a benchmark that is
  optimistic in the system's favour. Either model it or declare it — `ValidationReport.Limitations`
  exists for precisely this, and declaring it is the honest cheap option.
- **B5 first.** Duplicate ledger postings would corrupt every limit computed from the ledger.

**Safety boundary, absolute.** `Capability.SimulatedExecution` only.
`SimulatedExecutionProposal` structurally cannot emit `FinancialExecution`, which is refused by
`policy.financial-execution-unavailable@1` before any configuration is consulted. An architecture test
asserts by reflection that every `IExecutionVenue` in the solution reports `IsSimulated == true`.
Nothing in Block 3 changes any of that.

---

## 3E — Validation thresholds

**What exists, and it is nearly complete.** `ValidationService.RunAsync` computes the confusion
matrix, the calibration curve, system and benchmark and excess return, the shadow comparison, data
gaps, and a verdict. Sample floors are already in the domain: `ConfusionMatrix.MinimumSample = 20`,
`CalibrationCurve.MinimumSample = 20` with `MinimumPerBin = 10` across 10 bins,
`ShadowComparisonResult.MinimumSample = 20`, `PerformanceCalculator.MinimumRoundTrips = 5`.

`ValidationOptions` carries **no acceptance criteria** — it declares only what is measured. The
criteria live in `PromotionCriteria.Standard` as code constants, deliberately: *"a bar that can be
lowered from a settings file is not a bar."* **Do not move them into configuration.**

**The one real gap: validation reports are not persisted.** `RunAsync` returns a report and stores
nothing; `ValidationController` recomputes it on every request, with a fresh `RunId` each time. But
`PromotionAssessment` requires `assessment.ValidationRunId`, and `PromotionWarrant.Issue` refuses
without one — so a warrant would cite a run id that exists nowhere and can never be re-examined.
**Add a `ValidationReport` persistence path before any warrant is issued.** This is a prerequisite of
3F, not of 3D.

Beyond that, 3E needs no structural work. It needs a benchmark that resolves (B1) and enough sample
(Step 0).

---

## 3F — The promotion gate

**Block 3 does not build this.** It already exists, in full, with roughly 100 tests across six files:
`PromotionAssessment.Evaluate`, `PromotionWarrant.Issue`, `AutonomyGrant.IssueBounded`,
`LiveVenueGate.Evaluate`. The endpoints are read-only by design, pending authentication.

**What must be true to promote**, from the code:

1. a validation report exists, is at most 90 days old, and has `Verdict == BetterThanBenchmark`;
2. `Matrix.Scored >= 100`, `HitRate >= 0.60`, `Calibration.BrierScore <= 0.20`, `ExcessReturn >= 0` —
   each **measured**, not merely non-failing;
3. `Shadow.ShadowWouldHaveExecutedAndActualDidNot >= 30` and `DivergenceHitRate >= 0.60`;
4. the capability is not `FinancialExecution` and not a safety-administration capability, and the
   proposed mode is at most `AutoExecuteBounded`;
5. a **named person** issues the warrant with a justification, and the assessment is re-run at the
   moment of issue;
6. a **separate** act grants autonomy citing that warrant, refusing before building a proposal if the
   warrant does not cover the request.

**Block 3's ceiling is a warrant for `Capability.SimulatedExecution` at `AutoExecuteBounded`.** No
`FinancialExecution`. No `LiveVenueAuthorization`. No real money. `LiveVenueRefusal.None` stays
unreachable, as it is today.

**And B9 first.** The gate should not be exercised while its demotion counterpart is dead code.

---

## Order of work

| | Step | Cost |
| --- | --- | --- |
| 0 | **Rehearsal** — count what the rule would produce over the stored window | read-only, zero calls |
| 1 | Blockers **B1** (benchmark), **B2** (seed capital), **B5** (duplicate postings); **B4** if approval and execution cross a request | production code |
| 2 | **3A** universe sweep + **3B** ranking and selection, with **B7** query surface | production code |
| 3 | **3C** historical prediction generation, using 3A | zero calls |
| 4 | **3E** validation run against real predictions + report persistence | zero calls |
| 5 | **3D** paper trading, wired last, once limits and capital are real | zero calls |
| 6 | **3F** assessed, not issued. **B9** before any warrant | — |

Step 0 gates everything. If the rehearsal shows the rule cannot produce a hundred scorable
predictions from the accepted baseline, steps 2 to 6 are premature and the right work is a different
one.

## Explicitly out of scope

Anything touching `Capability.FinancialExecution`. Live venue activation. Raising autonomy beyond
`PrepareForApproval` without a warrant. Changing `PromotionCriteria` or `DemotionThresholds`.
Re-cutting the frozen Phase 2 baseline. Upgrading the subscription or spending the purchased extras.

**No Block 3 production code has been written. Awaiting approval of this plan.**
