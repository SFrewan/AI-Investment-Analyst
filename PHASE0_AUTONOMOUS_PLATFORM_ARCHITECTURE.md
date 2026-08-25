# Phase 0 — Project Audit & Autonomous Platform Architecture

**Report ID:** AUD-002
**Date:** 2026-08-24
**Scope:** `C:\Users\localadmin\Desktop\AI-Investment-Analyst` — full working tree, every project file, every hand-written source file, git state, remote, and all documentation.
**Supersedes:** AUD-001 (`AUDIT_AND_TARGET_ARCHITECTURE.md`), which remains valid on structure and data; this report re-verifies its findings against the current tree and extends it to the autonomy mandate.
**Status:** Findings and proposal. **No code has been changed. Nothing has been deleted. No file in the repository was modified by this audit.**

---

## 0. How to read this report

Per the platform's own §28 mandate to separate fact from interpretation, every statement is tagged:

- **[FACT]** — read directly from the repository. Verifiable by opening the named file.
- **[ASSESSMENT]** — engineering judgement about what a fact implies.
- **[PROPOSAL]** — a recommendation requiring approval before any implementation.

Two questions are asked of every architectural decision in this report, per the Phase 0 brief §14:

- **Q-AUTO:** *Will this let the system eventually operate without a human coordinating each normal step?*
- **Q-CTRL:** *Can that capability be constrained by deterministic code rather than by instructions to a model?*

A design that fails either question is called out explicitly. Sections H through L answer both by construction.

---

# A. Executive Summary

**[FACT]** The repository contains **four hand-written C# files totalling 95 lines**, three of which are unmodified `dotnet new webapi` template scaffolding. The only domain type is a `Company` POCO. The `AI-Investment-App` and `AI-Investment-Infrastructure` projects contain **zero** `.cs` files. There are no tests, no database, no external integrations, no AI code, no background processing, no authentication, and no CI.

**[FACT]** The repository *is* now under version control — this changed since AUD-001 was written. Two commits exist (`Add .gitattributes and .gitignore.`, then `Add project files.`), authored by `sfrewan`, with a remote at `https://github.com/SFrewan/AI-Investment-Analyst.git`. Seventeen files are tracked; `bin/`, `obj/` and `.vs/` are correctly excluded. AUD-001's most severe finding (F-01) is **resolved**.

**[ASSESSMENT]** The distance between what exists and what is specified is the defining fact of this project. The brief describes an autonomous multi-agent operating platform with a capital ledger, an approval workflow, a policy engine, a backtesting harness and up to fifteen agents. On disk, a single company record cannot yet be saved. **This is not a criticism — it is the most valuable moment in the project's life.** Every decision that is expensive to reverse is still free to make.

**[ASSESSMENT]** The gap that matters is not the missing code. It is that *nothing in the current structure has a place for autonomy to attach to.* There is no representation of "an action the system wants to take", no policy layer to gate it, no durable process to run without a request, and no audit substrate to reconstruct why anything happened. Autonomy is not a feature added at Level 4; it is a shape the system either has from the first vertical slice or acquires through a rewrite. The single most important recommendation in this report is §H.3: **make `Action` a first-class domain concept before writing the second use case.**

**[ASSESSMENT]** Three things in the repository are genuinely good and should be protected:

1. `AI-Investment-Domain` has **zero project references**. That is the most important rule in Clean Architecture and it is currently satisfied. Protect it with an executable architecture test, not with discipline.
2. `SYSTEM_ARCHITECTURE.md` is a coherent, disciplined intent document. Its §5 principles (evidence before conclusions, never present predictions as certainty, never fabricate financial data, preserve an audit trail) are exactly right and are treated in this report as binding requirements rather than aspirations.
3. The project is being planned before it is being built. That ordering is rare and it is why this audit is cheap.

**Maturity read:** Phase 0 of 7. Foundation incomplete. Autonomy level currently **L0** (human performs everything) with no mechanism to advance.

**Headline recommendation:** approve §Q — remaining foundation hygiene plus one vertical slice built *through the Action/Policy seam* — roughly 1.5 to 2 weeks. Do not start agents. Do not start ingestion. Prove the safety seam first, on a use case that cannot hurt anyone.

---

# B. Current Architecture

**[FACT]** The architecture as it exists is a four-project Clean Architecture skeleton with the following reference graph, confirmed both from the `.csproj` files and from build output:

```
AI-Investment-API ──┬──→ AI-Investment-App ──→ AI-Investment-Domain
                    └──────────────────────────→ AI-Investment-Domain

AI-Investment-Infrastructure ──┬──→ AI-Investment-App
                               └──→ AI-Investment-Domain

                  ( nothing references Infrastructure )
```

**[FACT]** `AI-Investment-API/bin/Debug/net8.0/` contains `AI-Investment-API.dll`, `AI-Investment-App.dll`, `AI-Investment-Domain.dll`, `Swashbuckle.*` and `Microsoft.OpenApi.dll` — and **no** `AI-Investment-Infrastructure.dll`. The Infrastructure assembly is not part of the running application.

**[FACT]** All four projects target `net8.0` with `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>`.

**[FACT]** Exactly one NuGet package is referenced anywhere in the solution: `Swashbuckle.AspNetCore 6.6.2`. No EF Core, no logging library, no validation library, no resilience library, no AI SDK, no test framework.

**[FACT]** `Program.cs` is the unmodified template host: `AddControllers`, `AddEndpointsApiExplorer`, `AddSwaggerGen`, Swagger UI in Development, `UseHttpsRedirection`, `UseAuthorization`, `MapControllers`. No DI registration of any application service exists, because no application service exists.

**[ASSESSMENT]** The *layering instinct* is correct and worth preserving. What is absent is every runtime concern that an autonomous platform is made of: a composition root that can reach Infrastructure, a persistence boundary, a background host, a message/queue substrate, an audit sink, and any notion of an operation that is not an HTTP request. The current architecture can only do one thing: answer a synchronous call. §K explains why that shape, unchanged, caps the system permanently at Level 2.

---

# C. Current Project Structure

**[FACT]** `AI-Investment-Analyst.sln` declares four C# projects and two *virtual* solution folders:

| Item | Kind | Contents |
|---|---|---|
| `AI-Investment-API` | `Microsoft.NET.Sdk.Web` | Template host, template controller, `appsettings`, `launchSettings`, `.http` file |
| `AI-Investment-App` | `Microsoft.NET.Sdk` | **Zero `.cs` files**; eight empty folders declared via `<Folder Include>` |
| `AI-Investment-Domain` | `Microsoft.NET.Sdk` | One `.cs` file (`Entities/Company.cs`); six empty folders declared |
| `AI-Investment-Infrastructure` | `Microsoft.NET.Sdk` | **Zero `.cs` files**, and unreferenced by the API |
| `Docs` | Solution folder (virtual) | Links `SYSTEM_ARCHITECTURE.md`, which physically lives at repo root |
| `Prompts` | Solution folder (virtual) | **Empty** |

**[FACT]** Complete inventory of hand-written source, excluding `bin/`, `obj/`, `.vs/` and `.git/`:

| File | Lines | What it is |
|---|---:|---|
| `AI-Investment-API/Program.cs` | 25 | Unmodified template host |
| `AI-Investment-API/WeatherForecast.cs` | 13 | Template scaffolding |
| `AI-Investment-API/Controllers/WeatherForecastController.cs` | 32 | Template scaffolding — **the only live endpoint in the system** |
| `AI-Investment-Domain/Entities/Company.cs` | 25 | The entire domain model |
| **Total C#** | **95** | |
| `SYSTEM_ARCHITECTURE.md` | 374 | Architecture intent document (repo root) |
| `AUDIT_AND_TARGET_ARCHITECTURE.md` | 523 | Prior audit AUD-001 (repo root) |

**[FACT]** Git state: initialized; branch `master`; two commits; remote `origin` → `https://github.com/SFrewan/AI-Investment-Analyst.git`; `.gitignore` is the standard GitHub VisualStudio template (6,585 bytes); `.gitattributes` present. Seventeen files tracked, none under `bin/`, `obj/` or `.vs/`.

**[FACT]** Repository visibility on GitHub **could not be determined from here**. An unauthenticated fetch of `https://github.com/SFrewan/AI-Investment-Analyst` returned HTTP 404 (consistent with a private repository, since GitHub returns 404 rather than 403 to anonymous callers, but also consistent with a rename); the GitHub API returned HTTP 403 (unauthenticated rate limit), and alternative URL forms are blocked by `robots.txt`. **[ASSESSMENT]** This is recorded as *unverified*, not as a finding. **No conclusion in this report depends on it** — the entire audit was performed against the local working tree. Visibility matters only for the §N.1 secrets recommendation and should be confirmed by you in the GitHub UI.

**[FACT]** There is **no** `.github/` directory, therefore no CI workflow, no PR template, no CODEOWNERS, and no dependency or secret scanning configuration.

