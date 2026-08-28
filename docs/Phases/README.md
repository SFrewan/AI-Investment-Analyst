# Phase documentation

The permanent engineering record of this project. One document per approved phase, describing
**what actually exists in the repository** — not what was planned.

## The canonical roadmap

There is **one** roadmap for this project: section **P. Development Roadmap** of
[../PHASE0_AUTONOMOUS_PLATFORM_ARCHITECTURE.md](../PHASE0_AUTONOMOUS_PLATFORM_ARCHITECTURE.md).
It defines phases **0 to 8**, each with an exit criterion and an autonomy level, and the phase
documents in this folder are numbered against it. Phases 0, 1 and 2 were implemented and
documented under that numbering, so it is also the numbering the repository's history is written
in.

**Recorded 2026-08-27.** A later restatement of the programme described a 28-item sequence
(Foundation, Knowledge & Analytics, Ingestion, Knowledge Graph, Financial Analytics, … through
Autonomous 24/7 System). That list is a finer decomposition of the same programme, not a competing
plan, and adopting its numbering would have renumbered three already-documented phases and moved
completed work under new headings for no engineering reason. The canonical numbering therefore
stays as it is, and the finer list is recorded here as a mapping onto it:

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

Nothing already implemented was renumbered, renamed or moved as a result of this reconciliation.

## Rules governing these documents

1. A phase is documented as **Verified** only after its build, test, runtime, integration,
   database and safety gates have actually passed. Until then its status is *Implemented —
   verification pending*, and section 12 states precisely which gates have run and which have not.
2. History is preserved. Corrections are appended with a date and a reason; earlier decisions are
   never quietly deleted. If a decision is reversed, both the original and the reversal stay in
   the record.
3. Verification events are appended to [VERIFICATION-LOG.md](VERIFICATION-LOG.md), which is
   append-only.
4. These documents are the context for later phases. Read them instead of re-deriving the
   architecture from source.

Every phase document carries the same 18 sections, in the same order, so that a reader who knows
one document knows all of them.

## Status

| Phase | Title | Status | Document |
|---|---|---|---|
| 0 | Foundation, governance and configuration | Implemented — verification pending | [PHASE-0-FOUNDATION.md](PHASE-0-FOUNDATION.md) |
| 1 | Domain core, epistemic model and the Action/Policy safety seam | Implemented — verification pending | [PHASE-1-DOMAIN-CORE-AND-SAFETY-SEAM.md](PHASE-1-DOMAIN-CORE-AND-SAFETY-SEAM.md) |
| 2 | Global data and intelligence foundation | Build and tests green (647/647 on 2026-08-27 after the integration-test repair); **not Verified** — see section 12 | [PHASE-2-GLOBAL-DATA-AND-INTELLIGENCE-FOUNDATION.md](PHASE-2-GLOBAL-DATA-AND-INTELLIGENCE-FOUNDATION.md) |
| 3 | Deterministic analytics | **Verified** 2026-08-27 — Release build clean; 808/808 passed, 0 failed, 0 skipped | [PHASE-3-DETERMINISTIC-ANALYTICS.md](PHASE-3-DETERMINISTIC-ANALYTICS.md) |
| 4 | AI layer | **Verified** 2026-08-27 — Release build clean; 1017/1017 passed, 0 failed, 0 skipped | [PHASE-4-AI-LAYER.md](PHASE-4-AI-LAYER.md) |
| 5 | Opportunity, approval, capital | **Verified** 2026-08-28 — Release build clean; 1284/1284 passed, 0 failed, 0 skipped; mutation score 96.73 % over the safety-critical domain, above the 70 % break threshold | [PHASE-5-OPPORTUNITY-APPROVAL-CAPITAL.md](PHASE-5-OPPORTUNITY-APPROVAL-CAPITAL.md) |
| 6 | Continuous operation | **Verified** 2026-08-28 — Release build clean; 1500/1500 passed, 0 failed, 0 skipped; autonomy-escape suite green; the two-week operational invariants demonstrated deterministically over accelerated time, one assertion each, with negative twins | [PHASE-6-CONTINUOUS-OPERATION.md](PHASE-6-CONTINUOUS-OPERATION.md) |

