# AI Investment Analyst — Project Audit and Target Architecture Report

**Report ID:** AUD-001
**Date:** 2026-08-24
**Scope:** `C:\Users\localadmin\Desktop\AI-Investment-Analyst` — full solution, every source file, every project file, all documentation.
**Status:** Findings and proposal. **No code has been changed. Nothing has been deleted.**

---

## 0. How to read this report

Per the platform's own mandate to separate fact from interpretation, every statement below is tagged:

- **[FACT]** — read directly from the repository. Verifiable by opening the named file.
- **[ASSESSMENT]** — my engineering judgement about what a fact implies.
- **[PROPOSAL]** — a recommendation requiring your approval before any implementation.

---

## 1. Current State

### 1.1 What is physically on disk

**[FACT]** The solution `AI-Investment-Analyst.sln` contains four C# projects and two *virtual* solution folders:

| Item | Kind | GUID / SDK |
|---|---|---|
| `AI-Investment-API` | C# project, `Microsoft.NET.Sdk.Web` | `{B90910F3-…}` |
| `AI-Investment-Domain` | C# project, `Microsoft.NET.Sdk` | `{207B91D7-…}` |
| `AI-Investment-App` | C# project, `Microsoft.NET.Sdk` | `{95F597E3-…}` |
| `AI-Investment-Infrastructure` | C# project, `Microsoft.NET.Sdk` | `{716B0E0A-…}` |
| `Docs` | Solution folder (virtual) | contains `SYSTEM_ARCHITECTURE.md` as a solution item |
| `Prompts` | Solution folder (virtual) | **empty** |

**[FACT]** All four projects target `net8.0` with `Nullable=enable` and `ImplicitUsings=enable`.

### 1.2 The complete inventory of hand-written source

Excluding `bin/`, `obj/` and `.vs/`, the entire codebase is **six** source files:

| File | Lines (approx.) | What it is |
|---|---|---|
| `AI-Investment-API/Program.cs` | 24 | Unmodified `dotnet new webapi` host: controllers + Swagger + HTTPS redirect + `UseAuthorization` |
| `AI-Investment-API/WeatherForecast.cs` | 12 | Template scaffolding |
| `AI-Investment-API/Controllers/WeatherForecastController.cs` | 33 | Template scaffolding — **the only live endpoint in the system** |
| `AI-Investment-Domain/Entities/Company.cs` | 26 | `Company` POCO: `Id`, `Name`, `Ticker`, `Exchange`, `Sector`, `Industry`, `Country`, `Description`, `CreatedAtUtc`, `UpdatedAtUtc` |
| `SYSTEM_ARCHITECTURE.md` | 375 | Architecture intent document (repository root, not in a `Docs/` directory) |
| — | — | `AI-Investment-App` and `AI-Investment-Infrastructure` contain **zero** `.cs` files |

**[FACT]** `AI-Investment-App` declares eight empty folders via `<Folder Include>`: `Abstractions`, `Common`, `Companies`, `DTOs`, `Interfaces`, `Mappings`, `Services`, `Validators`.
**[FACT]** `AI-Investment-Domain` declares six empty folders: `Common`, `Enums`, `Events`, `Exceptions`, `Interfaces`, `ValueObjects`. Only `Entities/` holds a file.

### 1.3 Project reference graph as it exists today

```
AI-Investment-API  ──→  AI-Investment-App  ──→  AI-Investment-Domain
       └────────────────────────────────────────────→ (Domain)

AI-Investment-Infrastructure ──→ AI-Investment-App
                             └─→ AI-Investment-Domain

        (nothing references Infrastructure)
```

**[FACT]** Confirmed by build output: `AI-Investment-API/bin/Debug/net8.0/` contains `AI-Investment-API.dll`, `AI-Investment-App.dll`, `AI-Investment-Domain.dll`, Swashbuckle and OpenApi — and **no** `AI-Investment-Infrastructure.dll`.

### 1.4 Dependencies

**[FACT]** Exactly one NuGet package is referenced anywhere in the solution: `Swashbuckle.AspNetCore 6.6.2` (API project). No EF Core, no logging library, no validation library, no HTTP client abstraction, no AI SDK, no test framework.

### 1.5 Configuration

**[FACT]** `appsettings.json` and `appsettings.Development.json` contain only default logging levels and `AllowedHosts`. There is no connection string, no provider configuration, no feature flags, no `UserSecretsId` in any `.csproj`.

### 1.6 Documentation

**[FACT]** `SYSTEM_ARCHITECTURE.md` is a coherent, well-structured 20-section intent document. It is genuinely good: it states the human-in-the-loop constraint, the fact/interpretation separation, the provenance requirement, and the "profitability is a hypothesis" rule. **[ASSESSMENT]** It is the strongest asset in the repository. The gap is not vision — it is that no line of code yet implements any part of it.