**[FACT]** Corrections to the initiating brief — three statements do not match the repository:

1. Projects are named `AI-Investment-API / -App / -Domain / -Infrastructure`, **not** `AI.Investment.API / .Application / .Domain / .Infrastructure`.
2. There is no `Docs` directory on disk; `Docs` is a virtual solution folder.
3. There is no `Prompts` directory on disk and **no prompts exist**; `Prompts` is an empty virtual solution folder.

---

# D. Existing Domain Model

**[FACT]** The complete domain model:

```csharp
namespace AI_Investment_Domain.Entity        // note: singular, and omits the folder segment
{
    public class Company
    {
        public Guid     Id           { get; set; }
        public string   Name         { get; set; } = string.Empty;
        public string   Ticker       { get; set; } = string.Empty;
        public string?  Exchange     { get; set; }
        public string?  Sector       { get; set; }
        public string?  Industry     { get; set; }
        public string?  Country      { get; set; }
        public string?  Description  { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
```

**[FACT]** There are no value objects, no enums, no domain events, no exceptions, no interfaces, and no base entity — despite `ValueObjects/`, `Enums/`, `Events/`, `Exceptions/`, `Interfaces/` and `Common/` folders being declared in the `.csproj`.

**[ASSESSMENT]** Four specific problems, in order of cost-to-fix-later:

1. **Every property is publicly settable.** `new Company { Ticker = "" }` is valid. So is `Ticker = "not a ticker at all"`. A domain that cannot reject its own invalid states is not enforcing business rules; it is documenting them.
2. **`Ticker` is a bare `string`.** In a system whose entire purpose is to be correct about which instrument it is talking about, the identifier is the last thing that should be a free-form string. It needs a value object with a normalization rule (case, whitespace, exchange qualification) and an equality contract.
3. **No temporal identity.** `CreatedAtUtc`/`UpdatedAtUtc` are row-audit fields, not domain time. §M.3 explains why the distinction between *when a fact is about*, *when it became public* and *when we fetched it* is the decision most likely to invalidate every backtest this project ever runs.
4. **No `Money` type.** No money exists in the model yet, which is exactly why this is the moment to decide that a decimal without a currency will never be allowed to represent one.

**[ASSESSMENT]** Q-CTRL fails here today: there is no type in the system whose construction can be used to enforce a rule. That is the first thing §P Phase 1 fixes.

---

# E. Existing Application Layer

**[FACT]** `AI-Investment-App` contains **no source files**. Its `.csproj` declares eight empty folders: `Abstractions/`, `Common/`, `Companies/`, `DTOs/`, `Interfaces/`, `Mappings/`, `Services/`, `Validators/`.

**[FACT]** There are therefore no use cases, no application services, no DTOs, no mappings, no validators, no abstractions, no command/query types, and no orchestration of any kind.

**[ASSESSMENT]** The empty folders are not neutral — they encode a decision that has not been made. `Companies/` is feature-based (vertical slice); `Services/`, `DTOs/`, `Mappings/`, `Validators/` are layer-based (horizontal). Both organizing conventions work; the *mixture* produces a codebase where a developer must check two places for every change. This is nearly free to decide today and genuinely annoying to reverse at 200 files. Recommendation in §H.2: **vertical slices by feature**, because in this system a "use case" (analyse a ticker, evaluate an opportunity, execute an approved action) is the natural unit of both change and testing.

---

# F. Infrastructure Assessment

**[FACT]** `AI-Investment-Infrastructure` contains **no source files**. It references `AI-Investment-App` and `AI-Investment-Domain`. **Nothing references it**, and its assembly does not appear in the API's build output.

**[FACT]** Consequently there is: no `DbContext`, no migrations, no connection string, no repository implementation, no HTTP client for any provider, no AI provider client, no cache, no message bus, no scheduler, no file/blob storage, and no telemetry exporter.

**[FACT]** Configuration is template-default: `appsettings.json` contains only `Logging.LogLevel` and `AllowedHosts: "*"`. `appsettings.Development.json` contains only `Logging.LogLevel`. No `UserSecretsId` element exists in any `.csproj`. No typed options classes exist.

**[FACT]** Observability is template-default console logging. No structured logging, no correlation IDs, no health checks, no `ProblemDetails`, no global exception handler, no metrics, no tracing.

**[ASSESSMENT]** The orphaned Infrastructure project is a real defect, not a cosmetic one: a `DbContext` or provider client written there today **cannot be registered in the API's DI container**, because the API cannot see the assembly. The correct fix is for the API (and later the Worker) to reference Infrastructure **for composition only**, with an architecture test asserting that no API type ever *uses* an Infrastructure type outside `Program.cs`/`DependencyInjection.cs`. That gives the composition root what it needs while keeping the dependency rule enforced by a test rather than by memory.

---

# G. Current Problems

Findings from AUD-001 re-verified against the current tree, plus new findings specific to the autonomy mandate. **F-01 is resolved.** Severity reflects impact on the platform being built, not on the code that exists today.

### Resolved since AUD-001

| ID | Finding | Evidence of resolution |
|---|---|---|
| ~~F-01~~ | ~~Project not under version control~~ | **Resolved.** `.git` present, 2 commits, remote configured, `bin`/`obj`/`.vs` untracked. |

### Critical

**F-16 — There is no representation of "an action the system wants to take".**
**[FACT]** No `Action`, `Command`, `Proposal`, `Intent`, `Approval` or `Execution` type exists anywhere in the solution.
**[ASSESSMENT]** This is the highest-severity finding in this report and it is invisible in a normal code review because there is no code to review. Every safety requirement in the brief — approvals, capital limits, kill switch, audit, escalation, autonomy levels — is a statement *about actions*. With no action abstraction, each of those controls has to be implemented separately at every call site that does something, which is precisely how safety controls get bypassed. Autonomy added later without this seam means a rewrite of every side-effecting path.
**Remedy:** §H.3. Introduce `ActionProposal` / `PolicyDecision` / `ActionExecution` in Phase 1, and route the *first* trivial use case through it so the seam is load-bearing from day one.

**F-17 — No deterministic policy layer exists, and nothing prevents one from being replaced by a prompt.**
**[FACT]** No policy, limit, permission or authorization construct exists.
**[ASSESSMENT]** The brief is explicit (§5, §15): risk controls must be deterministic code, never model instructions. Today there is nothing to violate — which is why the architectural rule must be written down and made testable *before* the first agent, because "the model checks the limit" is the path of least resistance under deadline pressure.
**Remedy:** §L. A `PolicyEngine` that is pure, synchronous, total (never returns "unknown"), fail-closed, and unit- and mutation-tested.

### High

**F-18 — No process can run without an inbound HTTP request.**
**[FACT]** The only host is `Microsoft.NET.Sdk.Web`. No worker service, no hosted service, no scheduler, no queue.
**[ASSESSMENT]** This is the structural cap on autonomy. A request-scoped web host can reach Level 2 (recommend on demand) and no further. Everything from "monitor a watchlist" to "re-evaluate when a filing drops" to "measure the outcome of a decision made three months ago" requires a durable process with its own lifecycle. Adding it later is not hard — *building six months of use cases assuming a request context* is.

**F-19 — No audit substrate, and no correlation identity to hang one on.**
**[FACT]** No audit store, no correlation ID, no structured logging.
**[ASSESSMENT]** `SYSTEM_ARCHITECTURE.md` §13 and brief §13 require recording, for every decision: initiator, agent, model, inputs, sources, timestamp, analysis, confidence, recommendation, approval, execution result, and outcome. That is an append-only *store*, not a log file. It needs a correlation identifier flowing from the first trigger through every stage, and retrofitting that through an existing pipeline is one of the more miserable refactors in this class of system.

**F-03 — Security middleware that looks present but is not.**
**[FACT]** `Program.cs` calls `app.UseAuthorization()` with no authentication scheme registered, no policies, and no `[Authorize]` attributes anywhere. No CORS policy, no rate limiting, no HSTS, no request-size limits.
**[ASSESSMENT]** `UseAuthorization()` without authentication is a no-op that reads as security. For a system that will hold capital state and eventually broker credentials, the unauthenticated API is the first thing to fix after the foundation.

**F-04 — No persistence layer at all.** *(unchanged from AUD-001)*
**[FACT]** No EF Core, no `DbContext`, no migrations, no connection string, no repository abstraction.
**[ASSESSMENT]** Provenance (§M) and audit (§F-19) have nowhere to land, and the *shape* of the schema — specifically point-in-time correctness, §M.3 — is the decision most likely to invalidate the entire validation effort if made carelessly.

