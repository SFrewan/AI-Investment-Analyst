# Roadmap memory

**Purpose.** This is the persistent navigation layer for the project's roadmap. It exists so that a
future agent does not have to rediscover which roadmap is canonical, what the earlier roadmaps were,
what the 28-item restatement contained, or what has actually been built. It was created on
2026-08-28 from a reconciliation performed against the repository's own documentation.

**It is a map, not a source.** The authoritative documents are named in every section, and where this
document and one of them disagree, the named document wins.

---

## Rules governing this document

1. **Never renumber.** Phase numbers 0 to 8 are the numbering the repository's history, its phase
   documents and its verification log are written in. A phase is never renamed, renumbered or moved,
   whatever a later restatement of the programme calls it.
2. **Never reconstruct missing roadmap history.** Section 5 records exactly what about the 28-item
   restatement is unrecoverable. Those items are not to be reconstructed from memory, inferred from
   the surviving row labels, or filled in with plausible titles. If the original source document is
   found, it may be incorporated — with the source identified and dated.
3. **Never treat a development block as a new phase.** The canonical roadmap ends at Phase 8. Work
   after it is a named, unnumbered **development block** (section 9). There is no Phase 9.
4. **Never use this document as a substitute for the authoritative phase documents.** It summarises
   and points; it does not decide. `PHASE-0-…md` through `PHASE-8-…md`, `VERIFICATION-LOG.md` and
   §P remain the record of what was built and what was verified.
5. **History is preserved.** Corrections are appended with a date and a reason. Nothing here is
   quietly deleted, in keeping with rule 2 of [README.md](README.md).

---

## 1. Which roadmap is canonical

**The canonical roadmap is section P, "Development Roadmap", of
[../PHASE0_AUTONOMOUS_PLATFORM_ARCHITECTURE.md](../PHASE0_AUTONOMOUS_PLATFORM_ARCHITECTURE.md).**

It defines **phases 0 to 8**, each with an objective, key components, dependencies, an output, a
principal risk, an exit criterion and an autonomy level. Its opening rule:

> Each phase has an **exit criterion**. No phase begins until the previous one's criterion is met.
> Autonomy level is stated per phase and does not advance ahead of the measurement that justifies it.

Three consequences, all of which have already been tested by events in this project:

- **Phase numbers are permanent.** Phases 0, 1 and 2 were implemented and documented under this
  numbering before any restatement of the programme existed, so it is also the numbering the git
  history is written in. Adopting a different numbering would move completed work under new
  headings for no engineering reason.
- **There is no Phase 9 in the canonical roadmap.** §P ends at Phase 8. A tenth heading would imply
  a roadmap that does not exist.
- **Phase 8 is conditional.** §P marks it *"only if Phase 7 justifies it."* That condition is not
  currently met — see section 7.

---

## 2. The canonical roadmap, 0 to 8

A summary for navigation. **§P is the authoritative source** for the full Key components, Output and
Principal risk columns, and for the two assessments attached to the table.

| Phase | Objective | Depends on | Exit criterion | Autonomy |
|---|---|---|---|---|
| **0. Foundation hygiene** *(1–2 days)* | Make everything after this safe and reviewable | — | Builds clean with warnings-as-errors; `/health` returns 200; CI green; no secret can be committed without CI failing | L0 |
| **1. Domain core + the safety seam** *(1 week)* | Establish the seam everything else attaches to | 0 | The slice works with four levels of tests; architecture tests fail on a layering violation; **a test proves no path writes without a `PolicyDecision`** | L0 |
| **2. Data plane** *(2–3 weeks)* | Trustworthy, replayable, point-in-time-correct data | 1 | 50 tickers ingested with full provenance, and any analysis replays byte-identically from stored raw responses | L1 |
| **3. Deterministic analytics** *(2 weeks — no AI)* | A defensible number before any model touches it | 2 | A stored bundle reproduces an identical score; every score input is a traceable `Claim` | L1 |
| **4. AI layer** *(3–4 weeks)* | Judgement on top of trustworthy data | 3 | Evaluation harness meets agreed thresholds for schema validity, groundedness and stability. **Below threshold, the phase does not end** | L2 |
| **5. Opportunity, approval, capital** *(3–4 weeks)* | The full decision path, simulated | 4 | A complete path replays end to end; safety suite green including mutation testing | L3 |
| **6. Continuous operation** *(2–3 weeks)* | The system notices things on its own | 5 | Runs unattended for two weeks: no duplicate actions, no runaway cost, no unhandled escalation; shadow-mode data accumulating | L3 (+shadow L4) |
| **7. Validation** *(open-ended — the real test)* | Find out whether any of this works | 6 | A performance report exists and has been read | L3 |
| **8. Bounded autonomy** *(only if Phase 7 justifies it)* | Automatic execution of the lowest-risk, reversible action classes | 7 | A named, narrow capability runs at L4 for a defined period with zero policy breaches | L4 → L5 (per capability) |