### 1.7 Corrections to the brief

**[FACT]** Three statements in the initiating brief do not match the repository:

1. Projects are named `AI-Investment-API / -Domain / -App / -Infrastructure`, **not** `AI.Investment.API / .Application / .Domain / .Infrastructure`.
2. There is no `Docs` directory on disk. `Docs` is a Visual Studio solution folder holding a link to the root-level `SYSTEM_ARCHITECTURE.md`.
3. There is no `Prompts` directory on disk and no prompts exist. `Prompts` is an empty solution folder.

**[ASSESSMENT]** Worth stating plainly: the repository is a **clean scaffold**, not a partially-built system. Roughly one entity and one template controller stand against a specification describing thirteen AI agents, an opportunity engine, a capital ledger, an approval workflow and a backtesting harness. That is not a criticism — it is the single best moment to make the decisions that are expensive to reverse later.

---

## 2. Problems Found

Fifteen findings, ranked by severity. Each is evidence-backed and each has a concrete remedy.

### Critical

**F-01 — The project is not under version control.**
**[FACT]** There is no `.git` directory anywhere in the tree, and no `.gitignore`. `bin/`, `obj/` and `.vs/` — including `.suo` (30 KB of binary IDE state) and two SQLite Copilot index databases — sit beside the sources.
**[ASSESSMENT]** Every mistake from here is unrecoverable, no change is reviewable, and the first `git add .` would commit 2 MB of build output and IDE state. This is the highest-severity item in the report and the cheapest to fix.
**Remedy:** `git init`, a .NET `.gitignore`, initial commit — before anything else happens.

**F-02 — Infrastructure is unreachable; there is no composition root.**
**[FACT]** `AI-Investment-API.csproj` references `AI-Investment-App` and `AI-Investment-Domain` only. Nothing references `AI-Investment-Infrastructure`, and its assembly is absent from the API's build output (§1.3).
**[ASSESSMENT]** Whatever is written in Infrastructure — a `DbContext`, a market-data client, an AI provider — cannot be registered in the API's DI container. The layer is currently dead weight.
**Remedy:** API references Infrastructure **for composition only**, with an analyzer or architecture test forbidding API code from using Infrastructure types directly.

### High

**F-03 — Security middleware that looks present but is not.**
**[FACT]** `Program.cs` calls `app.UseAuthorization()` with no authentication scheme registered, no authorization policies, no `[Authorize]` attributes. There is no CORS policy, no rate limiting, no HSTS, no request-size limits.
**[ASSESSMENT]** `UseAuthorization()` without authentication is a no-op that reads as security in a code review. For a system that will eventually hold broker credentials and capital state, an unauthenticated API is the first thing to fix after source control.

**F-04 — No persistence layer at all.**
**[FACT]** No EF Core, no `DbContext`, no migrations, no connection string, no repository abstraction.
**[ASSESSMENT]** The architecture document's §12 (provenance) and §13 (audit) requirements have nowhere to land. More importantly, the *shape* of the schema — specifically point-in-time correctness, §6.3 below — is the decision most likely to invalidate the entire backtesting effort if made carelessly.

**F-05 — No secrets mechanism exists.**
**[FACT]** No `UserSecretsId` in any project, no typed configuration classes, no environment-variable convention, no secret store integration.
**[ASSESSMENT]** Nothing leaks *today* because there are no secrets yet. The risk is precisely that: the first market-data API key acquired will, by default, land in `appsettings.json` and then in the first commit. Establish the mechanism before the first key exists.

**F-06 — No test project, and no seam to test against.**
**[FACT]** Zero test projects. No interfaces, no dependency injection of services, nothing mockable.
**[ASSESSMENT]** Requirements §17 (historical validation) and §26 (testing) are not merely unimplemented — they are currently unimplementable. Testability is an architectural property, and it has to be designed in at the point where the first service is written.

**F-07 — The domain model is anemic and enforces no invariants.**
**[FACT]** `Company` is a public-getter/public-setter POCO. `Ticker` is a bare `string`. There is no base entity, no value objects, no domain events (despite an `Events/` folder), no validation.
**[ASSESSMENT]** `new Company { Ticker = "" }` is valid today, as is `Ticker = "this is not a ticker"`. The brief calls for "strong typing" and "deterministic business rules"; a settable-string domain is the opposite. In a financial system, an unconstrained `decimal` for money and an unconstrained `string` for an instrument identifier are the two classic sources of silent, expensive errors.

### Medium

