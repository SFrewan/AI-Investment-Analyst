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
| 3 | Deterministic analytics | Implemented — **not verified**; never compiled | [PHASE-3-DETERMINISTIC-ANALYTICS.md](PHASE-3-DETERMINISTIC-ANALYTICS.md) |

**No phase is currently Verified.** Phase 2 reached a green build and a fully green suite on the
developer machine (647 passed, 0 failed, 0 skipped) after the integration-test infrastructure was
repaired; it is still short of Verified because its own section 12 lists gates that have not run.

Phase 3 has **never been compiled**. Everything in it was written, statically checked and
reviewed, but no build or test has executed against it. The reason is environmental and is
recorded in the verification log: the assistant working on this repository has no .NET SDK, and
the container's egress proxy blocks `api.nuget.org`, `packages.microsoft.com`,
`builds.dotnet.microsoft.com` and the Ubuntu archives, so no toolchain can be installed and no
package can be restored. `scripts/verify.ps1` exists so that a single run on the developer
machine produces machine-readable results the assistant can read back and act on without anyone
transcribing console output.

## Related documents

- [../PHASE0_AUTONOMOUS_PLATFORM_ARCHITECTURE.md](../PHASE0_AUTONOMOUS_PLATFORM_ARCHITECTURE.md)
  — the audit, the target architecture, and **§P, the canonical roadmap**.
- [../AUDIT_AND_TARGET_ARCHITECTURE.md](../AUDIT_AND_TARGET_ARCHITECTURE.md) — the Phase 0 audit,
  including the three factual corrections it made to the original brief.
- [../SYSTEM_ARCHITECTURE.md](../SYSTEM_ARCHITECTURE.md) — the architecture as it stands.
- [../SECURITY.md](../SECURITY.md) — secret handling, provider isolation, licensing posture.
- [../decisions/0001-phase0-phase1-approved-decisions.md](../decisions/0001-phase0-phase1-approved-decisions.md)
  — decisions D-1 to D-5 as approved.
