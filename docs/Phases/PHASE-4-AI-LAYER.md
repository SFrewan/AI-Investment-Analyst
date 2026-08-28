# Phase 4 — AI layer

**Status:** **Verified.** Release build clean; full suite 1017 passed, 0 failed, 0 skipped (2026-08-27).
**Roadmap:** §P of [../PHASE0_AUTONOMOUS_PLATFORM_ARCHITECTURE.md](../PHASE0_AUTONOMOUS_PLATFORM_ARCHITECTURE.md), phase 4
**Last updated:** 2026-08-27

---

## 1. Phase objective

Judgement on top of trustworthy data — and a mechanism that stops judgement being mistaken for
measurement.

Phase 3 produced defensible numbers. Phase 4 adds the one thing arithmetic cannot do: reading
unstructured evidence and saying what it means. The whole difficulty is that a model will answer
whether or not it has grounds to, and a confidently invented figure is worse than a missing one
precisely because it is indistinguishable from a real one everywhere downstream.

§P's exit criterion is therefore not "the agents work". It is *an evaluation harness meeting agreed
thresholds for schema validity, groundedness and stability; below threshold, the phase does not end.*

## 2. Scope

In scope: the chat port, the agent contract, three specialist agents, a groundedness validator, a
synthesis agent, prompt versioning, audit records for every agent run, and the evaluation harness.

Explicitly out of scope: **any real provider**. Phase 4 ships the port and a chat model that
refuses. Calling a paid provider costs money, needs a credential, and makes every test depend on a
network and someone else's model version — and spending money is an action this platform gates
rather than assumes. Also out of scope: opportunity assembly and approval (phase 5), scoring changes
(phase 3 owns scoring), and persistence of analyses beyond the audit trail.

## 3. What was implemented

**The AI vocabulary** (`Domain/Ai/`) — `AgentId`, `PromptRef`, `ModelRef`, `AgentStatus`,
`AgentDiagnostics`, `AgentResult` / `AgentResult<T>` / `AgentResults`, `EvidenceItem`,
`EvidenceBundle`.

**The groundedness check** (`Domain/Ai/Groundedness/`) — `AssertedFigure`, `IGroundedOutput`,
`NumericMention`, `NumericTextScanner`, `GroundednessTolerance`, `GroundednessPolicy`,
`FigureFinding`, `GroundednessReport`, `GroundednessValidator`.

**The ports and machinery** (`Application/Ai/`) — `IChatModel`, `ChatRequest`, `ChatCompletion`,
`IPromptStore`, `PromptTemplate`, `AgentEnvelope`, `AnalysisJson`, `AnalysisBudget`,
`EvidenceRenderer`, `IAnalysisAgent<TInput, TOutput>` and the `AnalysisAgent<,>` run loop.

**Four agents** (`Application/Ai/Agents/`) — `FinancialAnalysisAgent`, `NewsAnalysisAgent`,
`RiskAnalysisAgent`, `SynthesisAgent`, with their output types and the `SpecialistFinding` /
`SynthesisInput` narrowing between stages 3 and 5.

**The orchestrator** (`Application/Ai/Pipeline/`) — `AnalysisRequest`, `AnalysisPipeline`,
`AnalysisOutcome`.

**The evaluation harness** (`Application/Ai/Evaluation/`) — `EvaluationCase`,
`EvaluationExpectation`, `EvaluationThresholds`, `EvaluationHarness`, `CaseOutcome`,
`EvaluationReport`.

**Infrastructure** — `FilePromptStore`, `UnconfiguredChatModel`, `PromptStoreOptions`, and the
`AddAi` registration.

**Four versioned prompts** under `prompts/`, following the convention `prompts/README.md` set in
Phase 0.

## 4. Architecture changes

None to the layering. The AI layer attaches where §I.2 says it should: deterministic C# decides what
runs, agents return data, and nothing new can cause an effect.

`AuditEventType` gained four members (`AnalysisRequested`, `AgentOutputAccepted`,
`AgentOutputRejected`, `AnalysisCompleted`) and `AuditRecord` gained `ForAgentRun`. Phase 1 designed
the record to take agent, model and prompt identity without a schema rewrite; this is the first
phase to test that claim rather than assert it, and it held — the event type is stored as text, so
no migration was needed.

## 5. Important projects/files