**F-08 — Namespace does not match folder.**
**[FACT]** `Entities/Company.cs` declares `namespace AI_Investment_Domain.Entity` — singular, and omitting the `Entities` folder segment.
**Remedy:** rename to `…Domain.Entities`. Trivial now, tedious after 200 files.

**F-09 — Project naming has already drifted from the documented target.**
**[FACT]** `SYSTEM_ARCHITECTURE.md` §17 specifies `AI.Investment.API / .Domain / .Application / .Infrastructure`. On disk they are hyphenated, forcing `<RootNamespace>AI_Investment_Domain</RootNamespace>` because hyphens are illegal in C# identifiers.
**[ASSESSMENT]** Underscored namespaces are non-idiomatic and will appear in every `using` statement in the system forever. Renaming four projects with six source files between them costs perhaps twenty minutes; renaming them at 300 files is a genuine migration.
**[PROPOSAL]** Rename to dotted form now. This is a change to existing structure and therefore requires explicit approval — see §13.

**F-10 — Template scaffolding is still the only functioning endpoint.**
**[FACT]** `WeatherForecast.cs` and `WeatherForecastController.cs` remain. `GET /WeatherForecast` returns random temperatures.
**Remedy:** delete both, replaced by a real health endpoint in Phase 0.

**F-11 — `Docs` and `Prompts` do not exist as directories.**
**[FACT]** Both are virtual solution folders; `Prompts` is empty; `SYSTEM_ARCHITECTURE.md` lives at the repository root.
**[ASSESSMENT]** Prompts must be real, versioned files on disk — they are production artifacts whose version has to be recorded in every audit record (§7.4). A virtual folder cannot hold them.

**F-12 — No build governance.**
**[FACT]** No `Directory.Build.props`, no `Directory.Packages.props`, no `.editorconfig`, no analyzer packages, no `TreatWarningsAsErrors`. `Nullable` is enabled per-project but nothing enforces that it stays enabled in project five, six and seven.

**F-13 — The Application layer pre-declares two competing organizing conventions.**
**[FACT]** Empty folders include both feature-based (`Companies/`) and layer-based (`Services/`, `DTOs/`, `Mappings/`, `Validators/`) organization.
**[ASSESSMENT]** Both work; the mix does not. Decide before code arrives. My recommendation is vertical slices by feature — it scales better as the number of use cases grows and it keeps a change to one workflow inside one folder.

**F-14 — No observability substrate.**
**[FACT]** Default console logging only. No structured logging, no correlation IDs, no health checks, no `ProblemDetails`, no metrics, no tracing, no global exception handler.
**[ASSESSMENT]** §13 of the architecture document requires recording prompts, model metadata, sources, timings and errors for every analysis. That is an audit *store*, not a log file — but it needs correlation identifiers flowing through the whole pipeline from the first request onward, and retrofitting those is painful.

**F-15 — The AI subsystem is entirely absent.**
**[FACT]** No AI project, no provider abstraction, no agent interface, no prompt store, no structured-output contracts.
**[ASSESSMENT]** Expected at this stage. Recorded so the gap is explicit rather than assumed.

---

## 3. Architecture Assessment

**[ASSESSMENT]** The instinct behind the layout is right, and two things in particular are worth preserving:

- **The Domain project has zero project references.** That is the single most important rule in Clean Architecture and it is currently satisfied. Protect it with an architecture test, not with discipline.
- **`Application → Domain` only.** Also correct.

Two deviations, both cheap to fix at this size: Infrastructure is orphaned (F-02), and the API's reference to Infrastructure is missing (needed strictly for the composition root).

**[ASSESSMENT]** The deeper assessment is about *sequence*, not structure. The brief describes an autonomous multi-agent investment platform. The repository is at the point where a single company record cannot yet be saved. The risk is not that the architecture is wrong — it is that a system this ambitious, built in this order, tends to acquire thirteen AI agents before it acquires a trustworthy number. Every recommendation in §10 is arranged to invert that: **deterministic, sourced, point-in-time data first; AI reasoning on top of it second.**

**Maturity read:** Phase 0 of 6 in the project's own roadmap. Foundation is not yet complete.

---

## 4. Proposed Target Architecture

### 4.1 Solution layout

