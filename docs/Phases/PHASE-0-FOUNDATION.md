# Phase 0 — Foundation, governance and configuration

**Status:** Implemented — verification pending
**Approved:** with decisions D-1 to D-5
**Last updated:** 2026-08-25

---

## 1. Phase objective

Establish a foundation that a long-lived autonomous platform can be built on: a clean layered
solution with an enforced dependency direction, build governance that fails on drift rather than
accumulating it, configuration and secret handling that never puts a credential in source, and an
audit of the existing repository so that later phases start from what is actually there.

The objective was explicitly *not* to build features. Phase 0 exists so that Phase 1 has somewhere
correct to put things.

## 2. Scope

**In scope.** Repository audit; solution and project layout; dependency-direction rules; central
package management; analyzer and warning policy; configuration and options validation; secret
management approach; structured logging and correlation; health endpoints; CI; the architecture
and security documents.

**Out of scope.** Any domain model, any persistence, any safety machinery, any provider
integration. All deferred to Phase 1 and later.

## 3. What was implemented

- A **repository audit** (`docs/AUDIT_AND_TARGET_ARCHITECTURE.md`) covering sections A–Q, which
  corrected three factual errors in the original brief and proposed the target architecture.
- The **solution skeleton** under `src/` and `tests/`, using dotted `AI.Investment.*` naming
  (decision D-1), with the four production projects and six test projects registered in
  `AI-Investment-Analyst.sln` alongside `docs` and `prompts` solution folders.
- **Build governance** in `Directory.Build.props`, applied to every project.
- **Central Package Management** in `Directory.Packages.props`, with transitive pinning on and a
  written justification beside every package.
- **Package source control** in `NuGet.config`.
- **SDK pinning** in `global.json` (8.0.100, `rollForward: latestMajor`).
- **Code style** in `.editorconfig`, including naming rules for private fields.
- The **API host**: Serilog structured logging, correlation-ID middleware, a global exception
  handler, validated options, Swagger, and liveness/readiness health endpoints.
- **CI** in `.github/workflows/ci.yml`: restore, Release build with
  `ContinuousIntegrationBuild=true`, test with TRX output, artifact upload, and a separate
  vulnerable-dependency audit job.
- `scripts/phase0-remove-legacy-projects.ps1` to remove the pre-Phase-0 project layout.
- `docs/SYSTEM_ARCHITECTURE.md`, `docs/SECURITY.md`, and
  `docs/decisions/0001-phase0-phase1-approved-decisions.md`.

## 4. Architecture changes

The dependency direction was established and is now enforced by tests rather than convention:

```
Domain  ←  Application  ←  Infrastructure
   ↖             ↖              ↗
        Api (composition only)
```

- `Domain` references nothing. No EF Core, no ASP.NET, no logging framework.
- `Application` references `Domain` and declares abstractions it needs (`IClock`,
  `ICompanyRepository`, `IUnitOfWork`, `IAuditSink`, …). It does not reference `Infrastructure`.
- `Infrastructure` implements those abstractions and owns every external concern.
- `Api` references `Application` and `Infrastructure` and does composition only — no business
  logic, no data access.

Decision **D-3** (vertical slices by feature) shapes the `Application` layer: a feature is a
folder containing its command, handler, validator, parameters and result, rather than being spread
across `Commands/`, `Handlers/` and `Validators/` trees.

## 5. Important projects/files

| Path | Role |
|---|---|
| `Directory.Build.props` | Nullable, warnings-as-errors, analyzer level pin, CA1032 suppression |
| `Directory.Packages.props` | Every package version, each with a stated reason |
| `tests/Directory.Build.props` | Test-only settings; scopes CA1707 off for tests |
| `NuGet.config` | `<clear/>` + nuget.org only + package source mapping |
| `global.json` | SDK pin |
| `.editorconfig` | Style and naming, including private-field prefix rules |
| `.github/workflows/ci.yml` | Build, test, dependency audit |
| `src/AI.Investment.Api/Program.cs` | Composition root |
| `src/AI.Investment.Api/Middleware/CorrelationIdMiddleware.cs` | Correlation identity per request |
| `src/AI.Investment.Api/Diagnostics/GlobalExceptionHandler.cs` | ProblemDetails mapping |
| `src/AI.Investment.Api/Configuration/ValidatedOptionsExtensions.cs` | Options + validation helper |
| `docs/AUDIT_AND_TARGET_ARCHITECTURE.md` | The Phase 0 audit |

## 6. Domain / Application / Infrastructure changes

Phase 0 created the projects and their reference graph. It added no domain types, no handlers and
no persistence. The only production code written was in `Api`: hosting, logging, correlation,
error handling, options and health.

## 7. Database changes

None. Decision **D-2** (PostgreSQL + EF Core with a point-in-time schema) was approved during this
phase but implemented in Phase 1.

## 8. APIs / contracts

- `GET /health` — overall health
- `GET /health/live` — liveness, tagged checks only
- `GET /health/ready` — readiness
- Swagger UI in development

Every response carries an `X-Correlation-ID` header. Errors are returned as RFC 7807
`ProblemDetails` from a single global handler, so no endpoint formats its own errors.

## 9. Security and safety changes

- **No secret is ever hard-coded.** Configuration is layered: `appsettings.json` →
  `appsettings.{Environment}.json` → user secrets → environment variables, with environment
  variables winning. A real connection string belongs in user secrets or the environment, never in
  a file under source control.