Current per-phase status — Verified, Implemented, or blocked — is in the status table of
[README.md](README.md), which is updated as each phase closes. This document does not duplicate it.

---

## 3. Roadmap lineage

Three roadmaps exist in the repository. They are a sequence, not alternatives, and the earlier two
are retained because they record why the current one is shaped as it is.

### 3.1 `SYSTEM_ARCHITECTURE.md` §15 — the original vision

Six phases, written before any code was audited: **Foundation · Data · Analysis · AI · Validation ·
Continuous Improvement**. It is a statement of intent rather than an executable plan — no exit
criteria, no dependencies, no autonomy model. Its lasting contributions are §5 (Core Principles),
§11 (Human Approval), §18 (profitability is a hypothesis) and §19 (see section 6 below).

### 3.2 `AUDIT_AND_TARGET_ARCHITECTURE.md` §10 — the Phase 0 audit roadmap

Seven phases, **0 to 6**, and the first to attach an exit criterion to each. Two differences from
the canonical roadmap matter when reading old text:

- **Phase 5 was "Opportunity, approval, audit, dashboard"** — the dashboard was named at this stage.
- **Phase 6 was "Validation"** — what is now Phase 7.

It also states the sentence that still governs the programme:

> Until this exists, the correct description of the system is "an untested hypothesis." No automated
> execution should be discussed before this phase produces a number.

### 3.3 `PHASE0_AUTONOMOUS_PLATFORM_ARCHITECTURE.md` §P — canonical

Nine phases, **0 to 8**. What it changed from §10:

- **Inserted continuous operation as Phase 6**, which did not exist as a phase before.
- **Renumbered validation from 6 to 7.**
- **Added bounded autonomy as Phase 8**, conditional on Phase 7.
- **Added a per-phase autonomy level** (L0 → L5), which is the mechanism that keeps capability from
  outrunning measurement.
- **Moved the safety seam into Phase 1**, ahead of anything dangerous to gate — deliberately, and
  §P says why.

**Nothing in this lineage is to be edited.** The renumbering from §10 to §P happened before Phase 0
was implemented, so no completed work was moved. That is the reason no renumbering has been
permitted since.

---

## 4. The 28-item restatement — recovered mapping

Preserved exactly as it stands in [README.md](README.md), including its historical framing. This is
a record, not a working document.

> **Recorded 2026-08-27.** A later restatement of the programme described a 28-item sequence
> (Foundation, Knowledge & Analytics, Ingestion, Knowledge Graph, Financial Analytics, … through
> Autonomous 24/7 System). That list is a finer decomposition of the same programme, not a competing
> plan, and adopting its numbering would have renumbered three already-documented phases and moved
> completed work under new headings for no engineering reason. The canonical numbering therefore
> stays as it is, and the finer list is recorded here as a mapping onto it:

