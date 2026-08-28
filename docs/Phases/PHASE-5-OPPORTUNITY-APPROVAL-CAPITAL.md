# Phase 5 — Opportunity, approval, capital

**Status:** **Verified.** Release build clean; full suite 1217 passed, 0 failed, 0 skipped (2026-08-28).
**Roadmap:** §P of [../PHASE0_AUTONOMOUS_PLATFORM_ARCHITECTURE.md](../PHASE0_AUTONOMOUS_PLATFORM_ARCHITECTURE.md), phase 5
**Last updated:** 2026-08-28

---

## 1. Phase objective

The full decision path, simulated: a candidate becomes an evaluated opportunity, is ranked against
others, produces a proposal, is approved by a person, executes at a venue where nothing real moves,
and lands in a set of books that balance — with every step policy-gated, limit-checked and audited.

§P's exit criterion is the shape of the whole phase: *a complete path replays end to end; the safety
suite is green including mutation testing.* The second half is why this phase took the time it did.
Unit tests show that the guard rails work on the inputs someone thought of. Mutation testing shows
they cannot be removed without a test going red, which is the claim a safety control actually has to
make.

## 2. Scope

In scope: the generic `Opportunity` aggregate and its lifecycle; the first concrete type; the
approval request, token and workflow; the limit engine; the capital ledger; the kill switch read at
execution time; the simulated execution venue; the executor that sequences all of it; persistence
and a migration for the four new tables; the dashboard and reconciliation read models; and the
tests, including mutation testing of the safety-critical domain.

Explicitly out of scope: **any real venue, any broker credential, any real-money capability.**
`Capability.FinancialExecution` remains refused unconditionally by a structural rule, and the new
`Capability.SimulatedExecution` exists precisely so that the whole path can be exercised without
touching it. Also out of scope: the operating cycle, watches and autonomy grants (phase 6);
backtesting (phase 7); and HTTP endpoints for approving or executing — see section 13.

## 3. What was implemented

**The opportunity core** (`Domain/Opportunities/`) — `OpportunityId`, `OpportunityType`,
`OpportunitySource`, `OpportunityStatus`, `OpportunityDetail`, `OpportunityEconomics`,
`OpportunityRisk`, `OpportunityScore`, the `Opportunity` aggregate, and the three interfaces a type
plugs into: `IOpportunityDiscoverer`, `IOpportunityEconomicsCalculator`, `IEvidenceRequirement`.

**The first concrete type** (`Domain/Opportunities/Equity/`) — `EquityOpportunity`, `EquityDetail`,
`EquityEconomicsCalculator`, `EquityEvidenceRequirement`. Two registrations and nothing else, which
is the claim §J makes about the generic core and the thing this phase had to demonstrate rather than
assert.

**Approvals** (`Domain/Approvals/`, `Application/Approvals/`) — `ActionFingerprint`,
`ApprovalRefusal`, `ApprovalToken`, `ApprovalRequest`, `ApprovalActionProposal`, `ApprovalOutcome`
and `ApprovalWorkflow`.

**Limits** (`Domain/Limits/`) — `LimitKind`, `Limit`, `LimitSet`, `LimitBreach`, `LimitVerdict`,
`ExposureSnapshot`, `LimitEngine`.

**Capital** (`Domain/Capital/`) — `LedgerAccountKind`, `LedgerAccount`, `LedgerEntry`,
`CapitalLedger`.

**Execution** (`Application/Execution/`) — `OrderSide`, `VenueOrder`, `VenueFill`, `VenueResult`,
`IExecutionVenue`, `ExecutionRequest`, `ExecutionStatus`, `ExecutionOutcome`, `OrderParameters`,
`SimulatedExecutionProposal`, `OpportunityExecutor`.

**Read models** (`Application/Opportunities/OpportunityDto.cs`, `Application/Capital/LedgerReport.cs`)
and their HTTP surface (`Api/Controllers/OpportunitiesController.cs`, `CapitalController.cs`).