| Area | Files |
|---|---|
| Vocabulary | `Domain/Ai/*.cs` (10) |
| Groundedness | `Domain/Ai/Groundedness/*.cs` (9) |
| Ports and machinery | `Application/Ai/*.cs` (8), `Application/Ai/Abstractions/*.cs` (6) |
| Agents | `Application/Ai/Agents/*.cs` (12) |
| Orchestration | `Application/Ai/Pipeline/*.cs` (3) |
| Evaluation | `Application/Ai/Evaluation/*.cs` (6) |
| Infrastructure | `Infrastructure/Ai/*.cs` (2), `Configuration/PromptStoreOptions.cs` |
| Prompts | `prompts/<agent>/<name>.v1.0.md` (4) |
| Tests | 20 files across all five test projects |
| Tooling | `scripts/run-build.cmd` |

## 6. Domain / Application / Infrastructure changes

**Domain** holds the vocabulary and the groundedness check, because both are rules about what a
claim may assert, not mechanics of talking to a provider. **Application** holds the ports, the
agents and the orchestrator. **Infrastructure** holds the two things that touch the outside world: a
prompt store that reads files, and a chat model that refuses.

## 7. Database changes

**None.** No migration was created and none was needed. Agent runs are recorded in the existing
`audit_records` table; the new event types are stored as text in a column already sized for them.
The migration path is still exercised — the 101 integration tests run `MigrateAsync` against the
dedicated `ai_investment_tests` database.

## 8. APIs / contracts

No HTTP surface. New contracts: `IChatModel`, `IPromptStore`, `IAnalysisAgent<TInput, TOutput>`,
`IGroundedOutput`, and the JSON envelope every agent answers in
(`refused` / `refusal_reason` / `confidence` / `limitations` / `analysis`).

## 9. Security and safety changes

The claim this phase has to make good on is that adding judgement did not add a path from a model to
an effect. Six mechanisms, each with a test:

- **An agent's output can only ever become an `AiInterpretation`.** `AgentResult<T>.ToClaim()` is the
  single door into the claim graph and it opens onto one kind. The Phase 3 calculators refuse a
  judgement outright, so nothing requiring a measured value can consume one by accident.
- **A judgement cannot re-enter the evidence.** `EvidenceBundle` refuses any claim of a judgement
  kind, so one agent's opinion can never be fed to the next as fact.
- **Every figure is checked against the bundle.** Structured figures must match the claim they cite;
  prose is scanned for numerals that trace to nothing. A failure excludes the whole answer rather
  than annotating it.
- **The orchestrator cannot cause anything.** It holds no gateway, no repository and no unit of
  work, and a test asserts that no type in either AI namespace so much as references the
  Action/Policy seam.
- **Refusal is first-class.** `AgentStatus.Unknown` is zero and is a failure, so a result that
  skipped initialisation cannot present itself as a completed analysis.
- **It fails closed.** With no provider configured the registered `IChatModel` refuses every
  request, and the agent reports `ProviderError` with no output.

Untrusted input is handled in layers rather than by cleverness: evidence is delimited and framed as
data on **both** sides of the block (a single leading instruction is the easiest thing for injected
text to talk over), text values are sanitised so a headline cannot close the block early, the answer
is constrained to a schema, every figure is checked, and nothing an agent says can start an action.

## 10. Dependencies

**No new packages.** The architecture test forbidding `Microsoft.Extensions.AI`, `OpenAI`,
`Azure.AI`, `Anthropic` and `Microsoft.SemanticKernel` in any assembly is left in force, unweakened,
and still passes. See D-1.

## 11. Tests

**208 new test cases** across all five test projects: identity and versioning; result invariants;
bundle admission, look-ahead refusal and content hashing; the numeric scanner including dates and
scale suffixes; the validator's cited, uncited, mis-cited and prose paths; the run loop's retry
bounds, refusal, groundedness and budget behaviour; each agent's own shape; the pipeline's audit and
exclusion rules; prompt files against the agents that declare them; the fail-closed default; the
audit trail against a real database; the AI safety invariants; and the AI architecture rules.

Suite total after this phase: **1017** (809 before, 208 new).

## 12. Verification results

**Verified on the developer machine, 2026-08-27.** `scripts/verify.ps1` ran the Release build and
the full suite against the dedicated `ai_investment_tests` database.