**F-05 — No secrets mechanism, and the tracked file most likely to receive a key is `appsettings.json`.**
**[FACT]** No `UserSecretsId` in any project. No typed configuration. `.gitignore` contains **no** pattern matching `appsettings*.json`, `.env`, `*.secret*` or `*credential*` (verified by search). `AI-Investment-API/appsettings.json` and `appsettings.Development.json` are both **tracked in git**, and a remote is configured.
**[ASSESSMENT]** Nothing leaks today because no secret exists yet. That is exactly the point: the first market-data API key acquired will, by default, be pasted into a tracked file and pushed. Establish the mechanism *before* the first key exists — this is a one-hour task that has a permanent failure mode attached to it.

**F-06 — No test project, and no seam to test against.** *(unchanged)*
**[FACT]** Zero test projects. No interfaces, no injected services, nothing mockable.
**[ASSESSMENT]** Brief §17 (historical validation) and §26 (testing) are not merely unimplemented, they are currently *unimplementable*. Testability is an architectural property fixed at the moment the first service is written.

**F-07 — The domain model is anemic and enforces no invariants.** *(unchanged — see §D)*

**F-02 — Infrastructure is unreachable; there is no composition root.** *(unchanged — see §F)*

**F-20 — No CI, and a remote that will accept anything pushed to it.**
**[FACT]** No `.github/` directory. A remote is configured and one push has occurred.
**[ASSESSMENT]** With a remote in place, CI is now cheap and load-bearing: build with warnings-as-errors, run tests, run architecture tests, run secret scanning, run `dotnet list package --vulnerable`. Without it, the safety rules in §L are documentation.

### Medium

**F-08 — Namespace does not match folder.**
**[FACT]** `Entities/Company.cs` declares `namespace AI_Investment_Domain.Entity` — singular, omitting the `Entities` segment.
**Remedy:** rename to `…Domain.Entities`. Trivial now, tedious after 200 files.

**F-09 — Project naming has drifted from the documented target.**
**[FACT]** `SYSTEM_ARCHITECTURE.md` §17 specifies `AI.Investment.API / .Domain / .Application / .Infrastructure`. On disk they are hyphenated, forcing `<RootNamespace>AI_Investment_Domain</RootNamespace>` because hyphens are illegal in C# identifiers.
**[ASSESSMENT]** Underscored namespaces are non-idiomatic and will appear in every `using` in the system forever. Renaming four projects with four source files between them costs perhaps twenty minutes; renaming at 300 files is a migration. **[PROPOSAL]** Rename now — see decision D-1 in §Q.

**F-10 — Template scaffolding is the only functioning endpoint, and it is committed.**
**[FACT]** `WeatherForecast.cs`, `WeatherForecastController.cs` and `AI-Investment-API.http` (which calls `/weatherforecast/`) are all tracked in git. `GET /WeatherForecast` returns random temperatures.

**F-11 — `Docs` and `Prompts` do not exist as directories.**
**[FACT]** Both are virtual solution folders; `Prompts` is empty; both markdown documents live at the repository root.
**[ASSESSMENT]** Prompts must be real, versioned files on disk — they are production artifacts whose exact version has to be recorded in every audit record (§I.4). A virtual folder cannot hold them, and a prompt that is not versioned makes every historical analysis unreproducible.

**F-12 — No build governance.**
**[FACT]** No `Directory.Build.props`, no `Directory.Packages.props`, no `.editorconfig`, no analyzer packages, no `TreatWarningsAsErrors`. `Nullable` is enabled per-project, with nothing to keep it enabled in projects five, six and seven.

**F-13 — The Application layer pre-declares two competing organizing conventions.** *(see §E)*

**F-14 — No observability substrate.** *(see §F, §F-19)*

**F-15 — The AI subsystem is entirely absent.**
**[FACT]** No AI project, no provider abstraction, no agent interface, no prompt store, no structured-output contracts.
**[ASSESSMENT]** Expected at this stage. Recorded so the gap is explicit rather than assumed.

**F-21 — No idempotency or deduplication concept anywhere.**
**[FACT]** No idempotency key, no outbox, no dedup by content hash.
**[ASSESSMENT]** Filed as Medium only because nothing executes yet. In an autonomous system it becomes Critical the moment a retry can place a second order or a re-run can double-count a fill. The concept belongs in the `Action` design (§H.3) from the start, not in the execution layer later.

---

# H. Target Architecture

## H.1 Solution layout

**[PROPOSAL]**

```
AI-Investment-Analyst.sln
├── src/
│   ├── AI.Investment.Domain            (no project references — enforced by architecture test)
│   ├── AI.Investment.Application       → Domain
│   ├── AI.Investment.Infrastructure    → Application, Domain      (EF Core, provider adapters, outbox)
│   ├── AI.Investment.Agents            → Application, Domain      (AI agents, prompts, contracts)
│   ├── AI.Investment.Api               → Application, Infrastructure, Agents   (composition root only)
│   ├── AI.Investment.Worker            → Application, Infrastructure, Agents   (triggers, cycles, monitoring)
│   └── AI.Investment.Execution         → Application, Domain      (SEPARATE PROCESS — §N.2)
├── tests/
│   ├── AI.Investment.Domain.UnitTests
│   ├── AI.Investment.Application.UnitTests
│   ├── AI.Investment.Safety.Tests           (policy engine, limits, kill switch — highest bar)
│   ├── AI.Investment.Integration.Tests      (Testcontainers, real DB, real migrations)
│   ├── AI.Investment.Api.Tests              (WebApplicationFactory)
│   ├── AI.Investment.Agents.Evaluation      (AI eval harness — not a unit-test suite)
│   └── AI.Investment.Architecture.Tests     (layering + safety rules as executable assertions)
├── docs/            (real directory)
├── prompts/         (real directory, versioned, referenced by PromptId@version)
└── .github/workflows/
```

**[PROPOSAL]** Three projects beyond the six documented in `SYSTEM_ARCHITECTURE.md` §17:

- **`Agents`** — separated from Infrastructure because AI providers have fundamentally different failure modes, cost characteristics, latency profiles and testing needs from a SQL connection. Mixing them means a retry policy tuned for a database applied to a model call.
- **`Worker`** — because continuous operation must not live inside a request-scoped web host (F-18).
- **`Execution`** — a *separate deployable process* with its own identity, holding the only credentials that can move money. This is the single structural control that makes §L.7 true rather than aspirational: the analysis plane cannot execute a trade even if fully compromised, because it does not possess the capability.

## H.2 Modules inside the layers

**[PROPOSAL]** Organize the Application layer by bounded context, as vertical slices. Each starts as a folder; each becomes its own project only if it earns it:

`ReferenceData` · `Ingestion` · `Fundamentals` · `News` · `Analysis` · `Scoring` · `Opportunity` · `Risk` · **`Policy`** · **`Action`** · `Approval` · `Capital` · `Execution` · `Audit` · **`Autonomy`** · `Evaluation` · `Learning`

The three bolded modules do not appear in the current brief's decomposition and are the load-bearing additions for autonomy.

## H.3 The seam that makes autonomy safe — the `Action` pipeline

**[PROPOSAL — the most important proposal in this report]**

Every side effect in the system, without exception, is expressed as an `ActionProposal` and passes through one gate. Not most side effects. Not the financial ones. **All of them** — including sending an email, writing to a watchlist, spending money on an LLM call, and calling a paid data provider.

```
  ┌─────────────┐   proposes    ┌────────────────┐
  │  Reasoning  │──────────────▶│ ActionProposal │   (data — never an instruction)
  │ (agents, or │               └────────┬───────┘
  │ det. rules) │                        │
  └─────────────┘                        ▼
                            ┌────────────────────────┐
                            │      PolicyEngine      │   deterministic · pure · total
                            │  (deterministic C#)    │   fail-closed · unit+mutation tested
                            └────────────┬───────────┘
                                         │ PolicyDecision
                 ┌───────────────────────┼───────────────────────┐
                 ▼                       ▼                       ▼
             Execute                RequireApproval            Deny
                 │                       │                       │
                 │                       ▼                       ▼
                 │              ┌────────────────┐         AuditRecord
                 │              │ ApprovalRequest│         (with reason)
                 │              └───────┬────────┘
                 │                      │ human decision
                 │                      ▼
                 │              ApprovalToken (single-use, scoped,
                 │              bound to hash of the exact action)
                 ▼                      ▼
            ┌──────────────────────────────────┐
            │        ActionExecutor            │  idempotency key · capability-scoped
            │  (capability-scoped executors)   │  · re-checks kill switch · records result
            └────────────────┬─────────────────┘
                             ▼
                       ActionExecution ──▶ AuditRecord ──▶ Outcome (measured later)
```

Why this specific shape:

- **Q-AUTO passes.** Raising autonomy means changing which `PolicyDecision` the engine returns for a given action class. The reasoning layer, the executors, the audit trail and the UI are unchanged. Level 1 → Level 5 is a configuration migration plus new executors, not a rewrite.
- **Q-CTRL passes.** There is exactly one place where "may this happen?" is answered, it contains no AI, it is pure and synchronous, and it can be exhaustively tested — including with mutation testing, which is how you demonstrate the guard rails cannot be bypassed rather than merely asserting it.
- **Agent output is data, never control flow.** An agent returns a *proposal*. It cannot execute, cannot call a tool with side effects, and cannot alter the policy that judges it. This is also the structural defence against prompt injection (§N.6): a malicious instruction inside a news article can at most produce a proposal, which the deterministic engine then denies.
- **Audit is free.** Every action has a record by construction, because there is one path.
- **Idempotency is free** (F-21): the key lives on the proposal.

**[PROPOSAL]** Sketch of the contracts — final shape to be designed in Phase 1:

```
ActionProposal
├── ProposalId, CorrelationId, CycleId
├── Capability        : e.g. IngestData | AnalyzeCompany | UpdateWatchlist | PlaceOrder
├── ActionType        : the specific operation
├── Target            : what it acts on (opportunity, security, account)
├── Parameters        : typed payload
├── Economics         : Money cost, Money exposure, reversibility class
├── RiskTier          : computed deterministically from Economics + Capability (NOT by a model)
├── ProposedBy        : AgentId@version | ServiceId  (+ PromptId@version if AI)
├── Evidence          : ClaimId[]   — what it is based on
├── Confidence        : Confidence? — required if any input was AI-derived
└── IdempotencyKey

PolicyDecision  =  Execute | RequireApproval(level) | Deny(reason)   // total, never Unknown
├── EvaluatedPolicies : PolicyId@version[]     — which rules fired, for the audit record
└── Constraints       : any limits the executor must additionally respect
```

## H.4 Core aggregates

| Aggregate | Purpose | Key invariants |
|---|---|---|
| `Company` / `Security` | Reference data | `Ticker` value object; exchange required for listed securities; delisted records are retained, never deleted (§M.3) |
| `Claim<T>` | Epistemic primitive (§H.5) | Kind + provenance mandatory; `Confidence` required for AI/prediction, forbidden for fact |
| `EvidenceBundle` | Immutable, hashed input to a run | Frozen at creation; hash is the replay key |
| `AnalysisRun` | One execution of the pipeline | Immutable once complete; records bundle hash, every agent output, model IDs, prompt versions, cost, duration |
| `Opportunity` | Generic per brief §10 (§J) | Typed; must carry ≥1 evidence claim and a risk assessment before it can leave `Draft` |
| `ActionProposal` / `ActionExecution` | The autonomy seam (§H.3) | Execution requires a `PolicyDecision`; approval-gated executions require a valid unconsumed token |
| `Policy` / `Limit` | Deterministic guard rails | Versioned; evaluated in code; never authored or altered by an agent at runtime |
| `AutonomyGrant` | What the system may do unattended (§K.2) | Scoped to (capability, risk tier, environment); expiring; auto-demoted on measured degradation |
| `ApprovalRequest` / `ApprovalToken` | Human gate | State machine `Draft → Pending → Approved \| Rejected \| Expired`; immutable after decision; token single-use and bound to an action hash |
| `CapitalAccount` | Ledger | Double-entry; balance is a projection of immutable entries; **no settable balance field exists** |
| `Recommendation` + `Outcome` | Historical validation | Price, score and thesis frozen at recommendation time |
| `AuditRecord` | The traceability spine | Append-only, hash-chained; no update or delete path exists in the application |

**[PROPOSAL]** Value objects from day one: `Ticker`, `Money` (amount + currency, no implicit conversion, no arithmetic across currencies), `Percentage`, `Confidence` (0–1 plus calibration bucket), `DateRange`, `Exchange`, `RiskTier`, `ReversibilityClass`.

## H.5 `Claim<T>` — making brief §28 mechanically enforceable

**[PROPOSAL]** The requirement to distinguish **FACT / CALCULATION / AI INTERPRETATION / PREDICTION / UNCERTAINTY** must be a first-class domain type, not a UI convention or a documentation promise. Every number that reaches a report carries its own epistemic status:

```
Claim<T>
├── Value        : T
├── Kind         : Fact | Calculation | AiInterpretation | Prediction
├── Provenance
│   ├── SourceId, SourceUrl
│   ├── AsOfUtc         — the date the fact is ABOUT
│   ├── PublishedAtUtc  — the date it became public knowledge
│   └── RetrievedAtUtc  — the date we fetched it
├── DerivedFrom  : ClaimId[]     (required for Calculation and AiInterpretation)
├── Confidence   : Confidence?   (required for AiInterpretation/Prediction, forbidden for Fact)
└── Caveats      : string[]
```

**[ASSESSMENT]** This one type converts "we will separate facts from interpretation" from an aspiration into something the compiler and the test suite enforce. A report becomes a *graph of claims*; "show me why you said this" becomes a traversal rather than a prose explanation; and the fabrication guard in §I.4 becomes mechanically checkable rather than a matter of trust. It is also what lets the dashboard render epistemic status honestly instead of presenting a model's guess in the same typeface as a filed revenue figure.

---

# I. Target AI Architecture

## I.1 What is an agent, and what is not

**[PROPOSAL]** The decision rule: **if the task has a correct answer that code can compute, it is a service, not an agent.** Non-determinism is a cost paid only where judgement over unstructured evidence is genuinely required.

| Capability | Classification | Why |
|---|---|---|
| Market research, News intelligence, Competitive intelligence | **AI agent** | Unstructured text, judgement, synthesis |
| Financial analysis (interpretation of statements) | **AI agent** | Narrative interpretation over structured inputs |
| Valuation, Growth analysis | **AI agent** (on top of deterministic metrics) | Ratios are computed; the *reading* is judgement |
| Risk analysis (identification) | **AI agent** | Enumerating what could go wrong from evidence |
| Opportunity discovery (screening) | **Deterministic service** first, agent second | Screening is a query; *ranking rationale* is judgement |
| Financial ratio calculation | **Deterministic service** | Arithmetic |
| **Profitability calculation** | **Deterministic service — NOT an agent** | It is arithmetic. Brief §7 lists it as an agent; this report pushes back |
| **Scoring** | **Deterministic service** | Config-driven weights; must be reproducible from a stored bundle |
| **Decision (execute / approve / deny)** | **Deterministic PolicyEngine — NEVER an agent** | §H.3, §L |
| **Approval request assembly** | **Deterministic service — NOT an agent** | It is a structured record built from an opportunity. Brief §7 lists it as an agent; this report pushes back |
| Recommendation narrative | **AI agent (synthesis)** | Explanation, constrained to validated inputs |
| Monitoring / trigger detection | **Deterministic service** | Threshold and event evaluation must be exact and cheap |
| Outcome measurement | **Deterministic service** | Arithmetic against recorded predictions |
| Learning / model improvement | **Offline pipeline, human-gated** | §K.5 — never online, never self-modifying |

**[ASSESSMENT]** Two pushbacks are worth stating plainly because they contradict the brief: making the **Approval Agent** and **Profitability Agent** into agents introduces non-determinism exactly where the system most needs determinism, and it puts a model on the safety path. An approval request is a record; profitability is arithmetic. Neither benefits from reasoning, and both are audited.

## I.2 Orchestration is C#, not a model

**[PROPOSAL]** Control flow is deterministic code. Agents are invoked by an explicit pipeline. No agent decides what runs next; no agent holds a side-effecting tool; agent output is data.

```
AnalysisRequest(security, asOf)
   │
   ├─ Stage 1  Evidence assembly       (deterministic — no LLM)
   │            prices, fundamentals, filings, news → EvidenceBundle (immutable, hashed)
   │
   ├─ Stage 2  Deterministic analytics (deterministic — no LLM)
   │            ratios, growth rates, health metrics → Claim<Calculation>[]
   │
   ├─ Stage 3  Specialist agents       (parallel fan-out, LLM)
   │            Financial · Valuation · Growth · News · Competitive · Risk
   │            each: EvidenceBundle → typed AgentResult (JSON-schema constrained)
   │
   ├─ Stage 4  Groundedness validation (deterministic — no LLM)
   │            every figure must trace to a claim in the bundle, or be rejected
   │
   ├─ Stage 5  Synthesis agent         (LLM — sees only validated Stage-3 output)
   │
   ├─ Stage 6  Scoring                 (deterministic — config-driven weights)
   │
   ├─ Stage 7  Opportunity + ActionProposal assembly (deterministic)
   │
   └─ Stage 8  PolicyEngine → Execute | RequireApproval | Deny  →  AuditRecord
```

**[ASSESSMENT]** Stages 1, 2, 4, 6, 7 and 8 contain no AI. That is deliberate and it is what makes the system auditable: the score is reproducible from a stored evidence bundle, the arithmetic is unit-testable, an agent's creativity cannot change a number, and no model sits on the path between a recommendation and an action.

## I.3 Agent contract

**[PROPOSAL]** One generic interface; structured input and output; no free text between components:

```
IAnalysisAgent<TInput, TOutput>
    AgentId, Version, PromptId, PromptVersion
    Task<AgentResult<TOutput>> AnalyzeAsync(TInput input, CancellationToken ct)

AgentResult<T>
├── Output      : T                 // JSON-schema validated
├── Evidence    : ClaimId[]         // what it actually used
├── Confidence  : Confidence
├── Limitations : string[]          // what it could NOT determine
├── Diagnostics { ModelId, PromptVersion, TokensIn/Out, Cost, LatencyMs, Attempts }
└── Status      : Ok | SchemaFailed | Ungrounded | Refused | ProviderError | BudgetExceeded
```

**[ASSESSMENT]** `Limitations` and a `Refused` status are not decoration. An agent that has no way to say "I don't know" will fill the gap, and a confidently invented margin figure is worse than a missing one — it is worse *because* it is indistinguishable from a real one downstream.

## I.4 Provider abstraction and safeguards

**[PROPOSAL]**

- **Abstract on `Microsoft.Extensions.AI` (`IChatClient`)** — provider-neutral, first-party, and it avoids adopting a heavy orchestration framework whose control flow you would then have to fight. Semantic Kernel is a reasonable later addition *if* agent routing genuinely becomes dynamic; it is not needed, and dynamic routing is itself a control risk.
- **Structured outputs only.** JSON schema enforced at the provider. Deserialization failure → bounded retry → `SchemaFailed`. **Never** a free-text fallback.
- **Temperature 0** for extraction and classification. Where higher temperature genuinely helps, run *n* samples and report the spread *as* the uncertainty rather than picking one.
- **Groundedness validator (Stage 4).** Every numeric figure in agent output must match a claim in the input bundle within tolerance, or the result is marked `Ungrounded` and excluded from scoring. This is the mechanical implementation of "never fabricate financial data".
- **Untrusted input.** News and filings are adversarial input flowing into a model. Evidence is delimited and labelled as untrusted data; agent output never triggers an action directly (§H.3); Stage 4 is the backstop.
- **Cost and latency budget per run and per cycle**, enforced by the orchestrator with a hard ceiling and a `BudgetExceeded` status. Spending money is itself an action (§H.3) and is therefore policy-gated.
- **Prompts are versioned files** in `prompts/`, referenced by `PromptId@version`, recorded in every audit record. A prompt change is a code change and goes through review — because a prompt change silently invalidates every historical comparison unless it is versioned.
- **Model pinning.** Pin model versions per agent and record them. An unannounced provider-side model change is otherwise indistinguishable from a strategy drift in your outcome data.

---

# J. Opportunity Architecture

**[ASSESSMENT]** The generalization risk here runs in both directions. Force stocks, suppliers and resale deals into one rigid table and every type is modelled badly. Give each type its own independent pipeline and there is no platform — just three applications sharing a logo.

**[PROPOSAL]** Split into **an invariant core** (shared lifecycle, economics, evidence, risk, actions) and **a typed extension** (per-type detail, discovery, economics calculation, evidence requirements).

```
Opportunity                          ← shared core aggregate, identical for every type
├── OpportunityId, Type, Source, DiscoveredAtUtc
├── Title, Description
├── Economics : OpportunityEconomics ← common shape, per-type calculator
│   ├── EstimatedCost      : Money
│   ├── EstimatedRevenue   : Money
│   ├── EstimatedProfit    : Money        (calculated, never AI-stated)
│   ├── Margin             : Percentage
│   ├── RequiredCapital    : Money
│   ├── TimeHorizon        : DateRange
│   └── RiskAdjustedReturn : Money        (calculated)
├── Risk       : RiskAssessment           (mandatory — cannot leave Draft without it)
├── Evidence   : Claim[]                  (≥1 required)
├── Confidence : Confidence
├── Score      : Score                    (deterministic, config-versioned)
├── Reversibility : ReversibilityClass    ← drives policy, see §L.2
├── Status     : Draft → Evaluated → Ranked → Proposed → Approved
│                      → Executing → Active → Closed | Rejected | Expired
├── Detail     : typed payload (JSONB), validated against the type's schema
└── Actions    : ActionProposal[]         ← the only way an opportunity affects the world
```

Per-type behaviour lives behind three interfaces, not in the core:

| Interface | Responsibility | Equity implementation | Resale implementation |
|---|---|---|---|
| `IOpportunityDiscoverer` | Find candidates | Screener query over fundamentals | Marketplace price-gap scan |
| `IEconomicsCalculator` | Compute the economics block **deterministically** | Position sizing, commission, spread, tax lot | Cost of goods, shipping, platform fees, returns rate |
| `IEvidenceRequirement` | Declare what must exist before the type may leave `Draft` | Financials + price history + ≥1 risk claim | Supplier verification + demand signal + landed-cost quote |

**[ASSESSMENT]** Why this passes Q-AUTO and Q-CTRL: adding "supplier opportunities" in year two means implementing three interfaces and registering a policy set for a new `Capability`. The lifecycle, approval flow, audit trail, capital ledger, dashboard and autonomy machinery are untouched — and the new type is subject to the same deterministic gate on day one rather than needing its own safety review. The `Detail` payload is deliberately schema-flexible (JSONB) because per-type detail is exactly the part that should not require a migration; everything the policy engine reads is in the strongly-typed core.

**[PROPOSAL]** One firm rule: **`EstimatedProfit` and `Margin` are always outputs of `IEconomicsCalculator`, never fields an agent can populate.** An agent may supply an input claim (an estimated sale price, with provenance and confidence); the arithmetic is the system's.

---

# K. Autonomous Operations Architecture

This section answers the brief's central question: how the platform moves from human-assisted operation to controlled autonomy without a rewrite, and without ever letting a model decide what it is allowed to do.

## K.1 What actually blocks autonomy today

**[ASSESSMENT]** Three structural facts cap the current system at Level 0–2 regardless of how good the agents become:

1. Nothing runs without an HTTP request (F-18) → the system cannot notice anything.
2. There is no action abstraction (F-16) → there is nothing to authorize, so autonomy would have to be implemented per call site.
3. There is no audit or outcome store (F-19) → autonomy could never be *earned*, because performance could never be measured.

Autonomy is therefore not an agent problem. It is a **triggers + actions + measurement** problem, and all three are infrastructure.

## K.2 Autonomy as deterministic configuration, never a model's judgement

**[PROPOSAL]** Autonomy is expressed as `AutonomyGrant` records evaluated by the `PolicyEngine`. The level is never global — it is resolved per action from five deterministic dimensions:

```
resolve(Capability, ActionType, RiskTier, ExposureBand, Environment) → AutonomyMode
```

| Level | Mode | Meaning |
|---|---|---|
| L0 | `Off` | Capability disabled entirely |
| L1 | `ResearchOnly` | May collect and analyse; produces no proposals |
| L2 | `Advise` | May produce recommendations; a human initiates everything |
| L3 | `PrepareForApproval` | Assembles complete, executable proposals; a human approves each |
| L4 | `AutoExecuteBounded` | Executes automatically **within** a named limit set; anything outside escalates |
| L5 | `ContinuousBounded` | Operates on its own schedule within policy; escalates only exceptions |

Non-negotiable rules:

- **The AI never reads, writes, proposes or influences its own grant.** `AutonomyGrant` is not in any agent's input or output schema. This is enforced by an architecture test, not by a prompt.
- **Resolution is total and fail-closed.** No grant found, grant expired, store unreadable, kill switch unknown → `RequireApproval` at minimum, `Deny` on the execution path. There is no "unknown → proceed" branch anywhere.
- **Grants expire.** An unattended capability must be re-granted on a cadence. Autonomy that never expires is autonomy nobody re-examines.
- **Grants are per-environment.** A grant in `Development` (simulated venue) carries no weight in `Production`. Environment is part of the key, not an ambient assumption.
- **Grants are earned and automatically revoked** — §K.4.

**[ASSESSMENT]** This is what makes "progressive autonomy without a rewrite" true rather than a slogan. Moving one capability from L3 to L4 is a row change plus a test, reviewed like any other change, and instantly reversible.

## K.3 The operating loop as a durable, resumable cycle

**[PROPOSAL]** The brief's DISCOVER → … → LEARN loop is implemented as a persisted state machine — an `OperatingCycle` aggregate — not as a long-running method and not as a chain of cron jobs.

```
                   ┌──────────────── Triggers ─────────────────┐
                   │  Scheduled  ·  Event  ·  Threshold  ·  Manual │
                   └─────────────────────┬─────────────────────┘
                                         ▼
   ┌─────────────────────────── OperatingCycle ────────────────────────────┐
   │  Discover → Collect → Validate → Analyze → Identify → Calculate       │
   │  → AssessRisk → Rank → ProposeAction → PolicyGate → Execute|Escalate  │
   │  → Monitor → MeasureOutcome → Record                                  │
   └───────────────────────────────────────┬───────────────────────────────┘
                                           ▼
                        Outcomes ──▶ Evaluation ──▶ (offline) Learning
                                           │
                                           └──▶ AutonomyGrant adjustment (K.4)
```