**Infrastructure** — `SimulatedVenue`, `DatabaseAndEnvironmentKillSwitch`, `ConfiguredLimitProvider`,
`LedgerExposureProvider`, `KillSwitchFlag`, `OpportunityJson`, four entity configurations, four
repositories, and the `SimulatedVenueOptions` / `LimitOptions` configuration shapes.

**Two new capabilities** — `Capability.SimulatedExecution` and the pre-existing
`Capability.ApprovalAdministration` are now both used, and `RiskTierCalculator` gained the base tier
for the former.

## 4. Architecture changes

None to the layering. Three changes to how existing seams are used, each recorded because each was
a defect found by a test rather than a design preference:

1. **Issuing and revoking an approval now go through the action gateway.** They are side effects,
   §H.3 says every side effect passes through the seam without exception, and until this phase the
   token was written beside the seam rather than through it — which the persistence guard correctly
   refuses. See section 14, D-3.
2. **The executor persists the opportunity's transition under the decision that authorised the
   action.** The gateway's authorisation window closes when the effect returns, and the aggregate's
   move to `Active` happens after the outcome is known. See section 14, D-4.
3. **`Capability.SimulatedExecution` is separate from `FinancialExecution`.** Folding them together
   would force a choice between never exercising the execution path and enabling the one capability
   that must stay refused.

## 5. Important projects/files

| Area | Files |
|---|---|
| Opportunity core | `Domain/Opportunities/*.cs` (12) |
| Equity type | `Domain/Opportunities/Equity/*.cs` (3) |
| Approvals | `Domain/Approvals/*.cs` (3), `Application/Approvals/*.cs` (3) |
| Limits | `Domain/Limits/*.cs` (7) |
| Capital | `Domain/Capital/*.cs` (4) |
| Execution | `Application/Execution/*.cs` (11) |
| Read models | `Application/Opportunities/OpportunityDto.cs`, `Application/Capital/LedgerReport.cs` |
| API | `Api/Controllers/OpportunitiesController.cs`, `CapitalController.cs` |
| Infrastructure | `Infrastructure/Execution/SimulatedVenue.cs`, `Infrastructure/Policy/*.cs` (3), `Persistence/Configurations/*.cs` (4), `Persistence/Repositories/*.cs` (4), `Persistence/{KillSwitchFlag,OpportunityJson}.cs` |
| Migration | `Persistence/Migrations/20260828025509_Phase5OpportunityApprovalCapital.cs` |
| Tests | 14 new files across five test projects |
| Tooling | `scripts/mutation.ps1`, `scripts/run-mutation.cmd`, `scripts/stryker-config.json`, `scripts/verify.local.example.ps1` |

## 6. Domain / Application / Infrastructure changes

**Domain** holds everything that decides: the lifecycle, the economics arithmetic, the limit engine,
the ledger and the approval token. All of it is pure — an architecture test asserts that the limit
engine and the ledger reference no network, no filesystem and no logging, because a control that
needs something arranged before it can be tested is a control that will be tested approximately.

**Application** holds the orchestration: the three workflows, the proposal factories, the read
models, and the ports (`IExecutionVenue`, `IKillSwitch`, `ILimitProvider`, `IExposureProvider`,
`IApprovalTokenStore`, `ILedgerStore`, `IOpportunityRepository`).

**Infrastructure** holds the four things that touch the outside world: a venue that fills on paper,
a kill switch that reads a database and an environment variable, a limit provider that reads
configuration, and an exposure provider that projects the ledger.

## 7. Database changes

Migration `20260828025509_Phase5OpportunityApprovalCapital` creates four tables.