```
AI-Investment-Analyst.sln
├── src/
│   ├── AI.Investment.Domain            (no project references — enforced by test)
│   ├── AI.Investment.Application       → Domain
│   ├── AI.Investment.Infrastructure    → Application, Domain    (EF Core, provider adapters)
│   ├── AI.Investment.Agents            → Application, Domain    (AI agents, prompts, contracts)
│   ├── AI.Investment.Api               → Application, Infrastructure, Agents  (composition root only)
│   └── AI.Investment.Worker            → Application, Infrastructure, Agents  (ingestion, monitoring)
├── tests/
│   ├── AI.Investment.Domain.UnitTests
│   ├── AI.Investment.Application.UnitTests
│   ├── AI.Investment.Integration.Tests      (Testcontainers, real DB)
│   ├── AI.Investment.Api.Tests              (WebApplicationFactory)
│   ├── AI.Investment.Agents.Evaluation      (AI eval harness — not a unit-test suite)
│   └── AI.Investment.Architecture.Tests     (layering rules as executable assertions)
├── docs/          (real directory)
├── prompts/       (real directory, versioned)
└── .github/workflows/   (CI)
```

**[PROPOSAL]** Two new projects beyond the documented six: `Agents` (separated from Infrastructure because AI providers have fundamentally different failure, cost and testing characteristics from a SQL connection) and `Worker` (because continuous monitoring must not live inside a request-scoped web host).

**[PROPOSAL]** `src/` and `tests/` directories. Purely organizational, and much easier now than at twelve projects.

### 4.2 Modules inside the layers

Rather than one flat Application project, organize by bounded context. Each is a folder in Phase 1–3 and can become its own project only if it earns it:

`ReferenceData` · `Ingestion` · `Fundamentals` · `News` · `Analysis` · `Scoring` · `Opportunity` · `Risk` · `Approval` · `Capital` · `Execution` · `Audit` · `Evaluation`

### 4.3 The primitive that makes the brief's §28 real

**[PROPOSAL]** The requirement to distinguish **FACT / CALCULATION / AI INTERPRETATION / PREDICTION / UNCERTAINTY** should be a first-class domain type, not a UI convention or a documentation promise. Every number that reaches a report carries its own epistemic status:

```
Claim<T>
├── Value : T
├── Kind : ClaimKind          // Fact | Calculation | AiInterpretation | Prediction
├── Provenance
│   ├── SourceId              // which provider / filing / article
│   ├── SourceUrl
│   ├── AsOfUtc               // the date the fact is ABOUT
│   ├── PublishedAtUtc        // the date it became public knowledge
│   └── RetrievedAtUtc        // the date we fetched it
├── DerivedFrom : ClaimId[]   // for Calculation and AiInterpretation
├── Confidence : Confidence?  // required for AiInterpretation and Prediction, forbidden for Fact
└── Caveats : string[]
```

**[ASSESSMENT]** This single type is what turns "we will separate facts from interpretation" from an aspiration into something the compiler and the test suite enforce. A report is then literally a graph of claims, and "show me why you said this" is a traversal rather than a prose explanation. It also makes the fabrication guard in §5.4 mechanically checkable.

### 4.4 Core aggregates

| Aggregate | Purpose | Key invariants |
|---|---|---|
| `Company` / `Security` | Reference data | `Ticker` value object; exchange required for listed securities |
| `AnalysisRun` | One execution of the pipeline | Immutable once complete; records input snapshot hash, every agent output, model IDs, prompt versions, cost, duration |
| `Opportunity` | Generic per brief §10 | Typed by `OpportunityType`; must carry ≥1 evidence claim and a risk assessment |
| `ApprovalRequest` | Human gate | State machine `Draft → Pending → Approved \| Rejected \| Expired`; immutable after decision; bound to an action hash |
| `RiskPolicy` / `Limit` | Deterministic guard rails | Evaluated in code, never by an agent |
| `CapitalAccount` | Ledger | Double-entry; balance is a projection of entries, never a settable field |
| `Recommendation` + `Outcome` | Historical validation | Price and score frozen at recommendation time |

**[PROPOSAL]** Value objects from day one: `Ticker`, `Money` (amount + currency, no implicit conversion), `Percentage`, `Confidence` (0–1 plus calibration bucket), `DateRange`, `Exchange`.

---

## 5. Proposed AI Agent Architecture

### 5.1 The orchestrator is C#, not a model

**[PROPOSAL]** Control flow is deterministic code. Agents are called by an explicit pipeline; no agent decides what runs next, and no agent has tool access with side effects. Agent output is **data**, never instructions.

```
AnalysisRequest(ticker)
   │
   ├─ Stage 1  Evidence assembly        (deterministic — no LLM)
   │            prices, fundamentals, filings, news → EvidenceBundle (immutable, hashed)
   │
   ├─ Stage 2  Deterministic analytics  (deterministic — no LLM)
   │            ratios, growth rates, health metrics → Claim<Calculation>[]
   │
   ├─ Stage 3  Specialist agents        (parallel fan-out, LLM)
   │            Financial · Valuation · Growth · News · Competitive · Risk
   │            each: EvidenceBundle → typed AgentResult (JSON-schema constrained)
   │
   ├─ Stage 4  Groundedness validation  (deterministic — no LLM)
   │            reject any figure not traceable to the bundle
   │
   ├─ Stage 5  Synthesis agent          (LLM, sees only validated stage-3 output)
   │
   ├─ Stage 6  Scoring                  (deterministic — config-driven weights)
   │
   └─ Stage 7  Report assembly + audit record
```

