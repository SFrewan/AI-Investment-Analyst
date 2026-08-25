# Phase 1 — Domain core, epistemic model and the Action/Policy safety seam

**Status:** Implemented — verification pending
**Approved:** with decisions D-1 to D-5; safety seam mandatory per D-4
**Last updated:** 2026-08-25 (Provenance entry amended after Phase 2 stage 2)

---

## 1. Phase objective

Build the core the whole platform rests on: a domain model that keeps its own invariants, an
epistemic model that distinguishes a fact from a guess, and — the reason this phase exists — a
safety seam through which every side effect must pass and be recorded.

The seam is the point of the phase. A system intended to act autonomously must have a single,
deterministic, fail-closed gate between deciding and doing, and that gate has to be built before
anything writes, not after.

## 2. Scope

**In scope.** Domain primitives and value objects; the `Company` aggregate; the `Claim`/`Provenance`
epistemic model; the `ActionProposal` → `PolicyEngine` → `PolicyDecision` → `ActionExecution`
→ `AuditRecord` seam; risk tiering; idempotency; EF Core persistence with a write guard;
one harmless vertical slice routed through the seam end to end; six test projects.

**Out of scope.** Any external data provider, any AI proposer, any autonomy level above manual,
any financial capability. `Capability.FinancialExecution` exists in the enum precisely so it can
be refused, not so it can be used.

## 3. What was implemented

**Domain primitives and value objects.** `Money` (with currency-mismatch protection),
`Currency`, `Percentage`, `Confidence`, `DateRange`, `Ticker`, `Exchange`, `CorrelationId`,
`CompanyId`, `ClaimId`, `AggregateRoot<TId>`, and a small exception hierarchy
(`DomainException` → `DomainValidationException`, `DomainRuleViolationException`,
`CurrencyMismatchException`).

**The epistemic model.** `Claim` / `Claim<T>` carrying a `ClaimKind` of `Fact`, `Calculation`,
`AiInterpretation` or `Prediction`, with `Provenance` (`AsOfUtc`, `PublishedAtUtc`,
`RetrievedAtUtc`, source identity, optional URL) and an optional `Confidence`. The type enforces
the rule that gives it its value: **confidence is forbidden on a fact and required on a
judgement.** A fact with 80% confidence is not a fact, and an AI interpretation without a
confidence is being passed off as one.

**The Action/Policy safety seam.**

```
ActionProposal ──▶ PolicyEngine ──▶ PolicyDecision ──▶ ActionExecution ──▶ AuditRecord
   (intent)        (pure, total,     Execute |          (append-only        (append-only
                   deterministic)    RequireApproval |   ledger)             trail)
                                     Deny
```

`ActionGateway` (Application) is the only way to run an effect. The effect itself is a delegate
the caller supplies and **never invokes**:

```csharp
Task<ActionOutcome<TResult>> DispatchAsync<TResult>(
    ActionProposal proposal,
    Func<CancellationToken, Task<TResult>> effect,
    CancellationToken cancellationToken = default);
```

**The `Company` vertical slice** — create, get by id, search — chosen deliberately as the harmless
first slice. Creating a reference-data row is about as reversible as a side effect gets.

**Persistence.** EF Core 8 + Npgsql, four entity configurations, an `InitialCreate` migration, a
repository, a unit of work, and a `DbContext`-level write guard.

## 4. Architecture changes

- The seam became a hard architectural boundary rather than a convention: `Application` exposes
  `IActionGateway` and nothing else that performs a side effect.
- **Two independent write guards** were introduced, deliberately duplicating the enforcement:
  1. *Domain.* `ActionExecution.Start` calls `PolicyDecision.EnsureAuthorises`, so an execution
     object cannot be constructed without a decision that permits it.
  2. *Persistence.* `AppDbContext.SaveChangesAsync` calls `GuardWrites()`, which throws
     `UnauthorizedWriteException` when no authorisation window is open.

  Two mechanisms because a single call site can be forgotten. The domain guard protects the
  object model; the persistence guard protects the database from code that never touched the
  object model.