| Gate | Result |
|---|---|
| `dotnet build` (Release, whole solution) | **Succeeded** — 0 warnings, 0 errors |
| `dotnet test` (Release, whole solution) | **Passed** — `build_exit=0 test_exit=0` |
| Suite total | **1017 total, 1017 passed, 0 failed, 0 skipped** |
| Evaluation harness against the shipped thresholds | Passes at 1.0 on all four measures |
| Migrations | Not applicable — no EF model change |
| Architecture rules | 22 passed, including the unweakened AI-SDK ban |
| Safety suite | 65 passed |

Per assembly:

| Assembly | Total | Passed | Failed | Skipped |
|---|---:|---:|---:|---:|
| AI.Investment.Domain.UnitTests | 595 | 595 | 0 | 0 |
| AI.Investment.Application.UnitTests | 213 | 213 | 0 | 0 |
| AI.Investment.Integration.Tests | 101 | 101 | 0 | 0 |
| AI.Investment.Safety.Tests | 65 | 65 | 0 | 0 |
| AI.Investment.Architecture.Tests | 22 | 22 | 0 | 0 |
| AI.Investment.Api.Tests | 21 | 21 | 0 | 0 |
| **Total** | **1017** | **1017** | **0** | **0** |

**What the builds caught.** Three compile errors on the first attempt, all in new code:

1. `FilePromptStore` — CS1620. A `string.Create(provider, $"…" + "…")` where an interpolated string
   is concatenated with a plain literal produces a `string`, which cannot bind to the interpolated
   handler overload. The message did not need formatting at all; the call was removed.
2. `AnalysisBudgetTests` — CS1503. `Select(_ => … TryBeginCall(out _))`: the lambda's discard
   parameter `_` was in scope, so `out _` bound to an `int` instead of declaring a discard.
3. `AiLayerSafetyTests` — CS1503. `ToClaim()` returns `Claim<FinancialReading>`, and a calculator
   input takes `Claim<decimal>`. The test was restated as two assertions — that an agent records
   itself as an interpretation, and that a numeric interpretation is refused by a calculator — which
   is what it was actually claiming.

Then six test failures, all incorrect expectations in the new tests rather than defects:
four expected `ModelRef.ToString()` to be `provider/model/version` when it is `provider/model@version`
(consistent with `PromptRef`); one expected an unstable case to be reported as "repeats disagreed"
when it had also failed its expectation, so the report named the observed statuses instead; and one
expected `SchemaFailed` from a run whose single call was spent on the first attempt, which correctly
reports `BudgetExceeded`. That last one turned into an extra test making the precedence explicit:
a run that exhausts its budget mid-retry reports the budget, because the budget is why it stopped.

## 13. Known limitations

1. **No provider is wired up.** Everything is exercised against a scripted model. The machinery is
   tested; the models are not, and cannot be until a provider and a credential exist.
2. **Thresholds are all 1.0.** Defensible only because the provider is deterministic and offline.
   When a sampling provider arrives these come down to measured values, and the number they come
   down to is a decision recorded here — not an adjustment made to get a build green.
3. **The narrative scan is eager.** A numeral in prose that traces to nothing rejects the answer,
   including small counts, so the prompts instruct agents to write them as words. A false positive
   costs a refusal, which is recoverable; a false negative is a fabricated figure reaching a score.
4. **The JSON schema is advisory to the provider.** It is sent so a capable provider can constrain
   generation, but the parser is the enforcement. Two statements of the same shape can drift apart;
   the parser is authoritative.
5. **Stage 2 is not wired to stage 3 automatically.** The caller assembles the bundle, including any
   Phase 3 metric results. Wiring the analytics catalogue into bundle assembly belongs with
   opportunity generation in phase 5.
6. **Cost is estimated, not billed.** `AgentDiagnostics.CostUsd` records what the provider adapter
   reports. With no adapter it is zero.

## 14. Architectural decisions

**D-1 — The chat port is owned, not imported.** §I.4 proposes abstracting on
`Microsoft.Extensions.AI`. That remains the right adapter to write when a provider is wired up, and
it is the wrong dependency to take now: its surface has already been renamed once between previews,
and adopting it would put a preview API underneath every agent for a call this phase never makes. A
seven-member port owned here costs nothing and keeps the architecture test that forbids an AI SDK
in any assembly true rather than relaxed. Phase 4 adds zero packages.