| Finer programme item | Canonical phase |
|---|---|
| Foundation & Governance | 0 — Foundation hygiene |
| Knowledge & Analytics Foundation | 1 — Domain core + safety seam |
| Data Ingestion & Source Intelligence | 2 — Data plane |
| Knowledge Graph / Evidence Intelligence | 2 (evidence/provenance) and 3 (relationships between measurements) |
| Financial Analytics Engine | **3 — Deterministic analytics** |
| Market & Event Intelligence | 3 (deterministic market measures) and 5 (events) |
| Opportunity Engine | 5 — Opportunity, approval, capital |
| Strategy / Portfolio / Rotation & Capital Allocation | 5 |
| Risk Engine · Policy & Authorization Engine | 1 (the seam) and 5 (limits, kill switch, capital ledger) |
| AI / Agent System | 4 — AI layer |
| Backtesting · Simulation · Paper Trading | 7 — Validation, on the point-in-time guard built in 3 |
| Broker / Venue Abstraction · Execution Engine · Reconciliation | 5 (`SimulatedVenue`) then 8 |
| Controlled Real Money · Progressive Autonomy | 8 — Bounded autonomy |
| 24/7 Orchestration · Monitoring & Alerting | 6 — Continuous operation |
| Security & Secrets Hardening | 0 (established) and ongoing |
| Failure / Chaos / Concurrency Testing · Full System Validation · Production Readiness | 7 |
| Autonomous 24/7 System | 8 |

> Nothing already implemented was renumbered, renamed or moved as a result of this reconciliation.

**One additional fact established on 2026-08-28.** The seventeen rows above are in the restatement's
own programme order: the table opens with exactly the five items the preamble names in sequence
(Foundation, Knowledge & Analytics, Ingestion, Knowledge Graph, Financial Analytics) and closes with
exactly the item the preamble names last (Autonomous 24/7 System). So the **sequence** of the
28-item list survives even where its **granularity** does not.

---

## 5. The 28-item restatement — what is not recoverable

**Do not reconstruct any of the following from memory, from the row labels above, or from a general
knowledge of how such programmes are usually decomposed. Doing so would put invented history into
the permanent record, and nothing downstream would be able to tell it from the real thing.**

| Not recoverable | Why |
|---|---|
| **The verbatim title of each of the 28 items** | Section 4 records labels for 17 *rows*. Several rows merge more than one item behind a `·` or a `/`, and which side of a separator was one title and which was two cannot be told from the surviving text. |
| **The exact item-to-row grouping** | Counting the row labels with `·` read as a separator and `/` read as part of a name yields **26**; reading `/` as a separator in the two ambiguous rows yields as many as **29**. The recorded figure is **28**. Between two and three items are therefore merged in a way the record does not disambiguate, and no arithmetic on the surviving text resolves it. |
| **Items 6 through 27, individually** | Only items 1–5 (in order) and item 28 are named in the preamble. Everything between them survives as grouped subject matter in row order, not as titles. |
| **Per-item exit criteria, dependencies, estimates and autonomy levels** | The restatement's own detail was never written into the repository. The README preserved the *mapping*, not the source list. |
| **The document the restatement came from** | It is not in `docs/`, not in the repository root, not in `prompts/`. Only the fact of it, its date of record and its cardinality survive. |

**If the original source is ever found**, it may be incorporated here — appended, not substituted —
with the source document identified, its location stated, and the date of incorporation recorded.
Until then this section is the answer, and the answer is that the list is not recoverable.

**What is recoverable is sufficient for planning.** The subject matter, the ordering and the mapping
onto Phases 0–8 all survive. What does not survive is the ability to present the list as a numbered
artefact — and the canonical roadmap, which is §P, was never the list in the first place.

---

## 6. The original long-term vision

`SYSTEM_ARCHITECTURE.md` §19, "Future Direction", records nine capabilities:

- Portfolio analysis
- Watchlists
- Automated monitoring
- Real-time alerts
- Historical strategy evaluation
- Portfolio optimization
- Paper trading
- Broker integration
- Controlled automated execution

**These were described as *"possible future capabilities"* and explicitly placed outside the initial
MVP** — the section closes: *"These capabilities are outside the initial MVP unless explicitly
approved later."* They are a wish list written before the architecture was audited, not a phase
decomposition and not a commitment. Several have since been built or partially built under the
canonical phases; several are deliberately refused (section 8). Current status for each is in
section 7.

---

## 7. Current capability status

Established by the reconciliation of 2026-08-28. **Evidence is the phase documents and the
repository, not this table** — the Source column names where to look. Status vocabulary:
GREEN/VERIFIED · IMPLEMENTED · PARTIALLY IMPLEMENTED · NOT IMPLEMENTED · BLOCKED/CONDITIONAL.

