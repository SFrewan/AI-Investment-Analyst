# ADR-0001 — Approved architectural decisions for Phase 0 and Phase 1

- **Status:** Accepted
- **Date:** 2026-08-24
- **Deciders:** Project owner (approval), engineering (proposal)
- **Source:** `docs/PHASE0_AUTONOMOUS_PLATFORM_ARCHITECTURE.md` §Q, decisions D-1 … D-5

This record exists so that the reasoning behind five decisions is recoverable later, when the
code no longer makes it obvious why things are shaped this way.

---

## D-1 — Solution structure and naming

**Decision.** Projects use the dotted `AI.Investment.*` convention and live under `src/`, with
test projects under `tests/`. The restructure happens now.

**Why now.** Hyphenated project names (`AI-Investment-Domain`) force
`<RootNamespace>AI_Investment_Domain</RootNamespace>`, because hyphens are illegal in C#
identifiers — meaning an underscored namespace would appear in every `using` in the system
forever. At four source files the rename costs minutes; at three hundred it is a migration.

**Target layout.** `Domain`, `Application`, `Infrastructure`, `Agents`, `Api`, `Worker`,
`Execution` under `src/`; seven test projects under `tests/`; real `docs/`, `prompts/` and
`.github/workflows/` directories.

**Deliberate deviation.** `Agents`, `Worker` and `Execution` are **not created in Phase 0 or
Phase 1**. They belong to Phase 4, Phase 6 and Phase 8 respectively, and empty placeholder
projects would be speculative files that slow every build without carrying anything. The
layout reserves their place; the projects arrive with their phase. Likewise
`AI.Investment.Agents.Evaluation` is deferred until there is an agent to evaluate.

---

## D-2 — Persistence

**Decision.** PostgreSQL with EF Core.

**Why.** Native `JSONB` for raw provider payloads, agent outputs and per-type opportunity
detail (all schema-flexible by nature); strong time-series support; capable full-text search
for news; `pgvector` available later without changing engine; no licensing friction.

**Binding constraint on the first migration.** The schema must not need a rewrite to
accommodate: `AsOfUtc` / `PublishedAtUtc` / `IngestedAtUtc`, provenance, immutable claims,
audit records, opportunities, actions, policy decisions, approvals, executions, the capital
ledger, and autonomy records.

**The specific trap.** All historical queries — backtests, outcome measurement, shadow-mode
comparison — must filter on `PublishedAtUtc`, never `AsOfUtc`. Getting this wrong produces
**look-ahead bias**: a backtest of a January decision silently uses figures published in
March, and every strategy appears profitable. It cannot be retrofitted, because by then the
history has already been stored without the distinction.

---

## D-3 — Application organisation

**Decision.** Vertical slices by business capability, not a horizontal collection of generic
services.

**Why.** The pre-Phase-0 Application project declared *both* conventions in empty folders —
`Companies/` (feature) alongside `Services/`, `DTOs/`, `Mappings/`, `Validators/` (layer).
Both work; the mixture means checking two places for every change. In this system the natural
unit of change and of testing is the use case.

**Modules named in the approval:** ReferenceData, Ingestion, Fundamentals, News, Analysis,
Scoring, Opportunity, Risk, Policy, Action, Approval, Capital, Execution, Audit, Autonomy,
Evaluation, Learning. **Only those a phase actually needs are created.**

---

## D-4 — The Action / Policy safety seam

**Decision.** Mandatory Phase 1 architectural boundary. Every side effect passes through it.

```
ActionProposal → PolicyEngine → Execute | RequireApproval | Deny
                                    → ActionExecutor → ActionExecution → AuditRecord
```

**The `PolicyEngine` is:** deterministic, pure where practical, **fail-closed**, **total**
(never returns "unknown"), independently testable, and independent of AI reasoning.

**An AI agent may never:** execute an action directly, modify a policy, modify an autonomy
grant, bypass approval, increase its own permissions, or disable a safety control.
Agent output is data. It is never execution authority.

**Why in Phase 1, before anything dangerous exists.** Every safety requirement in the brief —
approvals, capital limits, kill switch, escalation, autonomy levels — is a statement *about
actions*. Without a single action abstraction, each control must be implemented at every call
site that does something, which is how safety controls get bypassed. A seam introduced when
the first risky feature arrives is a seam introduced under schedule pressure and retrofitted
to call sites that already exist.

**Consequence accepted.** Phase 1 routes a completely harmless use case (creating a company
record) through the gate. That is the point: it proves the seam is real rather than
theoretical, at a moment when getting it wrong costs nothing.

---

## D-5 — First candidate for bounded autonomy

**Decision.** The first capability eventually permitted to run unattended is **not** real-money
trading. It is a low-risk, reversible operation — refreshing data, updating a watchlist,
initiating a research cycle, or re-evaluating an existing opportunity.

**Architectural consequence.** The system must be built so that such a capability could
operate unattended under a deterministic `AutonomyGrant`, resolved per
`(Capability, ActionType, RiskTier, ExposureBand, Environment)`.

**Out of scope for Phase 0 and Phase 1:** real financial execution, broker integrations, the
full autonomy engine.

---

## Consequences

- Two additional risk dimensions are first-class from Phase 1: `ReversibilityClass` and
  `RiskTier`. **Reversibility, not amount, is the primary axis** — a small irreversible action
  deserves more scrutiny than a large reversible one.
- Risk tier is **computed** from capability, reversibility and exposure. A proposer — human,
  service or model — cannot assert the risk tier of its own proposal.
- The audit trail is a by-product of the seam rather than a parallel obligation: there is one
  path, so every action has a record by construction.