**[ASSESSMENT]** Stages 1, 2, 4, 6 contain no AI. That is deliberate: it means the score is reproducible from a stored evidence bundle, the arithmetic is unit-testable, and an agent's creativity cannot change a number.

### 5.2 Agent contract

**[PROPOSAL]** One generic interface, structured input and output, no free text between components:

```
IAnalysisAgent<TInput, TOutput>
    AgentId, Version, PromptId, PromptVersion
    Task<AgentResult<TOutput>> AnalyzeAsync(TInput input, CancellationToken ct)

AgentResult<T>
├── Output : T                  // schema-validated
├── Evidence : ClaimId[]        // what it actually used
├── Confidence : Confidence
├── Limitations : string[]      // what it could not determine
├── Diagnostics { ModelId, PromptVersion, TokensIn/Out, Cost, LatencyMs, Attempts }
└── Status : Ok | SchemaFailed | Ungrounded | Refused | ProviderError
```

**[ASSESSMENT]** `Limitations` and a `Refused` status are not decoration. An agent that cannot say "I don't know" will fill the gap, and in a financial system a confidently invented margin figure is worse than no figure.

### 5.3 Which agents, and in what order

Do not build thirteen. Build three, prove the harness, then add.

| Wave | Agents | Rationale |
|---|---|---|
| First | Financial Analyst, News Intelligence, Risk Analyst | Covers structured data, unstructured text, and the mandatory risk output. Exercises every part of the contract. |
| Second | Valuation, Growth, Competitive | Added only after the first wave passes evaluation thresholds. |
| Third | Synthesis / Decision, Opportunity Discovery, Monitoring | Synthesis is meaningless until there are enough specialists to synthesize. |
| Later | Profitability, Approval, Learning | Approval and Profitability should be **deterministic services, not agents** — they compute and route, they do not reason. |

**[ASSESSMENT]** The brief lists "Approval Agent" and "Profitability Agent". I would push back on both: an approval request is a structured record assembled from an opportunity, and profitability is arithmetic. Making them agents introduces non-determinism where the system most needs determinism.

### 5.4 Provider abstraction and safeguards

**[PROPOSAL]**
- Abstract on `Microsoft.Extensions.AI` (`IChatClient`) — provider-neutral, first-party, and avoids adopting a heavy orchestration framework whose control flow you would then have to fight. Semantic Kernel is a reasonable later addition if agent routing genuinely becomes dynamic; it is not needed now.
- **Structured outputs only.** JSON schema enforced at the provider; deserialization failure is a retry, then a `SchemaFailed` status — never a free-text fallback.
- **Temperature 0** for extraction and classification. Where a task genuinely benefits from higher temperature, run *n* samples and report the spread as uncertainty.
- **Prompt injection is a live threat.** News articles and filings are untrusted input flowing into a model. Evidence is always delimited and labelled as untrusted data; agent output never triggers an action; the groundedness validator (Stage 4) is the backstop.
- **Groundedness validator:** every numeric figure in an agent's output must match a claim in the input bundle within tolerance, or the result is marked `Ungrounded` and excluded from scoring. This is the mechanical implementation of "never fabricate financial data".
- **Cost and latency budget per run**, enforced by the orchestrator, with a hard ceiling.
- **Prompts are versioned files** in `prompts/`, referenced by `PromptId@version`, recorded in every audit record. A prompt change is a code change and goes through review.

---

## 6. Data Architecture

### 6.1 Store

**[PROPOSAL]** **PostgreSQL 16 + EF Core 8.** Rationale: native `JSONB` for raw provider payloads and agent outputs (which are schema-flexible by nature), strong time-series support, generous full-text search for news, and no licensing friction for the many environments this will run in. SQL Server is a defensible alternative if it is already your operational standard — the important thing is to decide now, since the migration story matters more than the engine.

### 6.2 Schema families

| Family | Contents | Character |
|---|---|---|
| Reference | companies, securities, exchanges, sectors | Slowly changing, versioned |
| Market | prices, volumes, market cap | High-volume time series |
| Fundamentals | statements, metrics, ratios | **Bitemporal** — see §6.3 |
| Documents | news, filings, transcripts + embeddings | Append-only, deduplicated by content hash |
| Analysis | runs, agent results, claims, scores | Append-only |
| Decisions | opportunities, approvals, executions | Append-only, state transitions recorded |
| Ledger | capital accounts, entries | Double-entry, immutable entries |
| Audit | every significant event | Append-only, hash-chained |
| Outcomes | recommendation vs. realized result | The measurement layer |