**D-2 — Groundedness is checked structurally *and* textually.** A structured figure list with
citations is easy to check and trivially bypassed: an agent that puts nothing in the list and writes
"margins improved to 42%" in its summary passes a structural check while inventing a number. The
narrative scan is the backstop. Calendar components of the evidence's own dates are admissible,
because a sentence naming the period a filing covers is quoting provenance, not inventing a figure.

**D-3 — The evidence list is derived by the validator, not reported by the agent.** An agent's own
account of what it read is exactly the sort of thing a model embellishes, and an evidence list
nobody checked is decoration. What the validator matched is what the result is allowed to cite.

**D-4 — Ungrounded answers are not retried; schema failures and provider errors are.** A malformed
answer may parse on a second ask. An ungrounded one, at temperature zero, is the same answer — and
an agent that re-rolls until a fabrication lands inside tolerance is precisely the failure the check
exists to stop.

**D-5 — A failed specialist contributes nothing to synthesis.** Not a summary, not a caveat, not its
figures. Passing along a failed answer with a warning attached puts the warning in a prompt, where
it is a suggestion, and the figure in a narrative, where it is quoted.

**D-6 — The bundle is content-addressed.** `EvidenceBundle.Hash` is computed from what the evidence
says, not from the identities its claims were handed in memory, so the same stored data hashes the
same way on a later run. Without it, two analyses of the same company a month apart differ for
reasons nobody can separate; with it, "the evidence changed" and "the answer changed" are
distinguishable in the audit trail.

**D-7 — The unset value of every new enum is the unsafe-to-default one.** `AgentStatus.Unknown` is
zero and is a failure; `GroundednessPolicy.Strict` is zero and is the strictest check. A caller who
forgets to choose gets the answer that refuses.

**D-8 — Phase 0's prompt convention was adopted as written.** `prompts/README.md` specified
`<agent>/<name>.v<major>.<minor>.md` before any prompt existed. An earlier draft of `PromptRef` used
a single integer version; it was rewritten to match the documented convention rather than the
convention rewritten to match the first implementation that arrived.

## 15. Deviations from the approved plan

**`Microsoft.Extensions.AI` was not adopted.** Recorded as a deliberate deviation with its reasoning
in D-1, not an omission. The port is shaped so the adapter is a single class when it is wanted.

**Six agents were not built.** §I.1 classifies Market research, News, Competitive intelligence,
Financial, Valuation, Growth and Risk as agent work. §P's phase 4 names **three** — Financial, News,
Risk — plus synthesis, and warns against "scope explosion to fifteen agents". Three plus synthesis
is what exists.

**No `AnalysisRequested` / `AnalysisCompleted` audit records are written yet.** The event types
exist; the pipeline records one entry per agent run, which is the level at which reproducibility
lives. A run-level envelope record belongs with the operating cycle in phase 6.

## 16. Dependencies on previous phases

Phase 1's `Claim`, `Provenance`, `ClaimKind`, `Confidence`, `AuditRecord`, `CorrelationId` and
`ProposedBy.AiAgent` — which anticipated this phase precisely enough that no safety type needed
changing. Phase 2's `IngestionSubject` and `SourceId`. Phase 3's `KnowledgeCutoff`, whose
publication-not-retrieval rule the bundle reuses unchanged.

## 17. Capabilities enabled for future phases

- **Phase 5 (opportunity)** has grounded, audited, confidence-bearing analyses to assemble
  recommendations from, each traceable to filings, and a hard rule that none of it authorises
  anything.
- **Phase 6 (continuous operation)** has a budget type and a cost record per run, which is what a
  cycle needs to hold itself within a spend ceiling.
- **Phase 7 (validation)** has the evidence hash and prompt hash, which is what makes a stored
  analysis re-derivable and fairly comparable with a later one.

## 18. Recommended next phase

§P puts **Phase 5 — opportunity, approval, capital** next: the full decision path, simulated. Its
exit criterion is a complete path replaying end to end with the safety suite green, including
mutation testing.

Phase 4 hands it exactly what it needs and nothing it should not have. Analyses arrive as data with
stated confidence and a checked evidence chain; the policy engine, the limit engine and the approval
workflow decide what happens next, and no part of this phase has an opinion about that.