- **Nothing credential-shaped is logged.** Serilog is configured with explicit enrichment;
  connection strings and provider keys are not written to any sink.
- **`EnableSensitiveDataLogging`** exists as a `DatabaseOptions` flag, defaulting to false, so
  enabling EF's parameter logging is a deliberate act rather than an accident.
- **Options are validated at startup**, so a misconfigured deployment fails to start rather than
  failing later in an unclear way.
- `docs/SECURITY.md` records the provider-isolation and licensing posture: the platform builds
  abstractions over providers and documents what each provider requires, rather than working
  around subscription or licensing restrictions.

## 10. Dependencies

Added in this phase, each for a stated reason:

| Package | Reason |
|---|---|
| `Serilog`, `Serilog.AspNetCore`, `Serilog.Sinks.Console` | Structured logging with correlation as a first-class field. `Serilog` is referenced directly because `Program.cs` uses the static `Log` API. |
| `Swashbuckle.AspNetCore` | OpenAPI/Swagger. Present before Phase 0; retained. |

Deliberately **not** added: FluentAssertions (v8+ is commercially licensed), NSubstitute,
MediatR, AutoMapper. The reasoning is recorded in `Directory.Packages.props` itself.

## 11. Tests

Phase 0 added the six test projects and the architecture tests that make the layering rules
executable (`tests/AI.Investment.Architecture.Tests/LayeringRuleTests.cs`, NetArchTest, 6 tests).
Feature tests arrived with Phase 1.

## 12. Verification results

**Not verified.** No gate has passed.

| Gate | Status |
|---|---|
| `dotnet build` | Not run — no .NET SDK available to the assistant |
| `dotnet test` | Not run |
| Runtime startup | Not run |
| CI workflow | Present, never triggered |

What *was* done: every file was reviewed statically, and a series of analyzer and build failures
reported by the developer were diagnosed and fixed (see section 15 and the verification log).
No build or test result has ever been claimed as passing.

## 13. Known limitations

- The CI workflow has never executed. Its correctness is unproven.
- `scripts/phase0-remove-legacy-projects.ps1` has not been run; the legacy projects it removes may
  still be present.
- `AnalysisLevel` is pinned to `8.0-Recommended` to stop analyzer drift between SDK versions. This
  means new analyzers shipped with later SDKs are not applied until the pin is raised deliberately.
- Health checks report database connectivity only. There is no dependency-level health for
  providers, because no providers exist yet.

## 14. Architectural decisions

| ID | Decision | Rationale |
|---|---|---|
| D-1 | Dotted `AI.Investment.*` project naming under `src/` | Assembly name and namespace agree; the folder tree reads as the architecture. |
| D-2 | PostgreSQL + EF Core, point-in-time schema | `jsonb` for evolving detail, real constraints, and the time-series support later phases need. |
| D-3 | Vertical slices by feature | A change to one feature touches one folder. |
| D-4 | The Action/Policy safety seam is mandatory in Phase 1, not deferred | Retrofitting a safety seam into a system that already writes is how safety seams end up with holes. |
| D-5 | The first L4 autonomy candidate must be low-risk and reversible, never trading | Autonomy is earned on work whose worst outcome is an undo. |
| — | Warnings are errors, solution-wide | Warning debt is silent architectural debt. |
| — | Central Package Management with transitive pinning | One version per package, chosen here, with a reason. |
| — | Package source mapping in `NuGet.config` | Dependency-confusion control, rather than suppressing NU1507. |

## 15. Deviations from the approved plan

- **`NuGet.config` was added**, which the plan did not list. It became necessary when CPM reported
  NU1507 for multiple package sources. The alternative — suppressing the warning — would have left
  a real dependency-confusion exposure in place.
- **`tests/Directory.Build.props` was added.** CA1707 (underscores in identifiers) fires on every
  descriptive test name. Rather than suppress it solution-wide, it is scoped off for tests only, on
  the argument that a test name *is* the failure message.
  - While doing this a latent bug was found and removed: the root `Directory.Build.props` carried a
    `Condition="'$(IsTestProject)' == 'true'"` block that was **always false**, because
    `Directory.Build.props` is imported before the project body sets that property. The block had
    silently done nothing. A comment now records this so it is not reintroduced.
- **`Program.cs` was rewritten from top-level statements** to `public sealed class Program` with a
  private constructor. Top-level statements trip CA1050 under warnings-as-errors and produce an
  internal `Program` type that `WebApplicationFactory<Program>` cannot reach.
- **`AnalysisLevel` was pinned.** Not planned; added after CA1848/CA1861 appeared from an SDK
  analyzer update mid-phase.

## 16. Dependencies on previous phases

None. This is the first phase.

## 17. Capabilities enabled for future phases

- A layered solution where the dependency direction is checked by tests, so later phases cannot
  quietly invert it.
- A configuration and secret pipeline that later phases can add provider credentials to without
  inventing a new mechanism.
- Correlation identity threaded from the HTTP edge, which the audit trail depends on.
- A build that fails on the first warning, so drift is caught in the phase that introduced it.

## 18. Recommended next phase

Phase 1 — domain core, epistemic model and the Action/Policy safety seam. Per decision D-4 the
safety seam must exist before anything in the system performs a side effect.