### 6.3 The decision that determines whether backtesting is worth anything

**[ASSESSMENT — the most important paragraph in this report]** Every fundamental and news record needs three separate timestamps: `AsOfUtc` (the period the data describes), `PublishedAtUtc` (when it became public), and `IngestedAtUtc` (when we fetched it). Backtests must query on `PublishedAtUtc`, never `AsOfUtc`.

If this is not designed in from the first migration, the system will suffer **look-ahead bias**: a backtest of a January decision will silently use Q4 figures that were not published until March, and every strategy will appear profitable. This is the single most common way projects of this kind produce a beautiful, meaningless track record — and it is nearly impossible to retrofit because the historical data has already been stored without the distinction.

**[PROPOSAL]** Two supporting rules: keep **delisted and failed companies** in the reference data (excluding them produces survivorship bias, the second-most-common source of fake performance), and store the **raw provider response** for every fetch, keyed by hash, so any analysis can be replayed exactly.

### 6.4 Ingestion

**[PROPOSAL]** A provider gateway sitting in front of every external API: typed client per provider behind a common interface, rate-limit awareness, caching keyed by (provider, endpoint, parameters, date), retry with jitter, circuit breaker, and a raw-response archive. Data quality validators run at the boundary — range checks, staleness checks, cross-provider agreement checks — and a record that fails validation is quarantined, not silently used.

---

## 7. Security Architecture

**[PROPOSAL]**

1. **Secrets.** `dotnet user-secrets` in development; environment variables or a managed secret store in production; strongly-typed `IOptions<T>` with `ValidateOnStart()`. No secret ever in `appsettings.json`. Add secret scanning to CI before the first API key exists.
2. **Plane separation.** Analysis-plane and execution-plane run as separate processes with separate identities. Only the execution service holds broker credentials, and it is reachable only through an authenticated internal API that requires a valid, single-use approval token. The analysis plane cannot move money even if fully compromised.
3. **AuthN/AuthZ.** Real authentication (OIDC/JWT), policy-based authorization, and `[Authorize]` by default with explicit opt-out. Approval endpoints require step-up authentication.
4. **API hardening.** Rate limiting, HSTS, request-size limits, CORS allow-list, `ProblemDetails` error responses that never leak internals, API versioning.
5. **Audit integrity.** Append-only, hash-chained, no update or delete path in the application at all.
6. **Untrusted input.** Treat all external text as untrusted (§5.4). Never render agent output as HTML without sanitization.
7. **Supply chain.** Central package management, lock files, `dotnet list package --vulnerable` in CI.

---

## 8. Financial Safety Architecture

**[PROPOSAL]** Every control here is deterministic code. None of it may be delegated to a model.

| Control | Mechanism |
|---|---|
| **Kill switch** | A gate evaluated on every execution path, backed by database flag **and** environment variable. **Fail-closed**: if the gate cannot be read, execution is refused. |
| **Limit engine** | Pre-trade checks: max position size, max total exposure, max daily loss, max orders per day, instrument allow-list. Server-side, unit-tested, mutation-tested. |
| **Approval tokens** | Single-use, expiring, scoped to one opportunity + action + amount, bound to a hash of the exact action. A token cannot approve a different or larger action than the one the human saw. |
| **Idempotency** | Every execution carries an idempotency key. Replays are refused, not duplicated. |
| **Simulation first** | `IExecutionVenue` with `SimulatedVenue` as the **only** registered implementation. A real venue is added only after a formal gate (§10, Phase 6) is passed. |
| **Ledger integrity** | Double-entry. Balances are projections of immutable entries. No settable balance field exists. |
| **Mandatory uncertainty** | Every recommendation payload carries a structured uncertainty field. A report cannot be serialized without it — enforced by the type system, not by a UI disclaimer. |

**[ASSESSMENT]** Two non-technical risks belong in this section. First, this system is an experiment whose central hypothesis — that it produces useful analysis — is unproven and must be measured before any capital is committed; §10 Phase 6 exists for exactly that. Second, if the platform is ever operated for anyone other than yourself, providing investment recommendations for compensation triggers regulatory registration questions in most jurisdictions. That is a matter for a qualified lawyer, not for me, but it should be on the risk register from now rather than discovered later.

---

## 9. Testing Strategy