| Capability | Status | What exists | What is missing | Source |
|---|---|---|---|---|
| **Data ingestion** | PARTIALLY IMPLEMENTED | Gateway with source admission and rate limiting; SEC EDGAR connector; operator price-history connector | Never executed against a live source; no news or market-vendor API connector; Phase 2's "50 tickers" criterion unmet | `PHASE-2-…md` §13 |
| **Historical / bitemporal data** | IMPLEMENTED | Three-timestamp `Provenance`; admission on publication only; restatement resolution in the read side | The store is empty | `PHASE-7-…md` |
| **Evidence / provenance** | IMPLEMENTED | `Claim<T>`, provenance, citation by stored observation id, an IL test forbidding retrieval-time admission | An observation's provenance is not checked against the source registry | `PHASE-2-…md` §13 |
| **Deterministic analytics** | GREEN / VERIFIED | Ratios, growth, sums, versioned scoring engine, golden files | — | `PHASE-3-…md` |
| **AI agents** | GREEN / VERIFIED | Financial, News, Risk, Synthesis; groundedness validator; prompt versioning; evaluation harness | No chat model configured in any environment | `PHASE-4-…md` |
| **Opportunity discovery** | IMPLEMENTED | `PriceRecoveryRule`, `PriceRecoveryDiscoverer`, `EquityReviewWorkPlan`, registered and tested | One rule, one asset class; never run on real data | `PHASE-8-…md` §11 |
| **Risk** | PARTIALLY IMPLEMENTED | Risk tier calculator, limit engine, exposure snapshot, mandatory `OpportunityRisk` | Per-instrument exposure map is empty; no portfolio-level risk | `PHASE-5-…md` §13.4 |
| **Portfolio** | NOT IMPLEMENTED | — | Portfolio state, position management, optimisation | `PHASE-5-…md` §13.4 |
| **Capital** | GREEN / VERIFIED | Double-entry `CapitalLedger`; no settable balance anywhere | Single currency; no FX | `PHASE-5-…md` §7 |
| **Continuous operation** | GREEN / VERIFIED | Cycle state machine, leases, budgets, cooldowns, backpressure, outbox, hosted services | Off by default; never run for a real fortnight | `PHASE-6-…md` §12 |
| **Shadow mode** | GREEN / VERIFIED | `ShadowDecision`, recorder, evaluator, structural boundary test | No accumulated records | `PHASE-6-…md` §12 |
| **Validation** | IMPLEMENTED, result **not established** | Backtest engine, point-in-time guard, hit rate, calibration, FP/FN, fingerprinted benchmark, shadow-vs-actual | No admissible prediction survived the guard, because the repository holds no data | `Reports/VALIDATION-REPORT.md` |
| **Autonomy** | L3, promotion BLOCKED / CONDITIONAL | Per-capability grants; promotion warrant; bounded-execution rule; live-venue gate; automatic demotion with per-capability signals now counted | No warrant exists and none can while Phase 7 says what it says | `PHASE-8-…md` §1, §3, §4 |
| **Execution** | PARTIALLY IMPLEMENTED | `IExecutionVenue` contract; credential isolation and plane separation asserted structurally | Bounded-execution rule not wired into the dispatch path | `PHASE-8-…md` §6, §8 |
| **Simulated execution** | GREEN / VERIFIED | `SimulatedVenue` under its own capability, through the full gate path | No slippage model | `PHASE-5-…md` §9 |
| **Paper trading** | NOT IMPLEMENTED | — | Continuous paper book, mark-to-market, running P&L | absence; §19 lists it as future |
| **Broker / live trading** | NOT IMPLEMENTED — **deliberately** | — | See section 8 | `PHASE-5-…md` §10; `PHASE-8-…md` §10 |
| **Monitoring** | PARTIALLY IMPLEMENTED | Audit trail, escalation queue, outbox, `/health`, structured logging, operations endpoint | **No notification transport** — no email, pager or chat | `PHASE-6-…md` §13.2 |
| **Dashboard / UI** | PARTIALLY IMPLEMENTED | Read models and eight REST controllers | **No frontend of any kind** — no UI project, no static assets | `src/AI.Investment.Api/Controllers/` |
| **Security** | PARTIALLY IMPLEMENTED | Secret scan clean; no credential in any interface; plane separation asserted | **No API authentication**; a committed database password remains reachable in git history and **must be rotated** | `SECURITY.md` §9 |
| **Auditability** | GREEN / VERIFIED | Append-only trail; guard forbids modification and deletion; the decision is recorded before the effect runs | Hash chaining described in §P is not implemented | `PHASE-1-…md` |
| **Human approval** | IMPLEMENTED | Approval tokens bound to an action fingerprint, single-use; promotion warrant; two-signature live-venue authorisation | No HTTP surface — deliberately, pending authentication | `PHASE-8-…md` §8 |
| **Multi-market / multi-asset** | PARTIALLY IMPLEMENTED | Generic taxonomy: `DataCategory`, `Region`, string-typed subjects, generic opportunity core with three per-type interfaces | Only U.S. equities implemented; no non-financial opportunity type | `PHASE-5-…md` §4 |