Design requirements, each with a reason:

- **Durable and resumable.** Every stage transition is persisted with its correlation ID. A crash mid-cycle resumes; it does not silently drop an in-flight opportunity or re-execute a completed action.
- **Every stage is independently replayable** from stored inputs. This is what makes an incident investigable.
- **The transactional outbox pattern** for anything leaving the process. A database commit and an external call must not be able to disagree about whether something happened.
- **Idempotency keys everywhere** (F-21). Retries are the normal case in an unattended system, not the exception.
- **Per-cycle budgets** — wall clock, LLM spend, provider calls, actions. Exceeding a budget suspends the cycle and escalates; it never silently truncates analysis and proceeds to a decision on partial evidence.
- **Backpressure and concurrency limits** per capability, so a market-wide event cannot cause the system to fan out into thousands of simultaneous cycles.

## K.4 Triggers — how the system notices without being asked

**[PROPOSAL]** A `Watch` abstraction is what removes "the human opens the dashboard and asks". It is deliberately **deterministic** — a model must not be the thing deciding whether something is worth waking up for, because that is both unreliable and unboundedly expensive.

```
Watch
├── Target        : Security | Sector | Opportunity | Portfolio | Supplier | …
├── TriggerType   : Schedule | PriceMove | VolumeSpike | NewFiling | NewsEvent
│                 | MetricThreshold | StaleData | OutcomeDue | PolicyBreach
├── Condition     : deterministic predicate (thresholds, windows, debounce)
├── Cooldown      : minimum interval between firings for this watch
├── Priority      : queue ordering under load
└── OnFire        : which OperatingCycle template to start
```

**[ASSESSMENT]** Debounce and cooldown are not polish. Without them, one volatile session produces a thousand cycles, a large LLM bill, and a flood of escalations that trains the operator to click through approvals without reading them — which is the most common way a human-in-the-loop control fails in practice.

## K.5 Escalation — what reaches the human, and how

**[PROPOSAL]** The human is not the operator of normal workflow; they are the authority on exceptions. An escalation is mandatory when **any** of the following is true — evaluated deterministically:

| Condition | Rationale |
|---|---|
| Risk tier ≥ High, or exposure above the configured band | Capital at risk |
| The action is **irreversible** (§L.2) | Reversibility, not size, is the real axis |
| Any limit would be breached | The limit *is* the decision |
| Confidence below the capability's threshold, or agents materially disagree | The system does not know |
| Evidence is stale, quarantined, or single-sourced where the type requires corroboration | The inputs are not trustworthy |
| A policy exception, provider failure, budget exhaustion or repeated retry occurs | Something is wrong |
| No `AutonomyGrant` resolves, or one has expired | Fail-closed |
| Novelty: the action falls outside the distribution the capability was granted for | Unknown territory is not routine |

Escalations carry the complete proposal, the evidence graph, the policy trace showing which rules fired, and the alternatives considered. **Approval expires** — an unanswered escalation is not a pending action indefinitely; a stale market context makes yesterday's approval a different decision than the human evaluated.

## K.6 Autonomy is earned and automatically revoked

**[PROPOSAL — the control that makes higher levels defensible]** A capability's autonomy level is a function of its *measured* record, and demotion is automatic:

- **Shadow mode first.** At L3, the system also records what it *would* have done at L4. After a defined volume of shadow decisions, compare shadow outcomes to actual. Promotion is a human decision informed by that number — never an automatic one.
- **Continuous evaluation.** Every capability has live quality metrics: approval-rate (how often a human agrees), outcome-hit-rate, confidence calibration error, groundedness failure rate, and policy-denial rate.
- **Automatic demotion — a circuit breaker on autonomy itself.** If any metric crosses its threshold, the grant drops one level automatically and an escalation is raised. This is deterministic code, it requires no human to be watching, and it is the difference between "the system degraded and someone eventually noticed" and "the system degraded and stopped acting."
- **Kill switch above everything** (§L.1) — global, per-capability, fail-closed.

## K.7 Learning — controlled, versioned, offline

**[PROPOSAL]** Brief §18 requires a feedback loop; brief §18 also forbids uncontrolled self-modification. Both are satisfied by making learning an **offline pipeline that produces a versioned artifact requiring human promotion**:

```
Outcomes ──▶ Evaluation ──▶ Candidate config / prompt / weights  (a versioned artifact)
                                   │
                                   ├─ Backtest on held-out period (point-in-time enforced)
                                   ├─ Shadow run against live cycles
                                   └─ Human promotion gate  ──▶ Champion (deployed, versioned)
                                                                    │
                                                          instant rollback to prior champion
```

Non-negotiables: production logic is **never** modified at runtime; every scoring config, prompt and weight set is versioned and referenced by every analysis that used it; every promotion is reversible in one step; and no artifact is promoted on in-sample performance. **[ASSESSMENT]** Overfitting is the failure mode here, and it is seductive precisely because it produces beautiful numbers. Hold-out periods and out-of-sample validation are not bureaucracy; they are the only thing standing between this project and a system that is confidently wrong.

---

# L. Risk & Safety Architecture

**[PROPOSAL]** Every control in this section is deterministic code. None may be delegated to a model. All are in `AI.Investment.Safety.Tests` (§O) and held to the highest test bar in the solution.

## L.1 Kill switch
A gate evaluated on **every** execution path, backed by a database flag **and** an environment variable. **Fail-closed**: if the gate cannot be read, execution is refused. Scoped globally, per-capability and per-environment. A kill-switch drill belongs in the test suite — a switch nobody has ever pulled is a switch of unknown state.

## L.2 Risk tiering by reversibility, not only by amount
**[ASSESSMENT]** Amount is the obvious axis and the incomplete one. A $50 irreversible action (a sent email, a placed order, a published listing, a signed supplier commitment) deserves more scrutiny than a $5,000 reversible one (a rebalance in a simulated account). `ReversibilityClass` — `Reversible | ReversibleWithCost | Irreversible` — is a mandatory input to tier computation, and tier is computed **deterministically** from `(Capability, ReversibilityClass, ExposureBand, Novelty)`. A model never assigns a risk tier to its own proposal.

## L.3 Limit engine
Pre-execution checks, server-side, evaluated in code: max position size, max total exposure, max daily loss, max drawdown, max actions per capability per day, max cost per cycle, instrument allow-list, concentration limits, and cooldown after a loss. Unit-tested and **mutation-tested** — mutation testing is how you demonstrate the guard rails cannot be bypassed rather than merely asserting it.

## L.4 Approval tokens
Single-use, expiring, scoped to one opportunity + action + amount, and **bound to a hash of the exact action the human saw**. A token cannot approve a larger, different or later action than the one presented. Consumed atomically with execution.

## L.5 Idempotency and replay safety
Every action carries an idempotency key. Replays are refused, not duplicated. Outbox for external effects. **[ASSESSMENT]** In an unattended system, "it retried and bought twice" is the single most likely way real money is lost first — ahead of any failure of analysis.

## L.6 Simulation first
`IExecutionVenue` with `SimulatedVenue` as the **only** registered implementation. A real venue is registered only after the §P Phase 7 gate is formally passed. Paper execution shares the entire code path — the same proposals, policies, tokens, ledger entries and audit records — so that switching venues changes one registration and nothing else. A simulation that takes a different path proves nothing about production.

## L.7 Plane separation
The analysis plane and the execution plane are separate processes with separate identities. Only `AI.Investment.Execution` holds venue credentials; it is reachable only through an authenticated internal API requiring a valid, unconsumed approval token, and it re-validates limits and the kill switch itself rather than trusting the caller. **The analysis plane cannot move money even if fully compromised, because it does not hold the capability.**

## L.8 Ledger integrity
Double-entry. Balances are projections of immutable entries. No settable balance field exists anywhere in the model.

## L.9 Mandatory uncertainty
Every recommendation payload carries a structured uncertainty field. A report **cannot be serialized without it** — enforced by the type system, not by a UI disclaimer that can be styled away.

## L.10 Non-technical risk
**[ASSESSMENT]** Two items belong on the risk register from today. First, this system is an experiment whose central hypothesis — that it produces useful analysis — is unproven and must be *measured* before any capital is committed; §P Phase 7 exists for exactly that. Second, if the platform is ever operated for anyone other than yourself, providing investment recommendations for compensation raises regulatory registration questions in most jurisdictions. That is a matter for a qualified lawyer rather than for this report, but it should be recorded now rather than discovered later. Nothing in this document is investment advice.

---

# M. Data Architecture