| Table | Purpose | Notes |
|---|---|---|
| `opportunities` | The aggregate | `detail`, `economics`, `risk`, `evidence` and `proposal_ids` are `jsonb`; `ix_opportunities_status` and `ix_opportunities_subject` |
| `approval_tokens` | Human permissions | `action_fingerprint` is the 64-character digest of the exact action; `ix_approval_tokens_opportunity` covers the unconsumed lookup |
| `ledger_entries` | Double-entry postings | Debit and credit are owned accounts with a name and a kind; `amount` is `numeric(18,4)` with its currency |
| `kill_switch` | The database half of the switch | `ux_kill_switch_capability` is unique, so one row governs a capability |

`PostgresFixture.TruncateStatement` names all four, which `DatabaseResetCoverageTests` checks
against the EF model — a table missing from that statement leaks rows between tests, and the check
exists so nobody has to remember.

**There is no settable balance column anywhere.** A balance is computed from immutable entries, and
an architecture test asserts that `LedgerEntry` exposes no public setter.

## 8. APIs / contracts

| Endpoint | Behaviour |
|---|---|
| `GET /api/opportunities?status=&limit=` | The pipeline, and with `status=Proposed` the escalation queue. `400` on an unknown status or an out-of-range limit |
| `GET /api/opportunities/{id:guid}` | One opportunity with economics, risk, confidence and score. `404` when unknown |
| `GET /api/capital/ledger?currency=` | Balances, entry count, and whether the books balance |
| `GET /api/capital/ledger/{opportunityId:guid}` | The postings behind one opportunity — the reconciliation view |

New application contracts: `IExecutionVenue`, `ILedgerReport`, `IOpportunityRepository`,
`IApprovalTokenStore`, `ILimitProvider`, `IExposureProvider`, `IKillSwitch`.

**Approving and executing have no HTTP surface, deliberately** — see section 13.

## 9. Security and safety changes

The claim this phase has to make good on is that a complete execution path exists and that nothing
on it can be bypassed. Five gates, in order, each with tests that assert both the refusal and that
nothing after it ran:

1. **Kill switch**, re-read by the executor rather than trusted from the caller. Engaged stops
   everything; **unknown stops everything in exactly the same way**, and any failure to read returns
   unknown.
2. **Limits**, evaluated against the ledger's current exposure before anything is consumed or
   dispatched. A set that could not be read refuses everything rather than permitting everything —
   the two differ by one word in the code and by the entire safety posture of the system.
3. **Policy**, through the action gateway, using the real `PolicyEngine`. An undefined capability
   denies; `FinancialExecution` denies structurally; an AI proposer may never administer approvals,
   policy or autonomy, and the safety suite asserts that under the most permissive configuration
   anyone could write.
4. **Approval**, consumed inside the effect so a denied action leaves the token unused, and consumed
   atomically by a conditional update so two concurrent callers cannot both spend it. An integration
   test races two contexts against one token and asserts exactly one wins.
5. **Venue and ledger**, in the same step, so a fill cannot exist without entries describing it.

Additionally: a consumed approval is spent even when the venue refuses, because the action was
attempted and the conservative reading of "we tried and it did not work" is that a person decides
again; a replayed order is suppressed by the idempotency key rather than filled twice; and the only
registered `IExecutionVenue` reports itself simulated, asserted by reflection over the built
assemblies rather than by a comment.

## 10. Dependencies

**No new runtime packages.** One new development-time tool, `dotnet-stryker` 4.4.1, installed into
the local tool manifest by `scripts/mutation.ps1` and pinned there rather than depending on whatever
is installed globally. The architecture test forbidding an AI SDK is unchanged and still passes, and
a new rule forbids any broker or exchange SDK in any assembly.

## 11. Tests

**267 new test cases**, taking the suite from 1017 to 1284.