- **Fail-closed determinism** was adopted as a design rule, not a habit: `KillSwitchState.Unknown`
  denies exactly like `Engaged`; a missing capability policy denies; an unrecognised enum value is
  denied or treated as `Critical`. The system's uncertainty about its own safety state is treated
  as unsafe.

## 5. Important projects/files

| Path | Role |
|---|---|
| `src/AI.Investment.Domain/Actions/PolicyEngine.cs` | The gate. Pure, total, fail-closed, versioned rules. |
| `src/AI.Investment.Domain/Actions/PolicyDecision.cs` | The authorisation token. `EnsureAuthorises` is the domain guard. |
| `src/AI.Investment.Domain/Actions/ActionProposal.cs` | Intent: capability, type, target, parameters, economics, proposer, idempotency key. |
| `src/AI.Investment.Domain/Actions/ActionExecution.cs` | Append-only ledger entry. `Start`, `MarkSucceeded`, `MarkFailed`. |
| `src/AI.Investment.Domain/Actions/RiskTierCalculator.cs` | Reversibility-first risk tiering. |
| `src/AI.Investment.Domain/Actions/CapabilityPolicy.cs` | Per-capability configuration shape. |
| `src/AI.Investment.Domain/Auditing/AuditRecord.cs` | Append-only trail, `jsonb` details. |
| `src/AI.Investment.Domain/Evidence/Claim.cs`, `ClaimOfT.cs`, `Provenance.cs` | The epistemic model. |
| `src/AI.Investment.Application/Actions/ActionGateway.cs` | The single dispatch path. |
| `src/AI.Investment.Infrastructure/Persistence/AppDbContext.cs` | The persistence guard. |
| `src/AI.Investment.Infrastructure/Persistence/DesignTimeDbContextFactory.cs` | EF tooling configuration, no fallback connection string. |
| `src/AI.Investment.Infrastructure/Actions/ScopedWriteAuthorization.cs` | The authorisation window. |
| `src/AI.Investment.Api/Controllers/CompaniesController.cs` | The slice's HTTP surface. |

## 6. Domain / Application / Infrastructure changes

**Domain** gained the `Actions`, `Auditing`, `Evidence`, `Companies`, `Common`, `ValueObjects`,
`Enums` and `Exceptions` namespaces. It still references no framework.

Enumerations, with ordering that carries meaning where relevant:

- `Capability`: `ReferenceDataManagement`, `DataIngestion`, `Analysis`, `OpportunityManagement`,
  `ApprovalAdministration`, `PolicyAdministration`, `AutonomyAdministration`, `FinancialExecution`
- `RiskTier`: `Low` → `Medium` → `High` → `Critical`
- `ReversibilityClass`: `Reversible` → `ReversibleWithCost` → `Irreversible`
- `PolicyOutcome`: `Execute`, `RequireApproval`, `Deny`
- `KillSwitchState`: includes `Unknown`, which denies
- `ActionExecutionStatus`, `AuditEventType`, `ProposerKind`, `ClaimKind`

**Application** gained the abstractions it needs (`IClock`, `ICorrelationContext`,
`ICompanyRepository`, `IUnitOfWork`, `IAuditSink`, `IActionExecutionStore`, `IIdempotencyStore`,
`IWriteAuthorization`, `IPolicyContextProvider`, `IDatabaseConnectivityProbe`), the
`IActionGateway`/`ActionGateway` pair, `ActionOutcome<T>`, `PagedResult<T>`, and the `Companies`
slice (`CreateCompany`, `GetCompany`, `SearchCompanies`).

**Infrastructure** gained `AppDbContext` and its four configurations, `CompanyRepository`,
`UnitOfWork`, `EfAuditSink`, `EfActionExecutionStore`, `EfIdempotencyStore`,
`ScopedWriteAuthorization`, `ConfiguredPolicyContextProvider`, `SystemClock`,
`DatabaseConnectivityProbe`, `DatabaseOptions`, `SafetyOptions`, and
`DesignTimeDbContextFactory`.