| Layer | Tooling | What it proves |
|---|---|---|
| Domain unit | xUnit, FluentAssertions | Invariants hold; value objects reject bad input |
| Application unit | xUnit, NSubstitute | Orchestration logic, error paths |
| Architecture | NetArchTest | Domain has no outward references; API does not use Infrastructure types |
| Integration | Testcontainers (PostgreSQL) | Real migrations, real queries, real transactions |
| API | `WebApplicationFactory` | Contracts, status codes, auth |
| Scoring | Golden-file / snapshot tests | A stored evidence bundle always produces the same score |
| Limit engine | Property-based (FsCheck) + Stryker.NET mutation testing | The guard rails cannot be bypassed |
| **Agent evaluation** | Custom harness, fixed evidence bundles | Schema validity, groundedness, stability across *n* runs, confidence calibration |
| **Backtesting** | Dedicated harness with a point-in-time guard | Strategy performance — and a test that *deliberately* attempts to read future data and must fail |

**[ASSESSMENT]** Agent evaluation is not unit testing and should not pretend to be. It produces distributions and thresholds, not pass/fail on a single run, and it belongs in its own project with its own cadence.

---

## 10. Development Roadmap

Each phase has an **exit criterion**. No phase begins until the previous one's criterion is met.

### Phase 0 — Foundation hygiene *(1–2 days)*
Source control + `.gitignore` (F-01) · remove template files (F-10) · fix namespace (F-08) · optional project rename (F-09) · API → Infrastructure reference (F-02) · `Directory.Build.props` + `Directory.Packages.props` + `.editorconfig` + analyzers + warnings-as-errors (F-12) · Serilog structured logging + correlation IDs · `ProblemDetails` + global exception handler · health checks · typed configuration + user-secrets (F-05) · real `docs/` and `prompts/` directories (F-11) · CI pipeline that builds and tests.
**Exit:** solution is in git, builds clean with warnings-as-errors, `/health` returns 200, CI green.

### Phase 1 — Domain core and first vertical slice *(1 week)*
`BaseEntity`, `Ticker` / `Money` / `Percentage` / `Confidence` value objects, `Claim<T>` and provenance primitives (§4.3), `Company` aggregate with invariants (F-07) · EF Core + first migration · repository abstractions · one end-to-end slice: create/read/search companies · unit + integration + API tests (F-06) · architecture tests.
**Exit:** one feature works end to end with three levels of test coverage, and the architecture tests fail if the layering is violated.

### Phase 2 — Data plane *(2–3 weeks)*
Provider gateway · one market-data, one fundamentals and one news provider behind interfaces · raw-response archive · **bitemporal schema (§6.3)** · ingestion worker · data-quality validators and quarantine.
**Exit:** 50 tickers ingested with complete provenance, and any analysis can be replayed byte-identically from stored raw responses.

### Phase 3 — Deterministic analytics *(2 weeks — no AI)*
Financial ratio calculations · health, growth and valuation metrics · scoring engine v1 with configuration-driven weights · golden-file tests.
**Exit:** a stored evidence bundle reproduces the identical score, and every input to the score is a traceable `Claim`.

### Phase 4 — AI layer *(3–4 weeks)*
`IChatClient` abstraction · agent contract (§5.2) · Financial, News and Risk agents · groundedness validator · synthesis agent · prompt versioning · full audit records · evaluation harness.
**Exit:** evaluation harness meets agreed thresholds for schema validity, groundedness and stability. **Below threshold, the phase does not end.**

### Phase 5 — Opportunity, approval, audit, dashboard *(3–4 weeks)*
Generic `Opportunity` model · approval workflow with tokens · limit engine · kill switch · capital ledger · simulated execution venue · dashboard read models.
**Exit:** a full recommendation → approval → simulated execution → outcome → audit trail, replayable end to end.

### Phase 6 — Validation *(open-ended — this is the real test)*
Backtesting with the point-in-time guard · hit rate, calibration curves, false positives and negatives · comparison against a naive benchmark.
**Exit:** a measured performance report. **[ASSESSMENT]** Until this exists, the correct description of the system is "an untested hypothesis." No automated execution should be discussed before this phase produces a number.

**Estimated elapsed time to end of Phase 5:** roughly 3 months of focused single-developer work. Phase 6 is not estimable — it depends on what the data says.

---

## 11. Dependencies

**[PROPOSAL]** Packages, each with a justification. Nothing is added because it is popular.