**Phases 3, 4 and 5 are Verified.** On 2026-08-27 the Release build ran clean across all ten
projects (0 warnings, 0 errors) and the whole suite passed on the developer machine — first at
**808** with Phase 3, then at **1017 total, 1017 passed, 0 failed, 0 skipped** with Phase 4's AI
layer added. On 2026-08-28 Phase 5 took it to **1284 total, 1284 passed, 0 failed, 0 skipped**,
with **zero skipped** because a real PostgreSQL was reachable and every integration test executed.
Phase 5 also carries a mutation-testing gate over the files that decide whether something is
allowed to happen: **96.73 %**, against a break threshold of 70 %. Phase 6 extended that gate to the
autonomy resolver, the cycle state machine, the budgets, admission control, the escalation policy,
watches and the shadow evaluator, and took the suite to **1500 total, 1500 passed, 0 failed,
0 skipped** on 2026-08-28.

**Phase 6's two-week criterion was closed by demonstration rather than by waiting.** Two weeks of
wall-clock time is not what the criterion tests; the invariants that must hold over two weeks are,
and each is now asserted separately over a deterministic accelerated fortnight that contains a
redelivering feed, a market-wide burst, overrunning budgets, two watches reaching the same action,
workers that die mid-cycle, an outbox outage, and an autonomy grant that expires and is not renewed.
Both harnesses have negative twins that fail. Running one instance for a real fortnight remains
worth doing - a simulation cannot produce a provider that degrades at four in the morning - but it
is an operational activity rather than an outstanding engineering gate, and §12.2 of the phase
document says exactly that.
All three runs are recorded in [VERIFICATION-LOG.md](VERIFICATION-LOG.md), including the defects
the gates caught and their fixes.

**A live database password was committed and pushed before Phase 5, and has been removed from the
tracked files.** `scripts/run-secret-scan.cmd` confirms by execution that no credential remains in
any of the 355 tracked files. It is still in git history, reachable from commits `a94b12c` and
`8d0c8d0`, and **must be rotated**; the full record and the remaining exposure are in
[../SECURITY.md](../SECURITY.md) §9.

Phases 0, 1 and 2 remain short of Verified, and the reason is unchanged: their own section 12
documents list gates — runtime startup, CI execution — that have not run. Phase 2's code is green
inside that same 808 and has been since the integration-test repair; what it lacks is the
non-test evidence its document asks for, not passing tests.

`scripts/verify.ps1` runs the Release build and the full suite against the dedicated
`ai_investment_tests` database and writes machine-readable results to `artifacts/verify`;
`scripts/run-verify.cmd` starts it with a double-click, which is what lets the assistant run the
gates itself under computer use rather than asking anyone to transcribe console output.
`scripts/run-mutation.cmd` runs the mutation-testing gate over the safety-critical domain, and
`scripts/run-secret-scan.cmd` checks every tracked file for credential-shaped patterns.

**The test database connection string is no longer in `verify.ps1`.** It comes from
`AIINV_TEST_POSTGRES`, or from the git-ignored `scripts/verify.local.ps1` — copy
`scripts/verify.local.example.ps1` to create it. Without either, the integration tests skip and
say so rather than being counted as passed.

## Related documents

- [../PHASE0_AUTONOMOUS_PLATFORM_ARCHITECTURE.md](../PHASE0_AUTONOMOUS_PLATFORM_ARCHITECTURE.md)
  — the audit, the target architecture, and **§P, the canonical roadmap**.
- [../AUDIT_AND_TARGET_ARCHITECTURE.md](../AUDIT_AND_TARGET_ARCHITECTURE.md) — the Phase 0 audit,
  including the three factual corrections it made to the original brief.
- [../SYSTEM_ARCHITECTURE.md](../SYSTEM_ARCHITECTURE.md) — the architecture as it stands.
- [../SECURITY.md](../SECURITY.md) — secret handling, provider isolation, licensing posture.
- [../decisions/0001-phase0-phase1-approved-decisions.md](../decisions/0001-phase0-phase1-approved-decisions.md)
  — decisions D-1 to D-5 as approved.
