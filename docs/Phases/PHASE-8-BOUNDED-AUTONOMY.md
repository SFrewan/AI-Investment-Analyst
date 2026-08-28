# Phase 8 — Bounded autonomy

**Status:** Foundation implemented and verified by execution. **Promotion is blocked.** No capability
has been promoted, nothing executes automatically, no live venue exists, and autonomy remains **L3**.

**Canonical scope:** §P of `PHASE0_AUTONOMOUS_PLATFORM_ARCHITECTURE.md` — automatic execution of the
lowest-risk, reversible action classes; per-capability grants at L4; automatic demotion (§K.6);
execution-plane hardening; and the live-venue gate as a formal, separate decision. §P marks the whole
phase *"only if Phase 7 justifies it"*.

---

## 1. The precondition, and why it is not met

Phase 7's measured performance report exists, was generated against a real database, and has been
read. Its verdict is **not established**: no prediction survived the point-in-time guard, because the
repository holds no opportunities, no price history and no shadow measurements.

That is not a technicality to be worked around. Promotion is a claim about measured behaviour, and
there is no measurement. The system therefore has to represent **PROMOTION NOT JUSTIFIED** as a
first-class state rather than falling through to a permissive default, and everything below is built
so that it does.

`GET /api/autonomy/promotion` answers that question directly, and today answers it with a list of what
is missing.

## 2. What was built

| Piece | What it is |
|---|---|
| `PromotionCriteria` | The bar. Declared in code, not configurable. |
| `PromotionAssessment` | Pure function from a validation report to justified-or-not, with one reason per unmet criterion. |
| `PromotionWarrant` | The only artefact that permits a grant of unattended execution. Cannot be built from an unjustified assessment. |
| `AutonomyGrant.IssueBounded` | The grant factory that takes a warrant and refuses anything the warrant does not cover. |
| `BoundedExecutionRule` | Which action classes may ever run unattended: lowest-risk and reversible. |
| `DemotionPolicy` | Pure, fail-closed rule for automatic demotion. |
| `AutonomyCircuitBreaker` | Applies it, on a sweep, without anybody deciding to. |
| `LiveVenueAuthorization`, `LiveVenueGate` | The formal live-venue decision, as an artefact rather than a switch. |
| `PromotionService`, `LiveVenueService` | The application paths, both through the action seam. |
| `promotion_warrants`, `live_venue_authorizations` | Two tables, expected to stay empty. |

## 3. The promotion gate

Three refusals, in the order they bite.

**A warrant cannot be built from an unjustified assessment.** `PromotionWarrant.Issue` takes a
`PromotionAssessment` and throws when it is not justified. There is one public factory, no public
constructor, and no overload that skips the check, so there is no argument list that produces a
warrant from an empty report.

**An assessment fails closed on every absence.** Each criterion reads `IsMeasured` before it reads a
value: a metric that could not be computed fails its check rather than being skipped. That is why an
empty report produces a long list of refusals rather than a short one, and why "we could not tell" and
"we looked and it was not good enough" are recorded under different names.

**The production path refuses an unwarranted grant.** `AutonomyAdministration.GrantAsync` is the only
production code that writes a grant. A request above `AutonomyGrant.HighestAttendedMode` is denied
unless it names a warrant that is active and covers the capability, environment, action type, mode,
risk tier and exposure. An architecture test walks the IL of every production member and fails if any
type other than that service calls the grant factory — the gate is only a gate if it is the only door.

### Never promotable, whatever the evidence

- `FinancialExecution` — no execution plane exists, and a grant cannot create an authority the system
  does not implement.
- `PolicyAdministration`, `AutonomyAdministration`, `ApprovalAdministration` — a capability that can
  change grants is a capability that can widen its own.
- `ContinuousBounded` — the top of the ladder describes a platform that decides *when* to act as well
  as what to do. No measurement of past decisions is evidence about that; reaching it is a separate
  architectural decision, not a better report.

### Lowest-risk and reversible