## M.1 Store
**[PROPOSAL]** **PostgreSQL 16 + EF Core 8.** Native `JSONB` for raw provider payloads, agent outputs and per-type opportunity detail (all schema-flexible by nature); strong time-series support; capable full-text search for news; `pgvector` available if embeddings are needed later; no licensing friction. SQL Server is a defensible alternative if it is already your operational standard — what matters is deciding now, because the migration story matters more than the engine.

## M.2 Schema families

| Family | Contents | Character |
|---|---|---|
| Reference | companies, securities, exchanges, sectors | Slowly changing, versioned, **delisted retained** |
| Market | prices, volumes, market cap | High-volume time series |
| Fundamentals | statements, metrics, ratios | **Bitemporal** — §M.3 |
| Documents | news, filings, transcripts (+ embeddings) | Append-only, deduplicated by content hash |
| Claims | every fact, calculation and interpretation | Append-only, immutable, provenance-carrying |
| Analysis | runs, evidence bundles, agent results, scores | Append-only |
| Decisions | opportunities, proposals, policy decisions, approvals, executions | Append-only, transitions recorded |
| Ledger | capital accounts, entries | Double-entry, immutable entries |
| Autonomy | grants, watches, cycles, budgets | Versioned, audited |
| Audit | every significant event | Append-only, hash-chained |
| Outcomes | recommendation vs. realized result | The measurement layer |

## M.3 The decision that determines whether validation is worth anything

**[ASSESSMENT — the most important paragraph in this report]** Every fundamental, news and filing record needs **three separate timestamps**: `AsOfUtc` (the period the data describes), `PublishedAtUtc` (when it became public), and `IngestedAtUtc` (when we fetched it). All historical queries — backtests, outcome measurement, shadow-mode comparison — must filter on `PublishedAtUtc`, never `AsOfUtc`.

If this is not designed into the first migration, the system will suffer **look-ahead bias**: a backtest of a January decision will silently use Q4 figures that were not published until March, and *every* strategy will appear profitable. This is the most common way projects of this kind produce a beautiful, meaningless track record, and it is nearly impossible to retrofit because by then the historical data has already been stored without the distinction.

**[PROPOSAL]** Two supporting rules with the same character:

- **Retain delisted and failed companies** in reference data. Excluding them produces survivorship bias — the second-most-common source of fake performance.
- **Store the raw provider response for every fetch**, keyed by content hash, so any analysis can be replayed byte-identically. This is also what makes an agent evaluation harness meaningful: you can re-run last month's analysis against a new prompt on *exactly* the evidence the old one saw.

## M.4 Ingestion and quality
**[PROPOSAL]** A provider gateway in front of every external API: a typed client per provider behind a common interface, rate-limit awareness, caching keyed by `(provider, endpoint, parameters, asOf)`, retry with jitter, circuit breaker, and the raw-response archive. Data-quality validators run at the boundary — range checks, staleness checks, unit and currency checks, cross-provider agreement checks — and a record that fails validation is **quarantined, not silently used**. Quarantine is itself an escalation condition (§K.5): in an unattended system, silently proceeding on bad data is the failure that produces confident nonsense.

---

# N. Security Architecture

1. **Secrets.** `dotnet user-secrets` in development; environment variables or a managed secret store in production; strongly-typed `IOptions<T>` with `ValidateOnStart()`. **No secret ever in `appsettings.json`.** Given F-05 — those files are tracked and a remote exists — add `.gitignore` entries for local override files, enable GitHub secret scanning and push protection, and add a secret-scanning step to CI **before the first API key exists**.
2. **Plane separation.** §L.7 — the structural control. Only the execution process holds venue credentials; separate identity, separate deployment, separate credential store, minimum necessary network reachability.
3. **AuthN / AuthZ.** Real authentication (OIDC/JWT), policy-based authorization, `[Authorize]` by default with explicit opt-out, and **step-up authentication on approval endpoints** — approving a capital action should not be reachable with the same session that reads a dashboard. This replaces the current no-op `UseAuthorization()` (F-03).
4. **API hardening.** Rate limiting, HSTS, request-size limits, a CORS allow-list, `ProblemDetails` responses that never leak internals, and API versioning.
5. **Audit integrity.** Append-only, hash-chained. **No update or delete path exists in the application at all** — not an unused one, not a guarded one. An audit trail the application can rewrite is not an audit trail.
6. **Untrusted input.** All external text — news, filings, marketplace listings, supplier pages — is adversarial. It is delimited and labelled as data on the way into a model; agent output never triggers an action (§H.3); agent output is never rendered as HTML without sanitization; and the groundedness validator is the backstop.
7. **Least privilege for the data plane.** The ingestion identity has no write access to decisions, ledger or audit. Compromising a news scraper must not put it a query away from the capital tables.
8. **Supply chain.** Central package management, lock files, `dotnet list package --vulnerable` and Dependabot in CI. Branch protection on `master` with required CI and review.
9. **Repository visibility.** **[FACT]** Visibility could not be verified from here (§C). **[PROPOSAL]** Confirm in the GitHub UI and keep the repository **private**. This is the one recommendation in this report that depends on the remote; nothing in the audit itself does.

---

# O. Testing Strategy

| Layer | Tooling | What it proves |
|---|---|---|
| Domain unit | xUnit, FluentAssertions | Invariants hold; value objects reject invalid input |
| Application unit | xUnit, NSubstitute | Orchestration, error paths, cycle state transitions |
| **Safety** | xUnit + **FsCheck** (property-based) + **Stryker.NET** (mutation) | Policy engine is total and fail-closed; limits cannot be bypassed; tokens are single-use and action-bound; kill switch refuses when unreadable |
| **Autonomy escape** | Adversarial tests in the Safety suite | **No agent output — including deliberately malicious output — can cause an action, raise its own autonomy, alter a policy, or bypass approval.** This suite is the executable form of the brief's central safety claim |
| Architecture | NetArchTest | Domain has no outward references; API uses no Infrastructure type outside composition; `AutonomyGrant` and `Policy` types appear in no agent input/output schema |
| Integration | Testcontainers (PostgreSQL) | Real migrations, real queries, real transactions, outbox behaviour |
| API | `WebApplicationFactory` | Contracts, status codes, authorization |
| Scoring | Golden-file / snapshot | A stored evidence bundle always produces an identical score |
| Idempotency & failure injection | Integration + chaos harness | Retries, duplicate deliveries, provider outages, mid-cycle crashes, partial failures |
| **Agent evaluation** | Custom harness, fixed evidence bundles | Schema validity, groundedness, stability across *n* runs, confidence calibration. Produces distributions and thresholds — **not** pass/fail on one run |
| **Backtesting** | Dedicated harness with point-in-time guard | Strategy performance — plus a test that *deliberately* attempts to read future data and **must fail** |
| Kill-switch drill | Integration | Every execution path refuses when the switch is on, and when its state cannot be determined |

**[ASSESSMENT]** Two suites here are unusual and both are the point of the exercise. **Autonomy-escape testing** is what converts "the AI cannot bypass the controls" from a design claim into a verified property; it should include prompt-injection payloads embedded in evidence, malformed and hostile agent outputs, and attempts to reference policy or grant objects. **Agent evaluation** is not unit testing and should not pretend to be — it belongs in its own project with its own cadence, and it gates promotion rather than merges.

---

# P. Development Roadmap

Each phase has an **exit criterion**. No phase begins until the previous one's criterion is met. Autonomy level is stated per phase and does not advance ahead of the measurement that justifies it.