| Project | Before | After | What was added |
|---|---:|---:|---|
| `AI.Investment.Domain.UnitTests` | 595 | 700 | Lifecycle and its refusals; economics arithmetic; the equity type; limit definitions and sets; exposure snapshots; the ledger, its guards, its sign convention and the wording of its refusals |
| `AI.Investment.Application.UnitTests` | 213 | 229 | The opportunity workflow including unregistered types; the approval workflow |
| `AI.Investment.Safety.Tests` | 65 | 194 | The limit engine kind by kind; the approval token, its boundaries and every refusal it can give; the executor's five gates; the kill-switch drill; the simulated venue; the approval seam; the policy engine's reason and its evaluated-rule trail |
| `AI.Investment.Integration.Tests` | 101 | 107 | The four new tables round-tripped through the real migration, and the concurrent-consume race |
| `AI.Investment.Architecture.Tests` | 22 | 33 | Every venue is simulated; no broker SDK; the AI layer cannot reach the decision path; the ledger has no setter |
| `AI.Investment.Api.Tests` | 21 | 21 | — |

No mocking framework. Doubles are hand-written, and the safety suite wires the **real** action
gateway and the **real** policy engine — a safety test that stubs the gate it is testing proves the
stub works.

## 12. Verification results

**Verified on the developer machine, 2026-08-28.** `scripts/verify.ps1`, launched by double-click,
ran the Release build and the whole suite against the dedicated `ai_investment_tests` database.

| Gate | Result |
|---|---|
| `dotnet build` (Release, whole solution) | **Succeeded** — 0 warnings, 0 errors |
| `dotnet test` (Release, whole solution) | **Passed** — `build_exit=0 test_exit=0` |
| Suite total | **1284 total, 1284 passed, 0 failed, 0 skipped** |
| Migration validity | **Passed** — `MigrateAsync` applied all three migrations to `ai_investment_tests`; the four Phase 5 tables were created and round-tripped |
| Database behaviour | **Passed** — jsonb economics, risk, evidence and proposal ids; owned subject, source, detail, score, money and account types; the not-null constraints held |
| Concurrency / idempotency | **Passed** — one of two concurrent consumers wins the token; a replayed order is suppressed and fills once |
| Kill switch | **Passed** — engaged, unknown, unreadable database, unparseable variable |
| Limit enforcement | **Passed** — every kind, in both directions, plus the fail-closed set |
| Approval tokens | **Passed** — single use, expiry, fingerprint binding, amount cap, wrong opportunity, wrong proposal, revocation |
| Ledger invariants | **Passed** — balanced books for purchase, gain and loss; no negative or zero entry; no cross-currency total |
| Simulated execution boundary | **Passed** — the only venue reports itself simulated; no broker SDK is referenced |
| Architecture rules | **Passed** — 33 |
| Safety suite | **Passed** — 194 |
| Mutation testing | **Passed** — 96.73 %, above the 70 % break threshold. See §12.1 |
| Secret scan | **Passed** — 0 findings across 355 tracked files. See §9 and `docs/SECURITY.md` §9 |

Per assembly:

| Assembly | Total | Passed | Failed | Skipped |
|---|---:|---:|---:|---:|
| AI.Investment.Domain.UnitTests | 700 | 700 | 0 | 0 |
| AI.Investment.Application.UnitTests | 229 | 229 | 0 | 0 |
| AI.Investment.Safety.Tests | 194 | 194 | 0 | 0 |
| AI.Investment.Integration.Tests | 107 | 107 | 0 | 0 |
| AI.Investment.Architecture.Tests | 33 | 33 | 0 | 0 |
| AI.Investment.Api.Tests | 21 | 21 | 0 | 0 |
| **Total** | **1284** | **1284** | **0** | **0** |

**Zero skipped is worth stating plainly.** Earlier runs of this repository counted skipped
database tests as passed; this run had a reachable PostgreSQL and every integration test executed.

**What the gates caught.** Eight defects, six of them in code written before this phase's tests
existed. They are listed in section 17 and in the verification log, because the record of what a
gate found is the evidence that the gate works.

### 12.1 The mutation gate