---

## 8. Deliberately absent

**The following are missing on purpose. They are decisions, not gaps, and they must not be read as
unfinished work to be tidied up by a future agent.**

| Absent | Enforced by |
|---|---|
| **Any broker or exchange SDK** | An architecture test fails the build if one is referenced anywhere in the solution |
| **Any live execution venue** | No implementation, no registration, no credential, no connection. Every `IExecutionVenue` in the solution reports itself simulated, asserted by reflection over the built assemblies |
| **Real-money execution** | `Capability.FinancialExecution` is refused structurally by the policy engine before any configuration is consulted, and no grant or warrant can be issued for it |
| **Automatic execution at L4** | The bounded-execution rule is enforced where authority is created and is deliberately not wired into dispatch, because no warranted grant could reach one |
| **HTTP endpoints for approving, promoting, authorising a venue, or engaging the kill switch** | Deliberate. These are decisions with a person's name on them, and an HTTP endpoint has no name attached until there is authentication — which there is not |
| **A notification transport** | Deliberate in Phase 6: inventing a notification plane in passing is how one ends up with an unconfigurable one. The outbox delivers into the durable, queryable record |
| **An analytical work plan in Phase 6** | Was deliberate at the time; closed on 2026-08-28 by the observation-window prerequisites. Recorded here because the Phase 6 document still describes the original absence |

A future agent that wants to remove one of these entries is making an architectural decision, not
performing maintenance, and it belongs in a phase document with a rationale.

---

## 9. Post-Phase-8 development blocks

**§P ends at Phase 8. Work after it is a named, unnumbered DEVELOPMENT BLOCK. There is no Phase 9,
and creating one would renumber nothing but would imply a roadmap that does not exist.**

Phase 8's own exit criterion — *a named, narrow capability runs at L4 for a defined period with zero
policy breaches* — cannot be attempted until measured evidence exists. As of 2026-08-28 producing
that evidence is a matter of configuration and elapsed time rather than architecture: the three
engineering prerequisites (market observations, opportunity discovery, per-capability breaker
signals) were closed and verified.

### Block 1 — "Operator surface and observation-window activation"

Identified 2026-08-28. **Implemented and verified 2026-08-29.** Full record:
[../Blocks/BLOCK-1-OPERATOR-SURFACE.md](../Blocks/BLOCK-1-OPERATOR-SURFACE.md).

| Contents, in the dependency order originally identified | Outcome |
| --- | --- |
| 1. Authentication and authorization on the API | **Done.** Keyed operator scheme, four privilege policies, fail-closed in five ways. Closes audit finding F-03. |
| 2. Human-in-the-loop endpoints | **Partly done.** Reject, acknowledge, resolve, engage the kill switch, create a watch — all through the existing action gateway. **Approve and disengage are not exposed**, for the reasons below. |
| 3. Watchlist and instrument administration | **Done** for scheduled watches. Source activation is now authenticated. |
| 4. A notification transport | **Not started.** Needs credentials this environment does not have. |
| 5. Position and instrument tracking on the ledger | **Not started.** A future development block. |

Added during the block and not originally listed: a minimum real operator console page, served from
the API, reading the existing read models and offering nothing a `curl` with the same key could not.