`BoundedExecutionRule` is the canonical sentence written as a total function, and it binds where a
warrant is issued: a warrant above `RiskTier.Low` is refused, and so is one for an action class that
is not `ReversibilityClass.Reversible`. This is deliberately stricter than the policy engine's
existing irreversibility rule, which stops irreversible actions: an unwinding that costs money is a
decision somebody should be making, and unattended means nobody is.

## 4. Automatic demotion

`DemotionPolicy.Required` is pure, total and fail-closed. **Every signal that arrives unknown
demotes**, and that check runs before anything that reads a number — a store that is down, a query
that timed out, a deployment mid-flight are exactly the situations in which the platform should be
doing less rather than the same amount.

Triggers, in severity order: state unknown, kill switch engaged or unreadable, warrant no longer
valid, policy breach, execution failures, unhandled escalations, evidence no longer justifies,
evidence stale. The most serious true one is what gets recorded on the grant.

`AutonomyCircuitBreaker` applies it. Three properties are worth stating:

- **One level at a time.** A breach drops a grant from unattended execution to preparing for approval;
  the platform keeps working and starts asking. Repeated breaches walk it to Off on their own.
- **It only ever lowers.** There is no method that raises, renews or clears a demotion. Recovering
  autonomy means a person issuing a new grant against fresh evidence.
- **It ignores attended grants.** Demoting those on a transient signal would turn a platform that asks
  permission into one that has quietly stopped proposing anything.

Today the breaker demotes any unattended grant it finds on its first sweep, because policy breaches
and execution failures are not counted per capability anywhere in the platform and are therefore
reported as unknown. That is recorded in the code rather than left as a surprise, and counting them is
the prerequisite for any real promotion.

## 5. The live-venue gate

The roadmap calls this "a formal, separate decision". It is modelled as an artefact, not a setting:
two **different** named people, a written justification, a promotion warrant underneath it, a stated
ceiling on real money, an expiry measured in days, and an audit record for every step **including
every refusal**.

**Configuration is not authorisation, and that check is first.** `LiveVenueGate.Evaluate` refuses a
request that originated in a configuration value before it looks at the authorisation at all — so an
installation holding a perfectly valid authorisation still cannot activate a venue by writing `true`
somewhere. A test asserts exactly that: the same request permitted by hand is refused when it arrives
from configuration.

**The gate decides and cannot act.** It has one public method, it returns a `LiveVenueDecision`, and
it takes no delegate and no venue. There is nothing in this phase that registers a venue, opens a
connection or hands over a credential. A gate that also performed the thing it gates would be one
refactor away from performing it for the wrong reason.

**Nothing can complete the path today**, and not because of a flag: no warrant can exist, and
`LiveVenueAuthorization.Create` requires an active one.

## 6. Plane separation and credentials

`IExecutionVenue` is the only type that would ever hold a venue credential, and the contract carries
none — a test asserts that no member of it is named for a key, secret, token, password or credential.

A second test asserts that no type in the analysis half of the platform — AI, analytics, evidence,
observations, opportunities, validation, autonomy — can hold an `IExecutionVenue` in a field or accept
one as a parameter. Research, agents and evidence components cannot be handed the thing that would
hold a credential, so they cannot be handed the credential.

Every `IExecutionVenue` implementation in the solution reports itself simulated, asserted over the
built assemblies. Registering a real one remains a formal decision, gated by §5, and not a
configuration change.

## 7. Verification

| Gate | Result |
|---|---|
| `dotnet build` (Release, whole solution) | **Succeeded** — 0 warnings, 0 errors |
| `dotnet test` (Release, whole solution) | **Passed** — `build_exit=0 test_exit=0` |
| Suite total | **1684 total, 1684 passed, 0 failed, 0 skipped** |
| Migration validity | **Passed** — `20260828155059_Phase8BoundedAutonomy` applied; both tables created and round-tripped |
| Per-capability autonomy, L4 grant boundaries | **Passed** — `PromotionGateTests` |
| Automatic demotion, expired and invalid grants | **Passed** — `LiveVenueAndDemotionTests`, `BoundedAutonomyTests` |
| Self-promotion, policy bypass, shadow-to-execution escape | **Passed** — `BoundedAutonomyEscapeTests` |
| Credential isolation, plane separation | **Passed** — `BoundedAutonomyEscapeTests`, `BoundedAutonomyRuleTests` |
| Live-venue gate isolation; configuration cannot activate | **Passed** — asserted from both directions |
| Fail-closed behaviour | **Passed** — every unknown signal demotes; every unmeasured metric refuses |
| Concurrent and replay safety | **Passed** — revocation idempotent, coverage evaluated at the instant asked, unique index enforced by the database |
| Lowest-risk / reversible restriction | **Passed** — `BoundedExecutionRule`, enforced at warrant issuance |
| Secret scan | **Passed** — 0 findings |