`scripts/mutation.ps1` runs Stryker.NET over the eight files that decide whether something is
allowed to happen — `PolicyEngine`, `RiskTierCalculator`, `LimitEngine`, `LimitSet`, `ApprovalToken`,
`ActionFingerprint`, `CapitalLedger` and `LedgerEntry` — driven by the safety and domain suites, with
a **break threshold of 70 %**.

| Run | Killed | Survived | No coverage | Score | Outcome |
|---|---:|---:|---:|---:|---|
| First | 176 | 78 | 21 | 64.00 % | **Below threshold — the gate failed** |
| After 67 additional tests | 266 | 7 | 2 | **96.73 %** | **Passed** |

The threshold was not lowered and no existing assertion was touched. The seventy-eight survivors were
almost all the same defect in the tests rather than in the code: **every refusal message could be
replaced with an empty string and the suite stayed green**, because the tests asserted the outcome
and never the reason. For a component whose entire product is a defensible "no", that is a real hole
— a decision with a blank reason denies exactly as correctly and is worthless to the person who has
to work out afterwards whether the control fired or the system broke. The rest were argument guards
nothing passed `null` to, exact length boundaries nothing sat on, and the credit side of the ledger's
sign convention, which no existing entry exercised because every one of them debited an asset.

**The nine that remain, each analysed rather than suppressed.** None is killable by a test that
asserts something true.

| Location | Mutation | Why it survives |
|---|---|---|
| `PolicyEngine.cs:76` | `\|\|` → `&&` on `!TryGetPolicy(...) \|\| policy is null` | Equivalent. `TryGetPolicy` returns `false` exactly when it sets `policy` to `null`, so both operands always agree. The second clause is defence against a future implementation that breaks that contract, and defence that is not yet needed cannot be observed. |
| `RiskTierCalculator.cs:108` and `:109` | negate the condition; remove the block | Equivalent. The guarded branch returns `RiskTier.Low` and so does the fall-through, so the branch is dead. It is left in place because it is the seam for the currency-aware exposure bands §14 defers, and deleting it would delete the note. |
| `RiskTierCalculator.cs:116` | `>=` → `>` in `left >= right ? left : right` | Equivalent. The two differ only when the operands are equal, and then both return the same value. |
| `ApprovalToken.cs:263`, `LedgerEntry.cs:123` | `<=` → `<` in `length <= Max ? s : s[..Max]` | Equivalent. They differ only at exactly `Max`, where `s[..Max]` is `s`. Both boundaries are covered by a test; the mutant simply cannot be observed. |
| `ApprovalToken.cs:280` | the `_ =>` arm of `Describe` | Unreachable. `Describe` is called only when `Check` returned a refusal, and every value `Check` can return has its own arm. The arm exists so that adding a member to `ApprovalRefusal` produces a sentence rather than an exception. |
| `CapitalLedger.cs:53`, `LimitSet.cs:68` | remove `ArgumentNullException.ThrowIfNull` | Equivalent. The next statement is `entries.ToList()` / `limits.ToList()`, and LINQ throws its own `ArgumentNullException` for a null source. The explicit guard names the right parameter and does not depend on the next line staying a LINQ call, so it stays. |

Recorded rather than removed: a mutation score is only evidence if the mutants it does not kill have
been looked at.

## 13. Known limitations

1. **Approving and executing have no HTTP endpoint.** An approval token is bound to the identity of
   the exact `ActionProposal` a person was shown; proposals are not persisted, so a second request
   rebuilding "the same" proposal produces a different identity. An endpoint pair would therefore
   either refuse every token or would have to loosen the binding that makes a token mean anything.
   Persisting proposals and decisions is the prerequisite and belongs with the phase that needs it.
   The whole path is exercised end to end by the tests.
2. **No opportunity discoverer exists.** `IOpportunityDiscoverer` is defined and unimplemented:
   deciding *which* candidates to raise needs the watchlist that phase 2 deliberately stopped short
   of, and inventing one here would put a real architectural piece in the wrong place.