**Two exclusions are architectural, not omissions.** *Approve* would need an approval token to bind
to a persisted proposal, and proposals are not persisted (Phase 5 §13.1); exposing it would mean
loosening the binding that makes a token mean anything. *Disengage* cannot work through the seam,
because the policy engine denies every action while the switch is engaged — the only implementation
that would work is one that bypassed the gate. Disengaging stays out of band.

One safety rule was extended, none weakened: `Escalation` gained a four-column progress allow-list
in `GuardWrites()`, without which an escalation could never have been answered. Identity fields
remain unwritable and escalations remain undeletable, both proven against Postgres.

Verified: Release build clean under `TreatWarningsAsErrors`; 1824 tests across six assemblies, 0
failed, 0 skipped; no migration required.

**Autonomy is unchanged at L3 and was not touched.**

### Block 2 — "EODHD market-data provider"

Implemented and verified 2026-08-29. Full record:
[../Blocks/BLOCK-2-EODHD-MARKET-DATA.md](../Blocks/BLOCK-2-EODHD-MARKET-DATA.md).

The first real external market-data vendor, added through the existing data plane: one more
`IDataProvider` (`eodhd-eod`), one more `ISourceDefinition` registered inactive, one more
`INormalizer`, one registration. **No second pipeline.** EDGAR and the operator's own file export
are untouched and still registered; which source a run uses stays a matter of configuration and an
operator's activation.

Scope is deliberately one endpoint and one attribute: daily end-of-day prices, normalised to
`security.close`. Open, high, low and volume stay in the archived payload for a later block to read
without re-fetching. `adjusted_close` is not stored at all — it is retroactively rewritten by every
later split and dividend, which is what a bitemporal ledger exists to prevent.

**The substantive problem was the two timestamps EODHD does not send.** Its rows carry a trading
date and no times, while provenance needs a session close and a publication instant, the second of
which every point-in-time judgement is made from. Rather than infer them, the connector takes them
from an exchange session the operator states in configuration, writes the assumption onto every
observation as a caveat, and quarantines any payload whose exchange nobody stated.

The API key is a secret: absent from tracked configuration, redacted out of anything the connector
throws, and absent from the registry entry and every public member — each asserted.

Verified: Release build clean; 1912 tests across six assemblies, 0 failed, 0 skipped; secret scan 0
findings; no migration required. **No live EODHD call was made by anything, and none was faked.**

**Engineering ready. The observation window is still not active** — that needs a subscription, a
token, stated exchange sessions, source activation and elapsed time. **Autonomy unchanged at L3.**

### Block 3 — "Position and portfolio read model"

Implemented and verified 2026-08-29. Full record:
[../Blocks/BLOCK-3-POSITION-AND-PORTFOLIO.md](../Blocks/BLOCK-3-POSITION-AND-PORTFOLIO.md).

Closes the one gap that three separate capabilities were waiting on: there was no record of what is
held. **Portfolio** moves from NOT IMPLEMENTED to IMPLEMENTED, and **Risk**'s per-instrument
exposure map is no longer empty — `LimitEngine`'s concentration ceiling could not bind before this
block and now can.

A holding is not stored; it is replayed from append-only `PositionEvent` rows, exactly as a balance
is projected from ledger entries. Idempotency is a unique index on the venue's own fill reference,
so applying a fill twice is refused by the database rather than by a convention. The fill-to-position
write happens inside the same authorised window as the capital postings, in `OpportunityExecutor` —
**no second execution mechanism and no second ledger.**

P&L is average-cost, long-only, fees excluded to match the ledger's separate fee account. Cost basis
is authoritative and average cost is derived from it, so a fully closed position reports exactly
zero. Unrealised P&L uses the existing point-in-time price read and is **null when no price has been
observed** — no fallback to cost, and the portfolio total is null unless every open position was
valued.

One guard was added to `GuardWrites()` and none weakened: position events are append-only, and
unlike the seam's own bookkeeping they still require an authorised decision to be created.

Verified: Release build clean; 1992 tests across six assemblies, 0 failed, 0 skipped; secret scan 0
findings; one new table (`position_events`), no unrelated schema touched. **Autonomy unchanged at
L3.**