Per assembly: Domain 938, Application 290, Safety 258, Integration 127, Architecture 50, Api 21.

### 7.1 What the gates caught

Four defects, all found by running the suite.

1. **An unmeasured excess return refused under the wrong name.** The assessment reported
   `NoBetterThanBenchmark` for a metric that could not be measured at all, which reads as "we looked
   and it lost". Split: unmeasurable now refuses under `PerformanceNotEstablished`, the same
   distinction the validation report makes one layer down.
2. **A warrant could be deleted inside an authorisation window.** The write guard's never-delete
   categories covered seam bookkeeping and operations records but not permissions, so a `Remove`
   committed. Added a third category: warrants and live-venue authorisations are revoked, never
   deleted. Found by the integration test that asserted it.
3. **The architecture test excluded the wrong type.** The body of `GrantAsync` lives in a
   compiler-generated state machine nested inside `AutonomyAdministration`, so excluding the outer
   type alone excluded nothing. The nested types are excluded by name now.
4. **A test could not express "unknown".** The demotion helper defaulted a null evidence age to a
   real one, so the case it meant to check was never checked. Given an explicit flag.

### 7.2 The mutation gate

Not run, and not extended. It covers seventeen files that decide whether something is allowed to
happen; Phase 8 changed none of them, so the Phase 6 result — 73.53 % against a break threshold of
70 % — stands unaffected. Extending it to `PromotionAssessment`, `BoundedExecutionRule`,
`DemotionPolicy` and `LiveVenueGate` is carried forward as a recommendation alongside the same
recommendation from Phase 7.

## 8. What is deliberately absent

- **No promotion.** No warrant exists, no grant above the attended ceiling exists, and none can be
  created while the evidence says what it says.
- **No automatic execution.** The bounded-execution rule is enforced where authority is created. It is
  not wired into the dispatch path, because there is no warranted grant that could reach one; wiring
  it is part of the first real promotion rather than something to write untested now.
- **No live venue.** No implementation, no registration, no credential, no connection.
- **No write endpoints.** `AutonomyController` is read-only. Issuing a warrant, writing a grant and
  authorising a venue are decisions with a person's name on them, and an HTTP endpoint has no name
  attached to it until there is authentication — which there is not.

## 9. What would unblock promotion

In order, and none of them are code:

1. **Data.** Ingested price history and opportunities that cite their evidence by the identifiers of
   stored observations, so Phase 7's guard can admit anything at all.
2. **Time.** A thirty-day horizon needs thirty days.
3. **A report that clears the bar** in §2 of `PHASE-7-VALIDATION.md` and `PromotionCriteria.Standard`
   here — including at least thirty shadow divergences with known outcomes, which is the only evidence
   that bears on whether acting more often would have been right.
4. **Counted breaches and failures per capability**, so the circuit breaker's signals stop arriving as
   unknown.
5. **A person**, to issue the warrant and to sign the grant.

## 10. Safety boundary

Unchanged. Autonomy remains **L3**. The only execution venue in the solution reports itself simulated,
`Capability.FinancialExecution` is refused unconditionally and structurally, no grant or warrant can
be issued for it, and no live credential, live venue or real-money path was introduced.

## 11. Recommended next phase

None. §P ends at Phase 8, and Phase 8's own exit criterion — *a named, narrow capability runs at L4 for
a defined period with zero policy breaches* — cannot be attempted until the evidence exists. The work
that matters next is the data plane running long enough to produce a validation report worth reading,
not another phase of architecture.