3. **The equity economics ignore commission.** What a fill costs is decided by the venue and posted
   from the fill; a second guessed fee in the estimate would produce two numbers for one thing.
4. **Exposure is not per instrument yet.** `LedgerExposureProvider` reports total exposure and
   per-capability action counts but leaves the per-instrument map empty, so the concentration limit
   currently measures the proposed position against equity with no existing position added. The
   ledger does not record which instrument a position is in — only the opportunity — and fixing that
   is a schema change that belongs with position tracking.
5. **The simulated venue does not model slippage.** It fills at the stated price on purpose;
   slippage is a backtesting concern and belongs where the model can be stated and varied.
6. **The kill switch has no administration surface.** It is set by an environment variable or by a
   row in `kill_switch`. An endpoint to engage it is a phase 6 concern, and it must be reachable
   when the API is not.
7. **Opportunity expiry has no scheduled caller.** `ExpireOverdueAsync` exists and is tested;
   nothing runs it on a cadence, which is the operating cycle's job.
8. **`Money.ZeroUsd` is a cached instance.** It is not currently referenced by any persisted entity,
   so the owned-instance defect described in section 17 cannot occur through it — but it is the same
   shape and is recorded here so the next persisted type that uses it is checked.
9. **The development database has not had the migration applied.** The migration is proven against
   `ai_investment_tests` by the integration suite; applying it to `ai_investment` is a deployment
   step, not a gate.

## 14. Architectural decisions

**D-1 — Simulated execution is its own capability.** Folding it into `FinancialExecution` would
force a choice between never exercising the execution path and enabling the one capability that must
stay refused. It is still a full capability rather than an exemption: proposals using it are policy
evaluated, limit checked, approval gated and audited exactly like any other, because the whole value
of simulating on the production path is lost the moment the simulation gets a shortcut.

**D-2 — Concentration is measured against equity, not against total exposure.** Against exposure the
first position in a flat book is always a hundred per cent of it, so any concentration ceiling below
one would refuse every opening trade forever. A limit that cannot be satisfied is an off switch
wearing a ceiling's name, and the failure would look exactly like a correctly working control.

**D-3 — Issuing an approval goes through the action gateway.** The rule is "every side effect,
without exception", and an approval is the most consequential non-financial write in the platform.
Three things follow and none are incidental: the write happens inside an authorisation window, so
the persistence guard permits it; the act of approving is audited with the same record shape as
everything else; and the structural rule refusing `ApprovalAdministration` to an AI proposer now
stands between a model and its own approval on a path that actually exists.

**D-4 — The executor persists the opportunity's transition under the decision that authorised the
action.** The gateway's window closes when the effect returns, and the move to `Active` depends on
the execution identity the outcome carries. The alternative — changing the gateway's effect
signature to pass the execution in — would touch every existing caller of a Phase 1 contract. The
window is opened with the decision reached for *that* proposal, so the guard still refuses a write
no decision authorises.

**D-5 — The approval ceiling may not exceed the exposure that was presented.** A ceiling above the
figure on the screen authorises something nobody read.

**D-6 — A consumed approval is spent even when the venue refuses.** The action was attempted. The
conservative reading of "we tried and it did not work" is that a person decides again, not that the
system retries on an old permission.

**D-7 — The well-known ledger accounts are fresh instances, not cached singletons.** They are mapped
as owned entities, and a shared instance referenced by two entries in one save is one object with
two owners. Record value equality means nothing else changes. See section 17.

**D-8 — `IsBalanced` implements the accounting identity rather than summing every balance.** Debit-
natured balances are added and credit-natured ones subtracted. Summing them all with one sign
happens to come to zero for a purchase and its fee, and comes to twice the gain the moment a
disposal credits income.

**D-9 — Mutation testing is scoped to the safety-critical domain.** The policy engine, the risk
tiering, the limit engine and set, the approval token and its fingerprint, and the ledger. Mutating
the whole domain would report a score dominated by value objects whose behaviour nobody's money
depends on, and would take long enough that nobody would run it.