## 7. Database changes

Migration `20260825023757_InitialCreate` creates four tables.

| Table | Purpose | Notes |
|---|---|---|
| `companies` | Reference data | `ix_companies_ticker` **unique**; `ix_companies_name` |
| `action_executions` | Append-only execution ledger | `decision_id` is **NOT NULL** — the schema-level expression of "no effect without an authorising decision" |
| `audit_records` | Append-only audit trail | `details` is `jsonb`; queried fields are real indexed columns |
| `processed_actions` | Idempotency claims | `idempotency_key` is the primary key, so deduplication is enforced by the database, not by racing application code |

Value objects are stored as their primitive representation through EF converters, and read back
**through their factories** (`Ticker.Create`, not a bypass constructor), so a row that violates a
domain rule fails loudly on load rather than becoming an invalid object in memory.

`audit_records.details` is `jsonb` because the set of useful detail keys grows every phase — agent
identity, model, prompt version, approval, measured outcome — and these values are read during an
investigation rather than joined on. The fields that *are* queried (correlation, capability,
outcome, risk tier) are proper columns.

## 8. APIs / contracts

| Endpoint | Behaviour |
|---|---|
| `POST /api/companies` | Routed through the seam. `201` on execute, `202` when the policy requires approval, `400` on validation failure, `403` on deny, `409` on ticker conflict |
| `GET /api/companies/{id:guid}` | `200` / `404` |
| `GET /api/companies` | `200` with `PagedResult<CompanyDto>` |
| `GET /health`, `/health/live`, `/health/ready` | From Phase 0; readiness now includes database connectivity |

The `202` and `403` responses are the seam surfacing in the HTTP contract. A denied action is not
an error in the client's request — it is the system declining to act — so it is reported as such
rather than as a `500`.

## 9. Security and safety changes

This is the substance of the phase.

**The policy engine.** Pure, total and deterministic: the same proposal and context always produce
the same decision, and every decision names the versioned policies that produced it
(`policy.kill-switch@1`, …), so a decision made months ago can be reconstructed and explained.

Rules evaluate in this order — the order itself is a safety property:

1. `policy.kill-switch@1` — engaged **or unknown** denies everything.
2. `policy.capability-defined@1` — a capability with no policy denies.
3. `policy.ai-may-not-administer-safety@1` — **structural, not configurable.**
4. `policy.financial-execution-unavailable@1` — **structural, not configurable.**
5. `policy.capability-enabled@1` — the first configurable rule.
6. `policy.ai-proposer-allowed@1`
7. `policy.irreversible-requires-approval@1`
8. `policy.risk-tier-within-auto-execute@1`

Rules 3 and 4 sit deliberately **before** the first configurable rule, so no configuration change
can switch them off:

- **An AI proposer may never administer `PolicyAdministration`, `AutonomyAdministration` or
  `ApprovalAdministration`.** A system that can widen its own permissions has no permissions. This
  is also the concrete implementation of the constraint that the system must not alter its own
  safety policies.
- **`Capability.FinancialExecution` is refused unconditionally.** It exists in the enum so that it
  can be denied and the denial audited, not so it can be enabled later by flipping a flag.

**Reversibility over amount.** `RiskTierCalculator` treats `ReversibilityClass` as the primary
axis. A large reversible action is safer than a small irreversible one, and tiering on money
alone gets that backwards.

**Idempotency.** Every proposal carries an idempotency key, claimed in `processed_actions` under a
primary key, so a retry cannot execute an effect twice.

**Secrets.** Unchanged from Phase 0 and still holding: nothing hard-coded, nothing logged, nothing
returned through an API response.

## 10. Dependencies