| Package | Phase | Why |
|---|---|---|
| Serilog + sinks | 0 | Structured logging; the audit trail's foundation |
| FluentValidation | 1 | Declarative validation at the application boundary |
| EF Core + Npgsql | 1 | ORM + PostgreSQL provider |
| MediatR *(optional)* | 1 | Only if vertical slices are adopted; otherwise skip — it is not free complexity |
| Polly | 2 | Retry, circuit breaker for external providers |
| `Microsoft.Extensions.AI` | 4 | Provider-neutral LLM abstraction |
| Quartz.NET or Hangfire | 2 | Scheduled ingestion and monitoring |
| xUnit, FluentAssertions, NSubstitute, Testcontainers, NetArchTest, Verify | 1 | Test stack |
| Stryker.NET, FsCheck | 5 | Mutation and property testing for the limit engine only |

**External services requiring a decision:**

| Need | Notes |
|---|---|
| Market data | Cost and **redistribution licensing** matter. Free tiers are generally unusable for backtesting because they lack point-in-time history. |
| Fundamentals | SEC EDGAR is free and authoritative but requires parsing work; commercial providers cost money but save weeks. |
| News | Licensing terms vary widely on whether content may be stored and processed by a model. Read them. |
| AI provider | Choose the abstraction now, the provider late. |

**[ASSESSMENT]** The market-data provider is the decision most likely to constrain the project, and it should be made in Phase 2 — not Phase 4 — because the point-in-time question (§6.3) determines whether Phase 6 is possible at all.

---

## 12. Risks

| # | Risk | Impact | Likelihood | Mitigation |
|---|---|---|---|---|
| R-01 | Look-ahead bias invalidates all backtests | Critical | High if unaddressed | Bitemporal schema from the first migration (§6.3); a test that must fail when future data is read |
| R-02 | Working without source control | Critical | Certain today | Phase 0, first task |
| R-03 | Agents fabricate financial figures | Critical | Moderate | Groundedness validator (§5.4); structured outputs; `Ungrounded` status excluded from scoring |
| R-04 | Survivorship bias inflates measured performance | High | High if unaddressed | Retain delisted companies in reference data |
| R-05 | Scope explosion — thirteen agents before one trustworthy number | High | High | Phase gates with hard exit criteria; three agents in wave one |
| R-06 | Overfitting the scoring model to history | High | Moderate | Hold-out periods; out-of-sample validation; version and freeze scoring configs |
| R-07 | Market-data licensing or cost blocks the project | High | Moderate | Decide the provider in Phase 2, before building on top of it |
| R-08 | LLM inference cost grows faster than value | Medium | Moderate | Per-run budget ceiling; deterministic stages carry the arithmetic; cache aggressively |
| R-09 | Prompt injection via news or filings | High | Moderate | Untrusted-input handling; agent output is data, never instruction |
| R-10 | Regulatory exposure if operated for others | High | Low now, rises with use | Legal review before any third party uses it |
| R-11 | Single-developer bus factor | Medium | High | Documentation discipline; CI; nothing that lives only in one person's head |
| R-12 | Credentials compromise enables real financial loss | Critical | Low | Plane separation (§7.2); simulation-only venue; kill switch |

---

## 13. Recommended Next Step

**[PROPOSAL]** Approve **Phase 0 only** — roughly one to two days of work, no new architecture, no new abstractions, and it makes everything after it safe and reviewable. Concretely:

1. `git init` + `.gitignore` + initial commit *(F-01)*
2. Delete `WeatherForecast.cs` and `WeatherForecastController.cs` *(F-10)*
3. Fix `Company`'s namespace to `AI_Investment_Domain.Entities` *(F-08)*
4. Add the API → Infrastructure project reference *(F-02)*
5. Add `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, analyzers, warnings-as-errors *(F-12)*
6. Serilog + correlation IDs + `ProblemDetails` + global exception handler + `/health` *(F-14)*
7. Typed configuration with `ValidateOnStart` + `UserSecretsId` *(F-05)*
8. Create real `docs/` and `prompts/` directories; move `SYSTEM_ARCHITECTURE.md` into `docs/` *(F-11)*
9. A CI workflow that restores, builds with warnings-as-errors, and runs tests

**Three decisions are needed before Phase 1 can start:**

| # | Decision | My recommendation |
|---|---|---|
| D-1 | Rename projects to dotted form (`AI.Investment.Domain`) and move under `src/`? | **Yes** — twenty minutes now, a migration later *(F-09)* |
| D-2 | PostgreSQL or SQL Server? | **PostgreSQL** for JSONB and time-series; SQL Server acceptable if it is your operational standard |
| D-3 | Vertical slices by feature, or layered services? | **Vertical slices** — `Companies/` already hints at it *(F-13)* |

**Nothing will be implemented until you approve.** No file in the repository has been modified by this audit.

---

*This report describes an experimental research system. No component of the proposed architecture guarantees, or is capable of guaranteeing, investment returns. Profitability remains a hypothesis to be measured in Phase 6, not an assumption to be built upon. This is not investment advice.*