## 15. Deviations from the approved plan

**No escalation UI was built.** §P names "dashboard read models · escalation UI". The read models
exist and `GET /api/opportunities?status=Proposed` is the escalation queue; there is no user
interface in this repository and no front-end project, so the queue is the contract a UI would read.
Recorded as a deliberate deviation.

**The economics interface is `IOpportunityEconomicsCalculator`, not `IEconomicsCalculator`.**
Consistent with Phase 3's D-3, which named `IMetricCalculator<TInputs>` for the same reason: the
equity assumption does not belong in the name.

**Approval and execution endpoints were not added.** Section 13, item 1, with the reason.

## 16. Dependencies on previous phases

Phase 1 in full: the Action/Policy seam, the two write guards, `Claim`/`Provenance`, `Money`,
`Percentage`, `Confidence`, `CorrelationId`, `AuditRecord`, and the capability enumeration —
including `ApprovalAdministration` and `FinancialExecution`, which existed so they could be refused
before anything could use them. Phase 2's `IngestionSubject` and `SourceId`. Phase 3's `MetricId`,
`MetricResult`, `CalculationVersion` and the knowledge-cutoff guard, which an opportunity score
inherits unchanged. Phase 4 contributed nothing to this path by design: an agent's output is an
`AiInterpretation`, and an architecture test now asserts that no type in the AI namespaces
references the approval, limit, capital or execution machinery.

## 17. Known issues found and fixed during this phase

Recorded in full because the record of what a gate caught is the evidence that the gate works.

1. **`CapitalLedger.IsBalanced` was wrong for income accounts.** It summed every account balance
   with the same sign, which is zero for a purchase and its fee and twice the gain for a disposal.
   Fixed to the accounting identity (D-8).
2. **The concentration limit could never be satisfied.** Measured against total exposure, the first
   position in a flat book is the whole book. Fixed to a share of equity, with a fail-closed branch
   for a book holding no equity (D-2).
3. **Two ledger entries saved together wrote a null `credit_account`.** The well-known accounts were
   cached singletons, so one owned instance had two owners and the provider resolved it by writing
   one as null. Surfaced as a not-null violation in the integration suite. Fixed (D-7).
4. **Approval issuance wrote outside an authorisation window**, which the persistence guard refuses
   — so the approval path could not have worked against a real database. Fixed by routing it through
   the gateway (D-3).
5. **The executor's opportunity transition was never persisted.** The repository stages; nothing
   saved, and the window had closed. Fixed (D-4).
6. **`PostgresFixture.TruncateStatement` did not name the four new tables**, so
   `DatabaseResetCoverageTests` would have failed and rows would have leaked between tests. Fixed.
7. **A safety claim in `Infrastructure/DependencyInjection` was not backed by a test.** The comment
   said an architecture test asserted that every registered venue is simulated. No such test existed.
   `ExecutionRuleTests.Every_execution_venue_in_the_solution_is_simulated` now does, and the comment
   names it.
8. **The scaffolded migration failed the build** on CA1861 (constant array arguments). The generated
   file was corrected rather than the rule exempted; the column lists are unchanged.

## 18. Recommended next phase

§P puts **Phase 6 — continuous operation** next: `Watch` and triggers, the `OperatingCycle` state
machine, the outbox, budgets and cooldowns, `AutonomyGrant` resolution, shadow mode, and the
autonomy-escape suite. Its exit criterion is two weeks of unattended running with no duplicate
actions, no runaway cost and no unhandled escalation.

Phase 5 hands it a complete decision path that is already gated, audited and reversible, and three
things it will need immediately: an idempotency key on every action, a limit engine that reads a
snapshot rather than a database, and a kill switch that answers without one.

**Not started, and deliberately.** Phase 6 begins when this document and the verification log entry
for it have been read.