| Package | Reason |
|---|---|
| `Microsoft.EntityFrameworkCore`, `.Relational`, `.Design` | ORM per D-2. `.Design` is `PrivateAssets=all`. |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | `jsonb`, `ILIKE`, and the time-series support later phases need |
| `Microsoft.Extensions.*` abstractions | `Application` and `Infrastructure` are plain libraries and do not get the ASP.NET shared framework free |
| `Microsoft.Extensions.Options.DataAnnotations` | Supplies `ValidateDataAnnotations()` |
| `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `coverlet.collector` | Test stack |
| `NetArchTest.Rules` | Layering rules as executable assertions |
| `Microsoft.AspNetCore.Mvc.Testing` | In-process host for API contract tests |
| `Testcontainers.PostgreSql` | Real PostgreSQL for integration tests. An in-memory provider proves nothing about real constraints. |

Note recorded in `Directory.Packages.props` and repeated here because it has already been
"tidied" once: `Microsoft.Extensions.Configuration.Json` and `.UserSecrets` are pinned at
**8.0.1**, not 8.0.0 like their neighbours. Transitive pinning plus
`Microsoft.AspNetCore.Mvc.Testing` → `Microsoft.Extensions.Hosting` 8.0.1 requires it; a central
8.0.0 pins them *down* and NuGet reports NU1605.

## 11. Tests

Six projects, **189 executable cases** (126 `[Fact]`, 18 `[Theory]` expanding to 63 `[InlineData]`
cases).

| Project | Cases | Covers |
|---|---|---|
| `AI.Investment.Domain.UnitTests` | 97 | Value objects, `Company`, `Claim` invariants |
| `AI.Investment.Safety.Tests` | 54 | `PolicyEngine`, `RiskTierCalculator`, `ActionGateway`, and `AiCannotEscalateTests` |
| `AI.Investment.Application.UnitTests` | 17 | Handlers, against hand-written fakes |
| `AI.Investment.Integration.Tests` | 8 | `WriteGuardTests` against real PostgreSQL |
| `AI.Investment.Api.Tests` | 7 | Endpoint contracts via `WebApplicationFactory` |
| `AI.Investment.Architecture.Tests` | 6 | Layering rules via NetArchTest |

Counts are Phase 1 scope. `AI.Investment.Domain.UnitTests` has since grown to 184 cases as
Phase 2 added its `Sources` tests to the same project.

The phase's exit criterion is a test proving **no write path executes without a
`PolicyDecision`**. `WriteGuardTests.A_domain_write_without_an_authorisation_window_is_refused`
is that test, and it runs against a real database rather than a fake.

No mocking framework is used. Fakes are hand-written (`tests/.../Fakes/TestDoubles.cs`) — they
double as readable test infrastructure and have no DSL to learn.

## 12. Verification results

**Not verified.** The phase exit criterion has not been demonstrated.

| Gate | Status | Detail |
|---|---|---|
| `dotnet build` | **Not run** | No .NET SDK is reachable from the assistant's environment |
| `dotnet test` | **Not run** | — |
| Runtime startup | **Not run** | — |
| `dotnet ef database update` | **Not run by the assistant** | The migration was generated on the developer machine |
| Migration ↔ configuration cross-check | **PASSED** | 39 columns, 4 tables, 9 indexes; model snapshot agrees with the migration; 0 discrepancies |
| Live PostgreSQL 16 schema validation | **PASSED** | DDL derived from the migration applied to a real PostgreSQL 16.13 server; `information_schema` diff against the migration: 39 declared, 39 actual, 0 mismatches |
| Live schema invariant checks | **PASSED** | 12 checks, all as specified — see below |
| Static review | **PASSED** | 12 distinct analyzer/build failures diagnosed and fixed; one live defect found in the write guard (section 15) |

The twelve live-database checks, run against a real PostgreSQL 16 instance:

| Check | Expected | Result |
|---|---|---|
| `action_executions` with `decision_id` NULL | reject | rejected (not-null violation) |
| `action_executions` with `decision_id` | accept | accepted |
| Duplicate ticker | reject | rejected (`ix_companies_ticker`) |
| 13-character ticker | reject | rejected (varchar(12)) |
| Re-claim an idempotency key | reject | rejected (`PK_processed_actions`) |
| Malformed JSON in `details` | reject | rejected (invalid json) |
| `details->>'model'` lookup | works | works |
| `timestamptz` offset normalisation | 12:00 UTC | 12:00 UTC |
| Ticker lookup plan | index scan | `Index Scan using ix_companies_ticker` |

**No build or test result has ever been claimed as passing.** The environment limitation is real
and was established once, deliberately, rather than by repeated retries: no C# compiler exists in
the assistant's environment in any form, the SDK cannot be downloaded, and nuget.org is
unreachable — two independent walls. The developer-machine bridge provides file access only, with
no shell, so the developer's own SDK cannot be driven either.

**What must be run locally:** `dotnet build`, `dotnet test`, `dotnet ef database update`. The
integration tests additionally need a Docker daemon for Testcontainers, or they self-skip and
report the reason rather than passing silently.

## 13. Known limitations

- The phase exit criterion is **unmet** until the tests above actually run.
- **Append-only is enforced in application code only.** Nothing prevents `UPDATE audit_records`
  from a psql session. Database-level enforcement (a `REVOKE` on the application role plus a
  trigger) is a deliberate open decision, not an oversight.
- **`decision_id` has no foreign key**, because `PolicyDecision` is not persisted. The column
  records the authorising decision's identity but nothing enforces that the decision existed.
  Persisting decisions is later-phase work.
- ~~`Provenance.SourceId` is a free-form `string` (max 200, no format rule) and is not required to
  name a registered source.~~ **Resolved in Phase 2 stage 2** (2026-08-25): it is now a typed
  `SourceId` — a registry key — with the record locator moved to a separate `SourceRecordId`. The
  original limitation is kept here because Phase 1's schema and tests were written against the old
  shape, and the record of that matters more than a tidy list.
- No approval workflow exists. `RequireApproval` is returned and audited, but nothing yet acts on
  it — an operator has no queue to approve from.
- Autonomy is manual throughout. No L4 candidate has been proposed (D-5).

## 14. Architectural decisions

| Decision | Rationale |
|---|---|
| The effect is a delegate the gateway receives and the caller never invokes | Makes "you cannot run this without going through the seam" a compile-time shape rather than a review comment |
| Two independent write guards | One call site can be forgotten; two independent mechanisms both have to be |
| Fail closed on `Unknown` | Uncertainty about the safety state is treated as unsafe |
| Versioned policy IDs on every decision | A decision must be reconstructable months later |
| Structural rules ordered before configurable ones | Configuration must not be able to disable them |
| Reversibility as the primary risk axis, not amount | A large reversible action is safer than a small irreversible one |
| Confidence forbidden on facts, required on judgements | The distinction is the entire value of the epistemic model |
| Value objects read back through their factories | An invariant that holds only for objects the application created is not an invariant |
| `jsonb` for audit detail, real columns for queried fields | Detail keys grow every phase; queried fields must stay indexable |
| No mocking framework | Hand-written fakes read as documentation |
| Idempotency enforced by a primary key | Application-level checks race under retries |

## 15. Deviations from the approved plan

- **`DesignTimeDbContextFactory` was rewritten** after `dotnet ef database update` was observed
  connecting to `127.0.0.1:5432` instead of the configured database. Root cause was a Phase 1
  defect of this project's own making: the factory carried a hard-coded fallback connection
  string, documented as being used "only to determine the SQL dialect". That was true of
  `migrations add`, which merely scaffolds, and false of `database update`, which genuinely
  connects — so schema could be applied to whatever that constant happened to name.

  The rewrite removes the fallback entirely. Resolution order is now `AIINV_DESIGNTIME_DB` →
  environment variables → user secrets → `appsettings.{Environment}.json` → `appsettings.json`,
  with the API project located by walking up to the solution file and `UserSecretsId` parsed from
  the `.csproj` with `XDocument` rather than duplicated as a constant. When nothing is configured
  it throws with four concrete remedies. The environment defaults to `Development`, deliberately:
  running `database update` with nothing set should target development, and reaching production
  should require setting `ASPNETCORE_ENVIRONMENT`.

- **`ValidateOnStart()` was moved to the API composition root.** It lives in
  `Microsoft.Extensions.Hosting`, and a persistence library should not be making host-lifecycle
  decisions.

- **A write-guard defect was found and fixed on 2026-08-25**, after the phase code was otherwise
  complete. In `AppDbContext.GuardWrites()` the append-only check sat *after* the
  `if (_writeAuthorization.IsAuthorized) return;` early exit, so it could never run on an
  authorised write. Since an authorisation window stays open for the whole duration of an action's
  effect, the code best placed to rewrite the record of what it had just done was precisely the
  code exempted from being checked. The comment claimed "never legitimate, authorised or not"; the
  code did not implement it.

  The existing tests `An_audit_record_cannot_be_modified` and `_cannot_be_deleted` opened no
  authorisation window, which is why the defect survived them.

  *Fix:* the append-only check now runs first and unconditionally, before the authorisation exit.
  The "must be written through their stores" check also gained an explicit `IsSeamBookkeeping`
  filter so it no longer depends on an earlier check throwing first. Two tests were added —
  `An_audit_record_cannot_be_modified_even_inside_an_authorisation_window` and
  `An_execution_record_cannot_be_deleted_even_inside_an_authorisation_window` — which fail against
  the old code. Neither has been executed yet.

- **Analyzer-driven changes** made during the phase, each fixed in code rather than suppressed:
  NU1507 (package sources), CA1848/CA1861 (source-generated logging, cached array),
  CA1859 (private parameter widened to `List<ClaimId>`), CA1707 (scoped off for tests only),
  CA1000 (`PagedResult<T>.Empty` moved to a non-generic host class), CA1711
  (`PostgresCollection` → `SharedPostgresDatabase`), NU1605 (package downgrade), and an
  `.editorconfig` naming rule that demanded `_camelCase` for *all* private fields including
  `static readonly` ones — resolved by adding PascalCase rules for private static and const fields
  **before** the underscore rule, since Roslyn applies the first matching rule.

- **A delivery mistake, recorded because the correction is now a standing rule.** Phase 1 was
  first delivered as `phase1.zip` rather than applied to the working tree. This was rejected. All
  136 files were re-applied directly to the repository, and the standing rule is now that changes
  are applied directly to the local repository and their presence verified — never left in an
  archive or a temporary copy.

## 16. Dependencies on previous phases

Phase 0 in full: the layered project structure, the dependency-direction tests, the configuration
and secret pipeline, correlation identity from the HTTP edge (which the audit trail depends on),
and the warnings-as-errors build.

## 17. Capabilities enabled for future phases

- **Every later phase has somewhere safe to put a side effect.** Ingestion, analysis and any
  eventual autonomy route through `IActionGateway` and inherit policy evaluation, audit and
  idempotency without implementing any of it.
- **Every later phase has somewhere to put a belief.** `Claim<T>` and `Provenance` are how data
  arrives with its origin and its epistemic status attached.
- A real persistence layer with migrations, converters and `jsonb`, ready for the historical
  storage Phase 2 needs.
- Capability enumeration and per-capability policy already exist, so a new capability is a policy
  entry rather than a new mechanism.
- The audit trail is in place before there is anything interesting to audit, which is the only
  order that produces a complete one.

## 18. Recommended next phase

Phase 2 — Global data and intelligence foundation. It is the natural successor: the seam and the
epistemic model are built, and what they lack is data with a trustworthy origin.

**Before Phase 2 stage 2 begins,** the Phase 1 verification gate should be closed by running
build, tests and migration locally. Phase 2 stage 1 was deliberately chosen as work that a Phase 1
runtime defect could not invalidate, but that headroom does not extend indefinitely.