### Block 4 — "Final Investment Dashboard"

Implemented and verified 2026-08-29. Full record:
[../Blocks/BLOCK-4-FINAL-DASHBOARD.md](../Blocks/BLOCK-4-FINAL-DASHBOARD.md).

The first real analytics frontend. **Dashboard / UI** moves from PARTIALLY IMPLEMENTED to
IMPLEMENTED. Ten pages — overview, market data, opportunities and detail, portfolio and position
detail, capital, risk, validation, operations, safety — all reading the existing endpoints, with no
new backend capability and no write path of any kind.

**Blazor WebAssembly, chosen from evidence rather than preference:** a toolchain probe found no
Node and no npm on the build machine, so a JavaScript-framework application could not have been
built or tested here at all. Blazor compiles and tests through the same `dotnet` commands as
everything else, and bUnit renders the components so the sign-in flow, the localization and the
unknown-value handling are asserted rather than described.

English and Arabic are both first-class: the document direction changes, the layout mirrors through
CSS logical properties, dates and numbers reformat, and a test asserts the two resource sets have
identical keys so a missing translation fails the build. `InvariantGlobalization` is overridden for
the dashboard projects alone, because under it Arabic would silently format as English.

**The rule that shaped every page: zero is not unknown.** Missing values render as named states —
no observed price, closed, not measured — never as `0.00`, and the portfolio total is withheld with
an explanation when any open position lacks a price.

Served from `wwwroot/dashboard` on the same origin as the API, so no cross-origin policy is opened
and the operator key never crosses an origin boundary. The Operator Console is untouched at `/` and
still owns every safety-sensitive action; the dashboard has **no promote, live-execution, broker or
kill-switch disengage control anywhere**.

Verified: Release build clean; 2049 tests across seven assemblies, 0 failed, 0 skipped; secret scan
0 findings; no migration. **Autonomy unchanged at L3.**

Two read-model gaps were found and left to a future backend block rather than faked: opportunity
evidence is exposed as a count rather than citable observation identifiers, and the limit engine's
configured ceilings are not exposed by any endpoint.

### Still outstanding after Block 4

Running alongside as an **operational activity rather than engineering work**: point the
price-history connector at a licensed export, activate the source, register watches against the
`equity-price-review` template, configure a policy for `Capability.OpportunityManagement`, and let
the L3 window accumulate. Nothing in that list raises autonomy, and none of it can. **The licensed
export is an external blocker: no engineering remains, and no market data will be fabricated to
stand in for it.**

Candidate contents of a future block, in no fixed order and not yet scheduled: persisted proposals
and decisions (the prerequisite for an approve endpoint); position and instrument tracking on the
ledger (the prerequisite for concentration limits, unrealised P&L and paper trading); a notification
transport behind the existing outbox handler seam.

### Scheduled independently, as hazards rather than features

- **Rotate the exposed database credential.** Still reachable in git history from `a94b12c` and
  `8d0c8d0`. See `SECURITY.md` §9.
- **Apply outstanding migrations to the development database.** Proven against
  `ai_investment_tests` by the integration suite; a deployment step, not a gate.

---

## Related documents

- [../PHASE0_AUTONOMOUS_PLATFORM_ARCHITECTURE.md](../PHASE0_AUTONOMOUS_PLATFORM_ARCHITECTURE.md)
  — **§P is the canonical roadmap.** The authoritative source for section 2.
- [README.md](README.md) — per-phase status, the phase-document rules, and the original home of the
  reconciliation preserved in section 4.
- [VERIFICATION-LOG.md](VERIFICATION-LOG.md) — append-only record of every verification event.
- [../SYSTEM_ARCHITECTURE.md](../SYSTEM_ARCHITECTURE.md) — §15 the original six-phase strategy, §19
  the nine future capabilities.
- [../AUDIT_AND_TARGET_ARCHITECTURE.md](../AUDIT_AND_TARGET_ARCHITECTURE.md) — §10 the superseded
  seven-phase roadmap.
- [../SECURITY.md](../SECURITY.md) — secret handling, provider isolation, the outstanding rotation.
- `PHASE-0-…md` through `PHASE-8-…md` — what was actually built, phase by phase.