| Phase | Objective | Key components | Depends on | Output | Principal risk | Exit criterion | Autonomy |
|---|---|---|---|---|---|---|---|
| **0. Foundation hygiene**<br>*1–2 days* | Make everything after this safe and reviewable | Remove template files (F-10) · fix namespace (F-08) · optional rename (F-09) · API→Infrastructure reference (F-02) · `Directory.Build.props` + `Directory.Packages.props` + `.editorconfig` + analyzers + warnings-as-errors (F-12) · Serilog + correlation IDs · `ProblemDetails` + global handler · health checks · typed config + user-secrets (F-05) · real `docs/` + `prompts/` (F-11) · CI with build, test, secret scan (F-20) | — | A clean, governed, CI-verified skeleton | Bikeshedding the rename | Builds clean with warnings-as-errors; `/health` returns 200; CI green; no secret can be committed without CI failing | L0 |
| **1. Domain core + the safety seam**<br>*1 week* | Establish the seam everything else attaches to | `BaseEntity` · `Ticker`/`Money`/`Percentage`/`Confidence` value objects · `Claim<T>` + provenance (§H.5) · `Company` aggregate with invariants (F-07) · **`ActionProposal` + `PolicyEngine` + `ActionExecutor` (§H.3)** · `AuditRecord` (append-only, hash-chained) · EF Core + first migration · one vertical slice (create/read/search companies) **routed through the Action seam** · unit + integration + API + architecture + safety tests | 0 | One feature works end to end, through the gate, with an audit trail | Treating the Action seam as ceremony and bypassing it "just this once" | The slice works with four levels of tests; architecture tests fail on a layering violation; **a test proves no path writes without a `PolicyDecision`** | L0 |
| **2. Data plane**<br>*2–3 weeks* | Trustworthy, replayable, point-in-time-correct data | Provider gateway · one market-data, one fundamentals, one news provider behind interfaces · raw-response archive · **bitemporal schema (§M.3)** · ingestion via the Worker host · quality validators + quarantine | 1 | 50 tickers with complete provenance | Provider licensing/cost; getting §M.3 wrong | 50 tickers ingested with full provenance, and any analysis replays byte-identically from stored raw responses | L1 |
| **3. Deterministic analytics**<br>*2 weeks — no AI* | A defensible number before any model touches it | Ratio calculations · health/growth/valuation metrics · `IEconomicsCalculator` for equities · scoring engine v1 (config-driven, versioned) · golden-file tests | 2 | Reproducible scores from stored bundles | Premature scoring-model complexity | A stored bundle reproduces an identical score; every score input is a traceable `Claim` | L1 |
| **4. AI layer**<br>*3–4 weeks* | Judgement on top of trustworthy data | `IChatClient` abstraction · agent contract (§I.3) · **three** agents: Financial, News, Risk · groundedness validator · synthesis agent · prompt versioning · full audit records · evaluation harness | 3 | Structured, grounded, audited analyses | Scope explosion to fifteen agents; ungrounded output | Evaluation harness meets agreed thresholds for schema validity, groundedness and stability. **Below threshold, the phase does not end** | L2 |
| **5. Opportunity, approval, capital**<br>*3–4 weeks* | The full decision path, simulated | Generic `Opportunity` (§J) · approval workflow + tokens · limit engine · kill switch · capital ledger · `SimulatedVenue` · dashboard read models · escalation UI | 4 | Recommendation → approval → simulated execution → outcome → audit, replayable | Approval fatigue in the UI design | A complete path replays end to end; safety suite green including mutation testing | L3 |
| **6. Continuous operation**<br>*2–3 weeks* | The system notices things on its own | `Watch` + triggers (§K.4) · `OperatingCycle` state machine · outbox · budgets, cooldowns, backpressure · `AutonomyGrant` + resolution · shadow mode · **autonomy-escape test suite** | 5 | Unattended research and monitoring; nothing executes automatically | Trigger storms; escalation flooding | Runs unattended for two weeks: no duplicate actions, no runaway cost, no unhandled escalation; shadow-mode data accumulating | L3 (+shadow L4) |
| **7. Validation**<br>*open-ended — the real test* | Find out whether any of this works | Backtesting with point-in-time guard · hit rate · calibration curves · false positives/negatives · comparison against a naive benchmark (e.g. index buy-and-hold) · shadow-vs-actual comparison | 6 | **A measured performance report** | That the honest answer is "no better than the benchmark" | A performance report exists and has been read | L3 |
| **8. Bounded autonomy**<br>*only if Phase 7 justifies it* | Automatic execution of the lowest-risk, reversible action classes | Per-capability grants at L4 · automatic demotion (§K.6) · execution plane hardening · live-venue gate (a formal, separate decision) | 7 | Bounded unattended operation | Everything in §L | A named, narrow capability runs at L4 for a defined period with zero policy breaches | L4 → L5 (per capability) |

**[ASSESSMENT]** Roughly three months of focused single-developer work to the end of Phase 5, and perhaps four to the end of Phase 6. Phase 7 is not estimable — it depends on what the data says, and the correct description of the system until Phase 7 produces a number is **"an untested hypothesis."** No real-money execution should be discussed before then.

**[ASSESSMENT]** Note what Phase 1 does that a conventional roadmap would not: it builds the policy gate *before there is anything dangerous to gate*, and routes a completely harmless use case through it. That is deliberate. A safety seam introduced when the first risky feature arrives is a safety seam introduced under schedule pressure, retrofitted to existing call sites, and untested in the paths that already exist.

---

# Q. Recommended First Implementation Phase

**[PROPOSAL]** Approve **Phase 0 plus Phase 1** — approximately **1.5 to 2 weeks**. Phase 0 alone is too small to be worth a review cycle now that source control exists, and Phase 1 is where the decision that shapes everything else gets made.

### Phase 0 — foundation hygiene (1–2 days)

1. Delete `WeatherForecast.cs`, `WeatherForecastController.cs`, and update `AI-Investment-API.http` *(F-10)*
2. Fix `Company`'s namespace to `AI_Investment_Domain.Entities` *(F-08)*
3. Add the API → Infrastructure project reference, for composition only *(F-02)*
4. `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, analyzers, `TreatWarningsAsErrors` *(F-12)*
5. Serilog + correlation IDs + `ProblemDetails` + global exception handler + `/health` *(F-14)*
6. Typed configuration with `ValidateOnStart()` + `UserSecretsId`; `.gitignore` entries for local override files *(F-05)*
7. Real `docs/` and `prompts/` directories; move both markdown documents into `docs/` *(F-11)*
8. `.github/workflows/ci.yml`: restore, build with warnings-as-errors, test, secret scan, `--vulnerable` check; branch protection on `master` *(F-20)*

**Exit:** builds clean with warnings-as-errors, `/health` returns 200, CI green, a committed secret fails the build.

### Phase 1 — domain core and the safety seam (1 week)

9. `BaseEntity`; `Ticker`, `Money`, `Percentage`, `Confidence` value objects
10. `Claim<T>` and the provenance primitives *(§H.5)*
11. `Company` as a real aggregate with enforced invariants *(F-07)*
12. **`ActionProposal`, `PolicyEngine`, `ActionExecutor`, `AuditRecord`** *(§H.3 — F-16, F-17, F-19, F-21)*
13. EF Core + first migration, with the three-timestamp shape present from migration one *(§M.3)*
14. One vertical slice — create / read / search companies — **routed through the Action seam**, even though nothing about it is dangerous
15. Test projects: domain unit, application unit, integration (Testcontainers), API, architecture, and the first safety tests

**Exit:** the slice works end to end with tests at four levels; architecture tests fail on a layering violation; and a test proves that **no write path executes without a `PolicyDecision`**.

### Decisions needed before Phase 1 starts

| # | Decision | Recommendation | Why it matters now |
|---|---|---|---|
| **D-1** | Rename projects to dotted form (`AI.Investment.Domain`) and move under `src/`? | **Yes** | Twenty minutes at four source files; a migration at three hundred *(F-09)* |
| **D-2** | PostgreSQL or SQL Server? | **PostgreSQL** — JSONB, time series, `pgvector`; SQL Server acceptable if it is your operational standard | The first migration encodes it |
| **D-3** | Vertical slices by feature, or layered services? | **Vertical slices** | The empty folders currently declare both *(F-13)* |
| **D-4** | Is the `Action`/`PolicyEngine` seam adopted in Phase 1, or deferred? | **Phase 1** | Deferring it means retrofitting every side-effecting path later *(F-16)* — this is the one decision in the list that is genuinely expensive to reverse |
| **D-5** | Which capability is the eventual L4 candidate? | Name it now, build toward it | It determines what Phase 6's shadow mode measures. A reversible, zero-capital action (refreshing data, updating a watchlist) is the right first candidate — not an order |

---

## Stop condition

**Nothing will be implemented until you approve.** No file in the repository has been modified by this audit; the only new file is this report.

One thing would be useful alongside your decision on D-1 through D-5: whether there is a hard budget ceiling for market data and LLM inference. That shapes Phase 2's provider choice more than any technical consideration.

**Sources for this audit.** Every finding above was read from the local working tree at `C:\Users\localadmin\Desktop\AI-Investment-Analyst`. Files read in full: `AI-Investment-Analyst.sln`; all four `.csproj`; `Program.cs`; `WeatherForecast.cs`; `WeatherForecastController.cs`; `Company.cs`; `appsettings.json`; `appsettings.Development.json`; `launchSettings.json`; `AI-Investment-API.http`; `AI-Investment-API.csproj.user`; `.gitignore`; `.gitattributes`; `SYSTEM_ARCHITECTURE.md`; `AUDIT_AND_TARGET_ARCHITECTURE.md`; and the git plumbing (`config`, `HEAD`, `logs/HEAD`, `refs`, `index`, `FETCH_HEAD`, `info/exclude`, `ms-persist.xml`). Directory structure was enumerated recursively to depth 5. **Not read, deliberately:** `bin/`, `obj/` and `.vs/` (compiler and IDE output — enumerated for evidence of the reference graph, but not source), and `.git/objects/` (compressed blobs of files already read in the working tree). **Nothing was inaccessible.** No remote repository was used as a source.

---

*This report describes an experimental research system. No component of the proposed architecture guarantees, or is capable of guaranteeing, investment returns. Profitability remains a hypothesis to be measured in Phase 7, not an assumption to be built upon. Nothing here is investment advice.*
