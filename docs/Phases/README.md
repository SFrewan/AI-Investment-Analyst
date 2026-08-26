# Phase documentation

The permanent engineering record of this project. One document per approved phase, describing
**what actually exists in the repository** — not what was planned.

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
| 2 | Global data and intelligence foundation | Code complete — all ten stages implemented; verification pending | [PHASE-2-GLOBAL-DATA-AND-INTELLIGENCE-FOUNDATION.md](PHASE-2-GLOBAL-DATA-AND-INTELLIGENCE-FOUNDATION.md) |

**No phase is currently Verified.** The solution has never been compiled, tested or migrated
end to end. The reason is environmental and is recorded in each document's section 12 and in the
verification log: the assistant working on this repository has no .NET SDK, no NuGet access and
no shell on the developer machine, so `dotnet build`, `dotnet test` and
`dotnet ef database update` must be run locally.

**635 executable test cases now exist across the solution and none has ever run.** That number is
the size of the gap, not a claim about quality: an unexecuted test proves nothing except that
somebody thought about the case.

Everything that *could* be verified without a compiler has been, including live PostgreSQL 16
validation of all five Phase 2 tables, index-usability checks by query plan, service-graph review,
dependency-direction analysis and a repository-wide structural scan. See the verification log for
exactly what was executed and what it found — including four defects it caught before any compiler
would have.

## Related documents

- [../AUDIT_AND_TARGET_ARCHITECTURE.md](../AUDIT_AND_TARGET_ARCHITECTURE.md) — the Phase 0 audit,
  including the target architecture and the three factual corrections it made to the original brief.
- [../SYSTEM_ARCHITECTURE.md](../SYSTEM_ARCHITECTURE.md) — the architecture as it stands.
- [../SECURITY.md](../SECURITY.md) — secret handling, provider isolation, licensing posture.
- [../decisions/0001-phase0-phase1-approved-decisions.md](../decisions/0001-phase0-phase1-approved-decisions.md)
  — decisions D-1 to D-5 as approved.
