# Phase 2 — Global data and intelligence foundation

**Status:** In progress — stages 1–5 and 7 implemented, plus approved retention policy; verification pending
**Last updated:** 2026-08-25

---

> **This document is live.** Stages 6 and 8–10 are not yet built. Sections below describe
> stages 1–5 and 7 as they exist in the repository. Stage 7 was taken ahead of stage 6 deliberately
> — see section 15. The ingestion path is now **complete in code**: registry, gateway, connector,
> archive and ledger all exist and are wired. It has never been executed, and the schema change it
> needs has not been migrated.

## 1. Phase objective

Give the platform a trustworthy way to know things. Phase 1 built a safe way to *act* and a
vocabulary for *belief*; what it lacks is data whose origin, standing and licensing are known.

Phase 2 establishes the data plane: where information may come from, how much each origin counts,
what the platform is permitted to do with it, how it arrives, and how staleness is detected. The
model must generalise beyond equities to products, suppliers, commodities, FX and logistics
without a rewrite — that constraint shapes stage 1 heavily.

## 2. Scope

Ten stages, per the approved Phase 2 prompt:

| Stage | Subject | Status |
|---|---|---|
| 1 | Source registry and trust model | **Implemented** |
| 2 | Provenance integration | **Implemented** |
| 3 | Ingestion contracts | **Implemented** |
| 4 | Provider abstractions | **Implemented** |
| 5 | First providers (SEC EDGAR) | **Implemented** |
| 6 | Normalisation and validation | Outstanding |
| 7 | Historical persistence | **Implemented** (migration pending) |
| 8 | Data events and freshness | Outstanding |
| 9 | Application/API contracts and observability | Outstanding |
| 10 | Tests and architecture validation | Outstanding |

**Explicitly out of scope for the whole phase.** Scraping sites merely because they hold
convenient data; bypassing any provider's licensing, subscription or rate restrictions; any
credential in source control.

## 3. What was implemented

### Stage 1 — the source registry and its trust model

Twelve files in
`src/AI.Investment.Domain/Sources/`, all present in the repository.

The registry answers "where did this come from and how much does it count?" as a lookup rather
than an investigation. Nothing may enter the system from an unregistered origin: an ingestion run
names the source it drew from, and every resulting claim carries that identity.

`DataSource` is an aggregate root that deliberately **says nothing about how to fetch anything** —
no URLs, no credentials, no protocol. Those belong to the connector, which is infrastructure.
Keeping the trust model free of transport detail is what lets the same source be reached over a
REST API today and a bulk file tomorrow without changing what the platform believes about it.

Three rules are enforced structurally at registration:

- **`UnverifiedCannotConfirmAlone`** — a source of unverified authority may not confirm
  information on its own. Without this, a registration that skipped the authority question could
  quietly mint confirmed facts.
- **`AggregatorIsNotPrimary`** — community and aggregator content is never the originating record,
  whatever the registration claims.
- **`ActivationRequiresUsableLicence`** — a source that may be neither stored nor processed
  automatically cannot be activated. The registry is the right place to catch this, before an
  ingestion run has already retained the data.

Two further properties matter:

- **A source registers inactive.** Appearing in the registry states that the source has been
  assessed, not that it is switched on. Activation is a separate, deliberate act.
- **Reliability is earned, not declared.** `Reliability` starts at `Unrated` and is only set by
  `RecordReliability`, which the evaluation phase calls from measured outcomes. Nothing in the
  ingestion path may call it.

`UpdateLicensing` auto-deactivates an active source whose terms have narrowed to unusable, because
terms can change after a legal review and a running feed must not outlive its permission.

### Stage 2 — provenance integration

**Origin and locator were separated.** `Provenance.SourceId` was a free-form `string` holding
values like `"sec-edgar:0000320193-26-000001"` — a source and a record identifier fused into one
field. That value could not be looked up in the registry, and two claims from the same source did
not compare equal on their origin. It is now two properties:

| Property | Type | Meaning |
|---|---|---|
| `SourceId` | `SourceId` | The registered origin — `sec-edgar`. A key into the registry. |
| `SourceRecordId` | `string?` | The locator within that source — a filing accession number, an article id, a vendor row id. |

`Provenance.Create` keeps a `string` overload that parses through `SourceId.Create`, so an
identifier the registry could never hold is rejected where the claim is made rather than becoming
an origin nothing can resolve.

**`SourceType.InternalDerivation` was added**, so values the platform produces itself — a
calculation, an analysis agent, an ingestion service — are registered origins like any other.
That keeps one rule instead of two: every claim names a registered source, with no carve-out for
the platform's own output.

**`SourceAdmission`** is the data plane's gate, built to the policy engine's specification — pure,
total, deterministic, fail-closed, and every refusal names a versioned rule:

| Rule | Refuses when |
|---|---|
| `source.active@1` | The source is registered but not switched on |
| `source.category-recognised@1` | The category is `Unknown` or not defined in this build |
| `source.supplies-category@1` | The source does not declare that category, or does not cover that region |
| `source.storage-permitted@1` | Licensing does not permit storage, and ingestion stores what it retrieves |
| `source.processing-permitted@1` | Licensing does not permit automated processing, the only kind this platform performs |

`SourceAdmission.Admissible` composes the gate with `SourceRanking` and returns the usable sources
most authoritative first — the question ingestion actually asks.

**`ISourceRegistry`** was added to the Application layer as the read/write abstraction over
registered sources. It deliberately returns inactive and unlicensed sources from
`FindSuppliersAsync`: filtering belongs to `SourceAdmission`, which is pure and testable, and a
repository that silently dropped rows would put a licensing rule inside a SQL query and make the
reason for an empty result invisible.

**A partial-mutation defect was fixed in `DataSource`.** Its mutators validated the modification
timestamp inside `Touch`, which runs last, so a call with an impossible timestamp threw only
*after* the aggregate had been changed — `Activate` set `IsActive = true` and then failed. The
check moved to `EnsureModificationFollowsRegistration`, called first by every mutator.

### Stage 3 — ingestion contracts

The vocabulary of a retrieval, with nothing that performs one. Six domain types in
`src/AI.Investment.Domain/Ingestion/` and two Application abstractions. Purely additive: no
existing type was modified, deliberately, because stages 1 and 2 are still uncompiled and adding
coupling on top of unverified changes compounds the eventual correction.

**`IngestionRequest`** — what to fetch, from where, about what, for which period. Like
`DataSource` it holds no URL, no credential and no paging token; a connector turns it into HTTP
and nothing above the connector knows HTTP was involved.

Its **`Fingerprint()`** is a SHA-256 over source, category, region, subject and window,
deliberately *excluding* `RequestedAtUtc` and `CorrelationId`. That exclusion is the point: it
makes a retry the same request, which is what lets the fingerprint serve as the idempotency key
when a run is proposed through the Action/Policy seam. Including either field would make every
retry unique — the exact bug an idempotency key exists to prevent. The computation is
culture-invariant throughout, because a fingerprint that differed between machines by locale
would fail only sometimes, which is worse than not existing.

**`IngestionSubject`** — a validated `(Kind, Identifier)` pair, mirroring `ActionTarget`. Two
strings rather than a typed reference, and this is a scope decision rather than laziness: a
subject typed as `Ticker` would quietly narrow the data plane to equities and would have to be
torn out at the first product, supplier, currency pair or shipping route. `Identifier` is optional
so that a sweep and a specific request are distinguishable rather than separated by a placeholder.

**`ContentHash`** — SHA-256 of a payload, lower-case hex. The raw archive is content-addressed and
this is the address. It is what makes the phase's exit criterion — *any analysis replays
byte-identically from stored raw responses* — checkable rather than assumed: a payload that has
been altered no longer answers to the name the claim recorded. It also makes the archive naturally
deduplicating, so a daily poll of an unchanged document costs one row rather than one per day.
Chosen for collision resistance, not secrecy; nothing here is a security control.

**`IngestionRun`** — the append-only ledger of one attempt, the data plane's counterpart to
`ActionExecution`. A claim's provenance answers "where did this number come from?"; this answers
"was that retrieval complete, and has this source been failing?"

Two modelling decisions in it are worth stating:

- **`Refused` is a distinct outcome from `Failed`, and refusals are recorded.** A run refused
  because its source was inactive or unlicensed produces a completed run carrying the admission
  rule that refused it. Otherwise the most interesting thing that can happen — the platform
  declining to ingest something it was configured to ingest — leaves no trace, and the operator
  sees only an unexplained absence of data. Keeping it separate from failure also matters: a
  refusal is the platform working correctly, and counting it as an error trains whoever reads the
  dashboard to ignore errors.
- **`PartiallySucceeded` is distinct from `Succeeded`.** A partial result silently treated as
  complete is how gaps enter a history without anyone noticing.

**`IRawResponseArchive`** stores exactly what a source returned, addressed by its own hash, and
never parses it — an archive that understood its contents would need migrating whenever a provider
changed its schema, defeating the point of keeping the original. Its documentation records what
must *not* be archived: request headers and query strings, because the archive is long-lived and
read during investigations, and a credential written into it outlives every rotation.

**`IIngestionRunStore`** is append-only like `IActionExecutionStore`. `GetLatestForSourceAsync`
returns the latest run *whatever its outcome*, deliberately: freshness asks when the platform last
*tried*, and a source failing for a week is a different problem from one nobody has asked for —
but the two look identical if only successes are returned.

### Stage 4 — provider abstractions and the ingestion gateway

The transport half of the data plane, and the orchestrator that makes stages 1-3 load-bearing.
Still no connector: everything here is contract and composition, testable end to end without a
network.

**`ProviderCapabilities`** is the counterpart to `DataSource`. The registry says what the platform
is *permitted* to take from a source; this says what a connector is *able* to fetch — categories,
regions, subject kinds, whether it accepts a period and how long a one, and the rate the provider
declares. Declared rather than discovered, because a connector that learns what it supports by
trying is a connector that discovers a provider's restrictions by violating them.

It is a class, not a record, deliberately: it holds collections, and record equality would compare
them by reference. Value semantics that are announced but not delivered are worse than none.

**`ProviderCapabilityCheck`** is the second pure gate, built to the same specification as the
policy engine and `SourceAdmission` — total, deterministic, fail-closed, every refusal named:

| Rule | Refuses when |
|---|---|
| `provider.category-supported@1` | The connector does not supply that category |
| `provider.region-supported@1` | No declared region covers the requested one |
| `provider.subject-kind-supported@1` | The connector does not understand that kind of subject |
| `provider.window-supported@1` | A period was asked of a latest-only connector |
| `provider.window-within-limit@1` | The period exceeds what the connector accepts |

The last two are worth their own note. Answering a historical question with the latest value is a
*wrong* answer rather than a missing one; and an over-long window is refused rather than sent,
because a provider may answer it with a silently truncated result — a gap that looks like a
complete answer.

**`ProviderQuota`** states a provider's declared rate. Staying under a declared quota is
compliance with terms; backing off after a 429 is reacting to enforcement. It lives on the
connector's capabilities rather than on `DataSource`, because a rate limit is a property of the
API, not of the source's trustworthiness — the same regulator's filings might be reachable through
a throttled API and an unlimited bulk file, and what the platform believes about the regulator does
not change between them.

**`IDataProvider`** is deliberately thin: declare what you can do, fetch bytes.
**Credentials are absent from the interface by design** — a connector reads its own options from
configuration inside Infrastructure, so no credential crosses into Application or Domain, appears
in a signature, or can be captured by a caller with no business holding one.

**`IngestionGateway`** is the centrepiece. Four gates stand in front of every fetch, in order, and
nothing touches the network until all four pass:

1. The source is registered — `ingestion.source-registered@1`
2. `SourceAdmission` — active, covering, licensed for what ingestion does
3. A connector exists (`ingestion.provider-available@1`) and `ProviderCapabilityCheck` passes
4. The declared rate limit has room — `ingestion.within-rate-limit@1`

Only then does the request reach `IActionGateway`. **Ingestion is a side effect, so it goes
through the same seam as everything else rather than beside it** — which means the kill switch,
the capability policy and the audit trail apply to data collection without a line of
ingestion-specific code being written for any of them. The request fingerprint becomes the
proposal's idempotency key, so retry safety is inherited rather than rebuilt.

Three further decisions in the gateway:

- **It returns rather than throws.** A scheduler ingesting fifty subjects must not lose forty-nine
  because the third provider was down, so every ingestion outcome comes back as a completed run
  already written to the ledger. A failure of the platform's own machinery *before* a run begins
  still propagates — that is not an ingestion outcome and must not be dressed up as one.
- **Paging is bounded at 500 pages and the bound is reported**, as `PartiallySucceeded`. A
  truncated run that claims success is indistinguishable from a complete one.
- **A failure reason names the exception type and nothing else.** The ledger is append-only and
  cannot be redacted, and a provider's exception message is one of the likelier places for a URL
  with an embedded key to surface. The full detail is already in the audit trail the seam wrote
  before rethrowing.

**`AppDbContext` gained a fourth exempt type.** `IngestionRun` joins `AuditRecord`,
`ActionExecution` and `ProcessedAction` as append-only seam bookkeeping, for the same reason the
audit trail is exempt: a refused run must be recordable precisely when nothing is authorised,
because refusal is the situation in which no authorisation exists. It is not yet mapped — the
exemption has no effect until the persistence stage — but it is a consequence of the design rather
than of the schema, and adding it alongside the table would mean discovering it through a failing
refusal.

### Stage 5 — the SEC EDGAR connector

The first connector, chosen for what it is rather than for being free: **EDGAR is the originating
record** for U.S. company disclosure. A vendor summarising a 10-K is a report of a filing; this is
the filing. That it needs no key, no account and no payment is what let it be built without a
commercial decision, but it would be the right first source either way.

**Compliance is built into the connector rather than bolted onto it.**

| Obligation | How it is met |
|---|---|
| Every request must identify its origin | `User-Agent` composed from configured application name and contact address, set per request |
| Published rate ceiling | The connector declares `ProviderQuota.PerSecond(n)`, bounded by `SecEdgarOptions.FairAccessRequestsPerSecond`; the gateway's limiter honours it *before* fetching |
| No anonymous use | `Enabled` defaults to false and the connector is not registered at all unless a contact address is configured |

That last row is the important one. An installation that has not supplied a contact address gets
**no EDGAR connector**, and the gateway refuses runs for that source with
`ingestion.provider-available@1`, recorded in the ledger with a reason. Failing closed and visibly
beats registering a placeholder identity the SEC would be entitled to block.

**No credentials, and none hard-coded.** EDGAR needs no key. The contact address is deployment
configuration, not source code — putting a real person's e-mail in a repository would be both
wrong and useless the moment it changed. `appsettings.json` documents the shape with empty values
and `Enabled: false`.

**`SecEdgarEndpoints` is pure and separately tested.** EDGAR identifies companies by CIK, not
ticker, so `"320193"`, `"0000320193"` and `"CIK0000320193"` all normalise to the same ten digits
while `"AAPL"` is rejected — a ticker accepted silently produces a 404 that reads as "this company
has filed nothing". The category-to-endpoint mapping is a total function returning null rather
than throwing, because the capability check has already refused unsupported categories by name.

**`supportsWindow: false` is a statement of fact.** The submissions and company-facts endpoints
return a company's whole history in one document; there is no period parameter. Declaring window
support would let a request for one quarter silently return everything.

**The connector fetches bytes and nothing else.** No parsing, no field extraction. If EDGAR changes
a JSON shape, normalisation breaks visibly instead of history quietly changing its account of what
was filed.

**`SecEdgarSource`** is the registry entry — a *definition*, not a registration, returned
**inactive**. A connector shipping in the box does not switch itself on. Its licensing is recorded
rather than assumed: public-domain government records permit storage, redistribution and automated
processing, and the notes carry the fair-access identification requirement so a reader of the
registry sees the obligation and not only the permission.

**`ProviderCatalogue`** is built from whatever `IDataProvider` implementations the container holds,
so adding a provider is one registration and nothing else — no switch statement, no factory to
edit. Two connectors claiming one source is refused at construction, because otherwise which one
answered would depend on registration order.

**`SlidingWindowRateLimiter`** uses a sliding rather than fixed window: a fixed window permits a
full quota at the end of one interval and another at the start of the next — twice the declared
rate across the boundary, which a provider enforcing its own limit would rightly treat as a
violation. It is per-process, which is stated in the type's own documentation rather than left to
be discovered; shared state belongs to whatever phase introduces horizontal scale.

**`IIngestionGateway` is deliberately not registered.** It needs `ISourceRegistry`,
`IIngestionRunStore` and `IRawResponseArchive`, none of which has an implementation yet.
Registering it would build a container that cannot construct it, and ASP.NET Core validates the
container on build in Development — so the whole application would fail to start rather than one
feature being absent. A half-registered graph turns a missing feature into a dead host.

### Stage 7 — historical persistence

The stage that makes everything before it reachable. `IIngestionGateway` is now registered,
because the three storage contracts it needs finally have implementations.

**Two mapping strategies, chosen per property rather than uniformly.**

*Single-value wrappers become converted scalars.* `SourceId`, `Region`, `CorrelationId`,
`ContentHash` and `IngestionRunId` each hold one value, so a converter is lossless and the column
stays queryable and indexable. All read back **through their factories** — `SourceId.Create`, not a
bypass constructor — so a row violating a domain rule fails loudly on load rather than becoming an
invalid object in memory.

*Multi-field value objects become owned types.* `LicensingTerms`, `VerificationPolicy`,
`UpdateCadence`, `IngestionRequest`, `IngestionSubject` and the window are flattened into real
columns, because every field in them is something an operator asks about — "which sources may we
redistribute?" and "show me every run for this company last week" have to be queries, not scans.
EF materialises them through their **private constructors**, whose parameter names already match
their properties; no domain type was changed to accommodate the ORM.

*Sets become `jsonb`.* A source's categories and a run's artifact hashes have no independent
identity and are never joined on. One column each, like `audit_records.details`, rather than child
tables adding a join to every read. Categories are serialised **by name, not by numeric value**, so
a jsonb document holding `"MarketPrices"` survives an enum being renumbered and is legible to
anyone reading the table. A category this build does not recognise is skipped on load rather than
failing the whole source — otherwise rolling a category back would make every source using it
unreadable.

**The request fingerprint is a shadow property.** It is derived — a SHA-256 over the canonical
request — so it is not domain state and does not belong on `IngestionRequest` as a stored field.
But it cannot be computed in SQL either, and `HasCompletedAsync` must look runs up by it. Shadow
property: stored and indexed, absent from the object model, written by the store that knows how to
derive it.

**`FileSystemRawResponseArchive`** is content-addressed — bytes named by their own SHA-256. That
buys three things at once: writes are idempotent, so a daily poll of an unchanged document costs
one file rather than 365; tampering is detectable, because altered bytes no longer answer to the
name a claim recorded; and replay is exact, which is what the phase's exit criterion asks for.

Writes are **atomic** — temp file then move — so a process killed mid-write leaves a stray temp
file rather than a truncated payload sitting under a hash that no longer describes it. The second
failure would be worse than losing the payload, because it would be silently wrong. Two callers
racing on identical content is not an error to reconcile: content addressing means they wrote the
same thing.

A sidecar holds media type, source and retrieval time. **No request detail is written** — no URL,
no headers, no query string — because those carry API keys and this store is long-lived and read
during investigations.

**`FindSuppliersAsync` filters region in SQL and category in memory**, deliberately. Categories
live in `jsonb` and `DataSource.Supplies` is domain logic; neither translates. The region predicate
does the work that matters — it is what makes the result a handful of rows rather than the table —
and the rest is a set intersection over what a registry realistically holds. (PostgreSQL `@>`
containment on the jsonb column was verified to work, so a GIN index is available if this ever
becomes a hot path.)

**`DataSource` is *not* exempt from the write guard.** Only `IngestionRun` joined the append-only
set. The registry is ordinary domain state, so registering or activating a source is a side effect
that must pass through the seam like any other — which is exactly the property that stops a
connector from switching itself on.

### Retention — approved and implemented (Option C, tiered by licence, with a floor)

The decision presented in section 18 was approved: **retention obligations attach to sources
individually, never globally.**

`RetentionLimit` lives on `LicensingTerms`, which makes the licence the authority on its own
obligation. Enforcement reads it from there, so a rule and the terms it implements cannot drift
apart — and **nothing in the retention engine names a provider**. Adding a source with a 12-month
clause is a registration; the engine does not change. `RetentionLimit.Unlimited` means *no legal
compulsion to delete*, modelled explicitly rather than as a null `TimeSpan`, so "no obligation" and
"obligation not yet established" cannot be confused. (The latter is `LicensingTerms.Unknown`, which
permits no ingestion at all — so nothing is ever stored under terms nobody has read.)

`RetentionPolicy` is pure, total and versioned like the policy engine:

| Rule | Outcome |
|---|---|
| `retention.no-licensed-limit@1` | Retain — nothing compels deletion |
| `retention.within-licensed-limit@1` | Retain |
| `retention.licensed-limit-exceeded@1` | **DeleteRequired**, and marks evidence when referenced |

**The default outcome is `Retain`, not deny.** Everywhere else in this platform the safe default is
refusal; here the irreversible operation is the *deletion*. A payload wrongly retained can be
deleted tomorrow; a payload wrongly deleted takes an audit trail and a backtest with it. `Retain`
is therefore the zero value, so an unset enum can never read as "delete".

**The floor.** A payload referenced by stored evidence is deleted only when a licence requires it —
never for age, convenience or disk pressure. There is no other deletion path in the system at all.
When a licence *does* compel deletion of referenced evidence, `UnreplayableEvidence` is written: the
claim survives and the gap becomes visible, instead of a later replay quietly returning nothing.
The marker is keyed by content hash rather than by claim, because one payload can underpin many
claims and one row per deleted payload is both smaller and more truthful than a flag copied onto
each of them.

**Deletion goes through the safety seam**, under a new `Capability.DataRetention` — distinct from
`DataIngestion` because permission to fetch data is not permission to destroy it. Three properties
follow: the kill switch stops retention deletion; an installation that has not deliberately
configured the capability deletes nothing, because a capability with no policy is denied; and every
deletion is audited with the rule and reason that required it.

The proposal declares `ReversibilityClass.Irreversible`, truthfully — which means
`policy.irreversible-requires-approval@1` applies and **every retention deletion requires human
approval** unless an operator has explicitly granted `AllowIrreversibleAutoExecute` for that
capability. That is the right default for the one operation here that cannot be taken back.

**Marking precedes deletion.** A crash between the two steps leaves a marker for a payload that
still exists — conservative, visible, self-correcting on the next pass. The other order would leave
a deleted payload with nothing recording why, which is exactly the silent gap the mechanism exists
to prevent. The archive deletes the payload before its sidecar for the same reason.

**Retain decisions are not written to the audit trail.** A retain is the absence of an action, and
a sweep over a large archive would produce one audit row per payload per pass, burying the
deletions that matter. The decision stays auditable because it is pure and deterministic —
re-derivable from the source's terms and the payload's age, which is cheaper and more trustworthy
than storing millions of copies of "nothing happened".

**Not built, deliberately:** storage tiering, and the scheduled sweep that walks the archive.
`IRetentionEnforcer` decides per payload, which is pure and exhaustively testable; deciding *when*
to walk is scheduling and belongs with stage 8.

## 4. Architecture changes

The **data plane** was introduced as a first-class concept, entered through the registry. It sits
entirely in `Domain` so far; no infrastructure has been added.

A deliberate separation was drawn between three things that are easy to conflate:

| Concept | Question it answers | Type |
|---|---|---|
| **Authority** | What the source *is* | `SourceAuthority` |
| **Reliability** | What the source *has done* | `ReliabilityGrade` |
| **Verification policy** | What the platform *requires of it* | `VerificationPolicy` |

`SourceRanking` orders sources deterministically — pure and total, the same properties the policy
engine has and for the same reason: "which source do we believe?" must be answerable identically
every time and reconstructable months later.

It **explicitly does not resolve conflicts.** Ordering sources by standing is a different problem
from deciding what to do when two of them state different numbers, and a clever resolver written
before any real conflicting data exists would be guessing. What it provides is the foundation a
resolver needs: a stable, explainable ordering.

Recency is deliberately absent from the ranking. It is a property of an observation, not of a
source, and belongs to whatever compares two claims.

## 5. Important projects/files

All under `src/AI.Investment.Domain/Sources/`:

| File | Role |
|---|---|
| `DataSource.cs` | The aggregate root. Registration rules, activation, licensing, coverage. |
| `SourceRanking.cs` | `IComparer<DataSource>`; deterministic ordering by standing. |
| `SourceId.cs` | Readable slug (`sec-edgar`, `fred`), max 64, `[a-z0-9.-]`, no leading/trailing separator. |
| `SourceAuthority.cs` | `Unverified` → `Secondary` → `Primary`. Ordering is meaningful. |
| `SourceType.cs` | 13 values from `RegulatoryAuthority` to `CommunityOrAggregator`. |
| `DataCategory.cs` | 18 values spanning `MarketPrices` … `ShippingAndLogistics`, `CompetitorIntelligence`. |
| `ReliabilityGrade.cs` | `Unrated` (default) → `Poor` → `Fair` → `Good` → `Excellent`. |
| `ConfirmationState.cs` | `Unverified`, `PartiallyConfirmed`, `Confirmed`, `Conflicting`, `Superseded`. |
| `VerificationPolicy.cs` | `CanConfirmAlone` + `RequiredIndependentSources`; `Classify(int)`. |
| `LicensingTerms.cs` | Storage / redistribution / automated processing / attribution. |
| `Region.cs` | `Global`, `UnitedStates`, `Create`, `Covers`. |
| `UpdateCadence.cs` | `CadenceKind` + expected interval; `IsOverdue(lastRefreshed, now, grace)`. |
| `SourceAdmission.cs` | Stage 2. The gate: five ordered, versioned rules, plus `Admissible`. |
| `SourceAdmissionResult.cs` | Stage 2. Admitted, or refused with a rule id and a reason. |

Elsewhere:

| File | Role |
|---|---|
| `src/AI.Investment.Domain/Evidence/Provenance.cs` | Stage 2. `SourceId` + `SourceRecordId`. |
| `src/AI.Investment.Application/Abstractions/ISourceRegistry.cs` | Stage 2. Read/write over registered sources. |
| `src/AI.Investment.Domain/Ingestion/IngestionRequest.cs` | Stage 3. What to fetch, plus the idempotent `Fingerprint()`. |
| `src/AI.Investment.Domain/Ingestion/IngestionRun.cs` | Stage 3. Append-only ledger of one attempt. |
| `src/AI.Investment.Domain/Ingestion/IngestionSubject.cs` | Stage 3. `(Kind, Identifier)` — domain-agnostic by design. |
| `src/AI.Investment.Domain/Ingestion/ContentHash.cs` | Stage 3. SHA-256; the raw archive's address. |
| `src/AI.Investment.Domain/Ingestion/IngestionOutcome.cs` | Stage 3. `InProgress` default; `Refused` separate from `Failed`. |
| `src/AI.Investment.Domain/Ingestion/IngestionRunId.cs` | Stage 3. |
| `src/AI.Investment.Application/Abstractions/IRawResponseArchive.cs` | Stage 3. Content-addressed byte store. |
| `src/AI.Investment.Application/Abstractions/IIngestionRunStore.cs` | Stage 3. Append-only run ledger. |
| `src/AI.Investment.Domain/Ingestion/ProviderCapabilities.cs` | Stage 4. What a connector can fetch. |
| `src/AI.Investment.Domain/Ingestion/ProviderCapabilityCheck.cs` | Stage 4. Five ordered, versioned capability rules. |
| `src/AI.Investment.Domain/Ingestion/ProviderCapabilityResult.cs` | Stage 4. |
| `src/AI.Investment.Domain/Ingestion/ProviderQuota.cs` | Stage 4. A provider's declared rate. |
| `src/AI.Investment.Application/Ingestion/IngestionGateway.cs` | Stage 4. Four gates, then the seam. |
| `src/AI.Investment.Application/Ingestion/IDataProvider.cs` | Stage 4. The connector contract; no credentials. |
| `src/AI.Investment.Application/Ingestion/ProviderResponse.cs` | Stage 4. Bytes, media type, optional locator and page token. |
| `src/AI.Investment.Application/Ingestion/IProviderCatalogue.cs` | Stage 4. `SourceId` to connector. |
| `src/AI.Investment.Application/Ingestion/IProviderRateLimiter.cs` | Stage 4. Consulted before a fetch, never waits. |
| `src/AI.Investment.Infrastructure/Ingestion/Providers/SecEdgarProvider.cs` | Stage 5. The first connector. |
| `src/AI.Investment.Infrastructure/Ingestion/Providers/SecEdgarEndpoints.cs` | Stage 5. Pure CIK normalisation and endpoint mapping. |
| `src/AI.Investment.Infrastructure/Ingestion/Providers/SecEdgarSource.cs` | Stage 5. The registry definition, inactive. |
| `src/AI.Investment.Infrastructure/Ingestion/ProviderCatalogue.cs` | Stage 5. One registration per provider, duplicates refused. |
| `src/AI.Investment.Infrastructure/Ingestion/SlidingWindowRateLimiter.cs` | Stage 5. Per-process, sliding window, never waits. |
| `src/AI.Investment.Infrastructure/Configuration/SecEdgarOptions.cs` | Stage 5. Disabled by default; identification required when enabled. |
| `src/AI.Investment.Infrastructure/Persistence/Configurations/DataSourceConfiguration.cs` | Stage 7. Converted scalars, owned types, `jsonb` categories. |
| `src/AI.Investment.Infrastructure/Persistence/Configurations/IngestionRunConfiguration.cs` | Stage 7. Flattened request, shadow fingerprint, `jsonb` artifacts. |
| `src/AI.Investment.Infrastructure/Persistence/Repositories/EfSourceRegistry.cs` | Stage 7. Tracked reads; region in SQL, category in memory. |
| `src/AI.Investment.Infrastructure/Persistence/Repositories/EfIngestionRunStore.cs` | Stage 7. Append-only, writes through the internal save path. |
| `src/AI.Investment.Infrastructure/Ingestion/FileSystemRawResponseArchive.cs` | Stage 7. Content-addressed, atomic writes, never deletes. |
| `src/AI.Investment.Infrastructure/Configuration/RawArchiveOptions.cs` | Stage 7. Root path; retention lives on the licence, not here. |
| `src/AI.Investment.Domain/Sources/RetentionLimit.cs` | Retention. On `LicensingTerms`; `Unlimited` is explicit. |
| `src/AI.Investment.Domain/Retention/RetentionPolicy.cs` | Retention. Three versioned rules; defaults to Retain. |
| `src/AI.Investment.Domain/Retention/UnreplayableEvidence.cs` | Retention. The visible form of a licensed gap. |
| `src/AI.Investment.Application/Retention/RetentionEnforcer.cs` | Retention. Seam-gated, irreversible, marks before deleting. |
| `src/AI.Investment.Infrastructure/Persistence/Repositories/EfPayloadReferenceIndex.cs` | Retention. `jsonb` containment; errs toward "referenced". |

`SourceId` is a readable slug rather than a GUID deliberately: this value is written into the
provenance of every claim the source produces and read by a human investigating why the system
believed something. `sec-edgar` answers that at a glance; a GUID sends the reader to a lookup
table.

## 6. Domain / Application / Infrastructure changes

**Domain.** The `Sources` namespace, now 14 files. `Provenance` (in `Evidence`) was changed to
carry a typed `SourceId` and a separate `SourceRecordId`; `SourceType` gained
`InternalDerivation`; `DataSource` mutators now validate before mutating.

A new `Ingestion` namespace was added in stage 3 (`IngestionRequest`, `IngestionRun`,
`IngestionSubject`, `ContentHash`, `IngestionOutcome`, `IngestionRunId`) and extended in stage 4
(`ProviderCapabilities`, `ProviderCapabilityCheck`, `ProviderCapabilityResult`, `ProviderQuota`).

**Application.** `ISourceRegistry` (stage 2), `IRawResponseArchive` and `IIngestionRunStore`
(stage 3), and an `Ingestion` namespace in stage 4: `IIngestionGateway`/`IngestionGateway`,
`IDataProvider`, `ProviderResponse`, `IProviderCatalogue`, `IProviderRateLimiter`,
`IngestionParameters`.

**Infrastructure.** Stage 7 added two entity configurations, two `DbSet`s, two stores, the
filesystem archive and `RawArchiveOptions`, and registered `IIngestionGateway` — which stage 5 had
deliberately left out. Stage 4 added `IngestionRun` to `AppDbContext.IsSeamBookkeeping`. Stage 5
added an `Ingestion` namespace — `ProviderCatalogue`, `SlidingWindowRateLimiter` and a `Providers`
folder holding the EDGAR connector, its endpoint builder and its registry definition — plus
`SecEdgarOptions` and the `AddIngestion`/`AddSecEdgar` registration in `DependencyInjection`. There is deliberately still **no EF configuration for `DataSource`**,
so `ApplyConfigurationsFromAssembly` does not pick it up and the existing model snapshot has not
drifted. Persistence of the registry is stage 6/7 work.

## 7. Database changes

**Stage 7 introduces the first schema change since Phase 1's `InitialCreate`** — two tables,
`data_sources` (21 columns) and `ingestion_runs` (17 columns), with five indexes.

**The migration has not been generated.** It requires `dotnet ef migrations add`, which needs the
SDK. What *has* been done is stronger than nothing and is the same treatment `InitialCreate`
received: the schema was derived by hand from the two configurations, applied to a live PostgreSQL
16.13 server, and exercised. All types are valid PG16; `interval` round-trips a cadence; jsonb
containment (`@>`) finds a source by category, so a GIN index is available later; the fingerprint
index is used by its lookup. Twelve behavioural checks, all as specified — see the verification
log.

When the migration is generated it should be cross-checked against `/tmp`-independent expectations:
21 + 17 columns, `PK_data_sources` on a `varchar(64)`, `outcome` and `request_fingerprint` both
NOT NULL, `cadence_interval` nullable `interval`, `categories` and `artifacts` both `jsonb` NOT
NULL.

### Historical note

Before stage 7 this section read **"None yet."** for stages 1-5, which was accurate then: the model
snapshot held exactly the four Phase 1 entities and nothing in the data plane was mapped. Verified rather than assumed: the model snapshot contains exactly four entities
(`Company`, `AuditRecord`, `ActionExecution`, `ProcessedAction`) and no `data_sources` table
exists. Stages 1 and 2 introduced no migration and no schema drift.

The `Provenance` change in stage 2 was free precisely because nothing persists claims yet. That
was the reason for doing it now rather than later; see section 13.

Stage 4 changed `AppDbContext` but not the model: `IngestionRun` was added to the append-only
exempt set without a `DbSet` or an entity configuration, so the snapshot is unchanged and no
migration is due. Verified rather than assumed — the drift check still reports exactly the four
Phase 1 entities.

## 8. APIs / contracts

None yet. Stage 9.

## 9. Security and safety changes

- **Licensing is a domain invariant, not a policy document.** `LicensingTerms` defaults every
  permission to **false**, and `LicensingTerms.Unknown` permits nothing. A source whose terms
  nobody has established cannot be activated. This is the concrete implementation of the
  constraint against bypassing provider licensing: the system cannot ingest from a feed it has no
  recorded permission to use, and the failure is a domain rule violation rather than a note in a
  README.
- **No transport detail in the trust model.** Because `DataSource` holds no URL and no
  credential, the registry cannot become a place where a key is accidentally stored — and it can
  be logged, serialised and displayed without a redaction pass.
- **Reliability cannot be self-declared**, so a source cannot be registered as trustworthy; it can
  only become trustworthy by measurement.
- **Structural rules over configuration**, matching the Phase 1 policy engine: the three
  registration rules are unconditional, so a mistaken or malicious registry entry cannot elevate
  an aggregator to primary or let an unverified source mint facts.
- **Licensing is enforced before retrieval, in one place** (stage 2). `SourceAdmission` refuses a
  source whose recorded terms do not permit storage or automated processing. Checking after
  retrieval would be checking after the impermissible ingestion had already happened, and leaving
  the check to each connector would put a compliance rule in code written to talk to an API.
- **Every claim's origin is now resolvable** (stage 2). Because `Provenance.SourceId` is a
  registry key rather than free text, "which source said this, and what are we allowed to do with
  it?" is answerable for any stored value — including values the platform produced itself.
- **The raw archive's contract states what must never enter it** (stage 3): request headers, query
  strings, and anything else that can carry an API key. The archive is long-lived and is read
  during investigations, so a credential written into it outlives every rotation. Stating this in
  the interface rather than in a wiki page puts it in front of whoever implements it.
- **Refusals are auditable** (stage 3). An ingestion run refused on licensing grounds is written
  to the ledger with the rule that refused it, so declining to ingest is evidenced rather than
  invisible — which is what makes the licensing posture demonstrable rather than merely claimed.
- **Credentials cannot reach Application or Domain** (stage 4). `IDataProvider` has no credential
  parameter and no credential property; a connector reads its own options from configuration
  inside Infrastructure. The isolation is structural rather than a convention someone must
  remember.
- **Licensing and rate limits are enforced before the network is touched** (stage 4). All four
  gateway gates run before any connector call. An unlicensed fetch is a compliance problem the
  moment bytes arrive, not the moment they are stored; and a declared rate limit is complied with
  rather than discovered by being throttled.
- **Ingestion inherits the whole safety seam** (stage 4). Because a run is proposed as an
  `ActionProposal` under `Capability.DataIngestion`, the kill switch stops data collection, the
  capability policy governs it and the audit trail records it — with no ingestion-specific safety
  code, which is the only kind of safety code that stays correct.
- **The first connector complies by construction** (stage 5). Identification is sent on every
  request because the fair-access policy requires it; the declared quota is honoured before
  fetching rather than after being throttled; and the connector is absent entirely rather than
  anonymous when unconfigured. No credential is hard-coded because EDGAR needs none, and the
  contact address is configuration.
- **The failure path cannot leak a key into an unredactable ledger** (stage 4). A failed run
  records the exception type only, never the provider's message; a test asserts that an API key in
  an exception message does not reach the run's reason.

## 10. Dependencies

Stages 1-4 added nothing. Stage 5 added one package:

| Package | Reason |
|---|---|
| `Microsoft.Extensions.Http` 8.0.0 | `IHttpClientFactory`, for a typed `HttpClient`. It pools and rotates handlers, which is what stops socket exhaustion under a long-running ingestion loop and stops a cached handler pinning a stale DNS entry for the life of the process. Hand-constructing `HttpClient` gets one or the other wrong. Referenced directly rather than transitively because `AddHttpClient` is called directly by Infrastructure's composition. |

No provider SDK was taken. A connector talks to an HTTP API through `HttpClient`, which keeps the
dependency surface flat and means adding a provider does not add a package whose release cadence
the platform then inherits.

## 11. Tests

Written in stage 2, covering stages 1 and 2 together. Deferring all of them to stage 10 was
recorded as the largest gap in stage 1; it was closed at the first opportunity rather than left
to accumulate.

| File | Covers |
|---|---|
| `tests/.../Sources/SourceTestData.cs` | Shared builders — named parameters keep each test's subject visible |
| `tests/.../Sources/DataSourceTests.cs` | All three registration rules by rule id, inactive-on-registration, unrated-on-registration, activation refusal on unusable licensing, auto-deactivation when terms narrow, coverage, `IsAuthoritative`, validate-before-mutate |
| `tests/.../Sources/SourceRankingTests.cs` | Each ordering level in isolation, plus the identifier tie-break asserted from both input orders |
| `tests/.../Sources/SourceAdmissionTests.cs` | Every refusal rule by id, the admitted path, and `Admissible` filtering and ordering |
| `tests/.../Sources/SourceValueObjectTests.cs` | `SourceId` normalisation and rejection, `Region`, `VerificationPolicy.Classify`, `UpdateCadence.IsOverdue`, `LicensingTerms` defaults |
| `tests/.../Evidence/ProvenanceTests.cs` | The origin/locator split, rejection of unresolvable origins, system-produced provenance |
| `tests/.../Ingestion/IngestionContractTests.cs` | Stage 3. `ContentHash` against the published SHA-256 vectors, `IngestionSubject` including four non-equity subjects, `Fingerprint()` stability and discrimination, `IngestionRun` lifecycle and refusal recording |
| `tests/AI.Investment.Domain.UnitTests/Ingestion/ProviderCapabilityTests.cs` | Stage 4. `ProviderQuota`, `ProviderCapabilities` validation and case-insensitive subject matching, every `ProviderCapabilityCheck` rule by id |
| `tests/AI.Investment.Application.UnitTests/Ingestion/IngestionGatewayTests.cs` | Stage 4. All four gates, the seam, paging, deduplication, and the failure path |
| `tests/AI.Investment.Application.UnitTests/Ingestion/IngestionTestDoubles.cs` | Stage 4. Six hand-written doubles, no mocking framework |
| `tests/AI.Investment.Integration.Tests/Ingestion/SecEdgarProviderTests.cs` | Stage 5. CIK normalisation, endpoint mapping, the registry definition, options validation, the rate limiter's sliding window, and duplicate-connector rejection |

`tests/.../Evidence/ClaimTests.cs` was updated for the new `Provenance` shape — its fixture
previously used `"sec-edgar:0000320193-26-000001"`, which is exactly the fused value stage 2
removed.

Stage 2 added **87** executable cases, stage 3 **37**, stage 4 **43**, stage 5 **31**, and retention
**31**. The solution has gone from 189 to **418**. **None has been executed.** See section 12.

The EDGAR tests deliberately **do not make an HTTP request**. Hitting the SEC from a test suite
would consume somebody's fair-access quota on every CI run — precisely the behaviour this connector
exists to avoid. What is tested is everything that can be wrong without a network: identifier
normalisation, endpoint selection, the recorded licensing, options validation, and the limiter's
window arithmetic.

Two assertions recur through the gateway tests and carry most of their weight: `FetchCount == 0`,
which says the network was never touched, and a recorded run naming the rule, which says the
refusal was written down. A gate that stops a request but leaves no trace turns a compliance
decision into an unexplained absence of data, so both halves are asserted every time.

The `ContentHash` tests deserve a note on method: they assert against the *published* SHA-256
digests of the empty string and `"abc"`, not against the implementation's own output. A hash
tested only against itself is self-consistent and possibly wrong, and content addressing is
worthless if the address is computed differently by a future version.

## 12. Verification results

**Not verified.**

| Gate | Status |
|---|---|
| `dotnet build` | **PENDING LOCAL VERIFICATION** — no .NET SDK reachable from the assistant's environment |
| `dotnet test` | **PENDING LOCAL VERIFICATION** — none of Phase 2's cases has been executed |
| Runtime | Not applicable yet |
| Database | Not applicable — no schema change |
| Safety | Rules exist in code and in tests; not executed |
| Static review | Passed, including one analyzer fix and two defects found (section 15) |
| Snapshot drift check | **Passed** — model snapshot still contains exactly the four Phase 1 entities |
| Structural check on every changed file | **Passed** — brace/paren balance with strings and comments stripped |

The structural check is a weak instrument and is recorded as such: it catches gross damage and
proves nothing about type correctness. It is not a substitute for a compiler and is not offered
as one.

Static verification actually executed, across the whole solution (163 files, 46 namespaces, 194
top-level types):

| Check | Result |
|---|---|
| Brace/paren balance, strings and comments stripped | **PASS** |
| Namespace declaration matches folder path, every file | **PASS** |
| Every `using AI.Investment.*` resolves to a declared namespace | **PASS** |
| Dependency direction: Domain references nothing; Application references no Infrastructure or Api | **PASS** |
| Duplicate type names within a namespace | **PASS** — the only two hits are generic-arity pairs (`PagedResult`/`PagedResult<T>`, `Claim`/`Claim<T>`) |
| Stray `.cs` outside `src/` and `tests/` | **PASS** — none |
| Interface members implemented by every implementer | **PASS** for all six new ingestion doubles |
| Non-ASCII characters anywhere in `src/` or `tests/` | **PASS** — none |
| EF model snapshot drift | **PASS** — still exactly the four Phase 1 entities |
| Service-graph review: every registered service's dependencies are registered | **PASS** — `IngestionGateway`'s seven dependencies all resolve, lifetimes compatible (scoped depending on scoped and singleton) |
| Stage 7 schema against live PostgreSQL 16.13 | **PASS** — 2 tables, 38 columns, 5 indexes created; 12 behavioural checks all as specified |
| Retention schema against live PostgreSQL 16.13 | **PASS** — `unreplayable_evidence` created; duplicate marker and null reason both rejected |
| The reference-index `jsonb` containment query, against real rows | **PASS** — finds a run holding the hash, returns false for one nothing references |
| `dotnet ef migrations add` | **PENDING LOCAL VERIFICATION** — requires the SDK |

These are real checks with real results, and they are also the ceiling of what can be established
without a compiler. None of them proves type correctness.

Stage 1 was chosen deliberately as work that a Phase 1 runtime defect could not invalidate. Stage
2 does not have that property — it changes `Provenance`, which Phase 1 code uses. Stage 3 was kept
**purely additive** for that reason: it modifies no existing type, so a Phase 1 or stage 2 build
failure cannot cascade into it.

**Stage 5 is where implementation stops without input.** It needs real provider access —
credentials and licence terms — which is a commercial and legal decision rather than an
implementation one. See section 18.

## 13. Known limitations

- **Nothing has been compiled or executed.** Tests now exist; none has run.
- ~~**No tests.**~~ *Resolved in stage 2 — see section 11.*
- ~~**`Provenance.SourceId` and `SourceId` have not been reconciled.**~~ *Resolved in stage 2.*
  The original entry is kept here because its reasoning is the record of why the change was made
  when it was: `Provenance.SourceId` was a free-form `string` (max 200, no format rule) while
  `SourceId` was a normalised slug (max 64, restricted charset), so a claim could name a source
  that was never registered, in a form the registry could never produce. Retyping it was free only
  until claims are persisted at stage 7; after that the same change needs a data migration. It was
  done at stage 2 for that reason.
- **Nothing yet verifies that a claim's origin is actually registered.** `Provenance` guarantees
  the identifier is *well formed*, not that a source with that id exists or is admissible.
  Enforcing that needs the registry persisted and is stage 6/7 work; `ISourceRegistry` and
  `SourceAdmission` are the pieces it will be assembled from.
- ~~**`SourceAdmission` is not yet called by anything.**~~ *Resolved in stage 4* — it is gate 2 of
  the ingestion gateway.
- ~~**No connector exists.**~~ *Resolved in stage 5* — SEC EDGAR.
- ~~**Ingestion cannot yet run end to end.**~~ *Resolved in stage 7* — the gateway is registered
  and every dependency resolves. It has still never been executed.
- **Nothing can put a source into the registry.** `ISourceRegistry.Add` exists and registering a
  source is correctly a seam-gated side effect, but no command or endpoint calls it, so the
  registry starts empty and every ingestion run refuses with `ingestion.source-registered@1`. A
  registration command is the first item of the API stage.
- ~~**The archive never deletes.**~~ *Resolved* — Option C implemented; see section 3.
- **Nothing sweeps the archive.** `IRetentionEnforcer` decides per payload; the recurring job that
  walks the archive is stage 8, so no retention deletion happens on its own yet.
- **Claims are still not persisted**, so `IPayloadReferenceIndex` consults ingestion runs only. The
  interface does not change when claims arrive — it becomes a union of two queries.
- **EDGAR takes CIKs, not tickers.** Nothing yet resolves a ticker to a CIK, so ingestion for a
  company needs its CIK supplied. EDGAR publishes a ticker-to-CIK mapping; wiring it in is small
  and belongs with normalisation.
- **The rate limiter is per-process.** Two instances would each keep to the quota and together
  exceed it. Correct for the single-instance deployment this is, and stated in the type itself.
- **Only regulatory and fundamental data has a source.** Market prices, news and macroeconomic
  series have categories and a registry but no connector. Each is one registration once a source
  is chosen; none requires an architecture change.
- ~~**Nothing implements the storage contracts.**~~ *Resolved in stages 5 and 7.*
- **`IngestionRun`'s write-guard exemption is untested.** The type is now mapped, so the
  integration test that would prove a refusal is recordable with no authorisation window open is
  finally *writable* — but it needs the migration applied, so it waits on that.
- **No integration test covers the new mappings.** Owned types, converters and shadow properties
  are the parts most likely to be subtly wrong, and only a real round-trip proves them. That test
  belongs with the migration.
- **`SourceRanking` does not resolve conflicts**, by design. Something must, eventually.
- **The registry is not persisted**, so it currently exists only for the lifetime of a process.
- **No freshness monitoring.** `UpdateCadence.IsOverdue` exists but nothing calls it; there is no
  scheduler and no staleness event. `IIngestionRunStore.GetLatestForSourceAsync` is the other half
  it will need.
- `ConfirmationState.Superseded` and `Conflicting` are defined but nothing produces them yet.
- **The retention policy for the raw archive is undecided.** Storing every response indefinitely
  is what makes replay possible and is also unbounded growth; some licences additionally cap how
  long data may be kept. This needs a decision before stage 5 puts real provider data into it.

## 14. Architectural decisions

| Decision | Rationale |
|---|---|
| The registry holds trust, never transport | Lets a source move from REST to bulk file without changing what the platform believes about it; also keeps credentials structurally out of the registry |
| Sources register **inactive** | Being assessed and being switched on are different statements |
| Reliability is measured, never declared | Otherwise every source is registered as excellent |
| `SourceId` is a readable slug, not a GUID | It is read by humans investigating provenance |
| Authority, reliability and verification policy are three separate concepts | They answer three different questions and conflating them loses all three |
| Licensing defaults to permitting nothing | Unknown terms must not read as permissive |
| `SourceRanking` orders but does not resolve | A resolver written before real conflicting data exists would be guessing |
| Recency excluded from source ranking | It is a property of an observation, not of a source |
| Ranking is pure and total, like the policy engine | "Which source do we believe?" must be reconstructable months later |
| `DataCategory` spans commerce and logistics from the start | The generalisation requirement is cheap now and expensive later |
| **Stage 2:** origin and locator are separate fields | A fused string cannot be a registry key, and two claims from one source must compare equal on their origin |
| **Stage 2:** the platform's own output is a registered source | One rule instead of two; a derived value whose producer cannot be named is one nobody can explain later |
| **Stage 2:** admission is a pure result, not an exception | Refusal is an ordinary outcome of a routine question; the caller quarantines or skips rather than unwinding a stack |
| **Stage 2:** `ISourceRegistry` returns unfiltered rows | Keeps licensing rules out of SQL and keeps the reason for an empty result visible |
| **Stage 2:** validate before mutating | An operation that fails must leave the aggregate unchanged |
| **Stage 3:** the ingestion subject is `(Kind, Identifier)`, not a typed reference | A `Ticker` here would narrow the data plane to equities and be torn out at the first product or supplier |
| **Stage 3:** the request fingerprint excludes time and correlation | A retry must be the same request, or the idempotency key prevents nothing |
| **Stage 3:** the raw archive is content-addressed | Makes byte-identical replay checkable, and deduplicates unchanged polls for free |
| **Stage 3:** the archive never parses what it stores | An archive that understood its contents would need migrating whenever a provider changed schema |
| **Stage 3:** `Refused` is separate from `Failed` | A refusal is the platform working correctly; counting it as an error teaches operators to ignore errors |
| **Stage 3:** refusals are written to the ledger | Otherwise the platform declining to ingest leaves no trace and looks like missing data |
| **Stage 3:** `PartiallySucceeded` is separate from `Succeeded` | A partial result read as complete is how gaps enter a history unnoticed |
| **Stage 3:** `IngestionOutcome.InProgress = 0` | The default value must never read as success |
| **Stage 3:** kept purely additive | Stages 1–2 are uncompiled; adding coupling on top of unverified changes compounds the correction |
| **Stage 4:** capabilities are declared, never discovered | A connector that learns its limits by trying discovers a provider's restrictions by violating them |
| **Stage 4:** the quota lives on the connector, not the source | A rate limit is a property of the API; what the platform believes about the source does not change with the transport |
| **Stage 4:** `IDataProvider` has no credential in its signature | Structural isolation beats a convention someone has to remember |
| **Stage 4:** an over-long window is refused, not sent | Providers answer them with silent truncation — a gap that looks like a complete answer |
| **Stage 4:** ingestion goes through `IActionGateway` | The kill switch, capability policy and audit trail then apply to data collection with no ingestion-specific safety code |
| **Stage 4:** the gateway returns instead of throwing | A scheduler ingesting fifty subjects must not lose forty-nine to one provider being down |
| **Stage 4:** the page bound is reported, not silent | A truncated run claiming success is indistinguishable from a complete one |
| **Stage 4:** failure reasons name only the exception type | The ledger is unredactable, and provider messages carry URLs with keys in them |
| **Stage 4:** `IngestionRun` joins the append-only exempt set | A refusal must be recordable precisely when nothing is authorised |
| **Stage 5:** the first source is the originating record, not a vendor | A summary of a filing is a report of it; provenance should point at the filing |
| **Stage 5:** the connector is absent when unconfigured, not anonymous | A placeholder identity would violate fair access on the first request |
| **Stage 5:** endpoint building is pure and separately tested | The part of a connector most likely to be wrong is the part a network test would obscure |
| **Stage 5:** `supportsWindow: false` | EDGAR has no period parameter; claiming otherwise would answer "one quarter" with "everything" |
| **Stage 5:** no HTTP in the test suite | A test run must not consume someone's fair-access quota |
| **Stage 5:** a sliding rather than fixed window | A fixed window permits double the declared rate across an interval boundary |
| **Stage 5:** duplicate connectors for one source are refused | Otherwise which one answers depends on registration order |
| **Stage 5:** the gateway is left unregistered | A container that cannot construct a registered service kills the host in Development |
| **Stage 7:** mapping strategy chosen per property, not uniformly | Queried fields deserve columns; identity-less sets deserve one `jsonb` column; single-value wrappers deserve converters |
| **Stage 7:** value objects read back through their factories | An invariant that holds only for objects the application created is not an invariant |
| **Stage 7:** no domain type was changed for the ORM | The private constructors already bound by parameter name; bending the model to the mapper is how domains rot |
| **Stage 7:** categories serialised by name, not enum value | A renumbered enum would otherwise silently re-categorise history |
| **Stage 7:** an unrecognised category is skipped on load | Rolling a category back must not make every source using it unreadable |
| **Stage 7:** the fingerprint is a shadow property | Derived, not domain state — but it must be indexed, and SQL cannot compute a SHA-256 |
| **Stage 7:** the archive is content-addressed | Idempotent writes, detectable tampering, exact replay — one property buys all three |
| **Stage 7:** archive writes are atomic | A truncated payload under a hash that no longer describes it is worse than a lost payload, because it is silently wrong |
| **Stage 7:** `DataSource` is not write-guard exempt | The registry is domain state; activating a source must pass through the seam |

## 15. Deviations from the approved plan

- **Stage 1 was implemented before the Phase 1 verification gate closed.** The approved sequence
  assumes a verified Phase 1. It was chosen knowingly, on the basis that pure domain code with no
  persistence or seam dependency cannot be invalidated by a Phase 1 runtime defect. It is recorded
  here as a deviation rather than left implicit.
- **Stage 2 went ahead of the Phase 1 verification gate**, like stage 1, but with less headroom:
  it modifies `Provenance`, a Phase 1 type.

- **Stage 7 was taken ahead of stage 6.** The roadmap orders normalisation before persistence, but
  normalisation operates on archived bytes and there was no archive; meanwhile every other stage
  was blocked behind three unimplemented storage contracts. Doing 7 first unblocked the gateway and
  gave stage 6 something to read. The roadmap's dependency was real; its ordering was not.

- **Stage 5 was reordered relative to the roadmap's wording.** The roadmap names it "first U.S.
  equity providers" and expects one market-data, one fundamentals and one news source. Only the
  fundamentals/regulatory slot is filled, deliberately: EDGAR needs no commercial decision, and the
  standing instruction is to prioritise authoritative free sources and not block implementation on
  a complete provider list. The other two remain outstanding and are one registration each.

- **Stage 3 also went ahead of the gate, and was scoped defensively because of it.** It defines
  contracts and adds no coupling to anything written in stages 1–2, so a build failure there
  cannot cascade into it. Stage 4 has no such option, which is why section 18 stops there.

- **Stage 3 defines contracts but no `IIngestionConnector`.** The provider-facing interface was
  left to stage 4 rather than guessed at here: designing the adapter before knowing which
  providers it adapts is how an abstraction ends up shaped like whichever API was imagined first.

- **Stage 4 modified two committed files**, having kept stage 3 additive. `IngestionRun.Refuse`
  was generalised to take a rule id and reason (the seam and the rate limiter refuse runs too, not
  only source admission), and `AppDbContext.IsSeamBookkeeping` gained `IngestionRun`. Both are
  small and both were necessary rather than convenient; the alternative to the second was a
  refusal that could not be written down.

- **A stray non-ASCII character was introduced and removed** while writing
  `IRawResponseArchive`'s documentation. A repository-wide scan now confirms `src/` and `tests/`
  are entirely ASCII.

- **Two test files were briefly written to the wrong path** (`src/AI.Investment.Domain/Sources/tests/…`)
  because a shell working directory had been left inside `src/`. Moved to
  `tests/AI.Investment.Domain.UnitTests/Sources/` and the stray tree deleted; absolute paths used
  since.

- **A partial-mutation defect was found and fixed in `DataSource` (2026-08-25).** Every mutator
  called `DateRange.EnsureUtc` and then `Touch(nowUtc)` last, and `Touch` was where the
  "modification cannot precede registration" rule lived. So `Activate` with an impossible
  timestamp set `IsActive = true` and *then* threw, leaving the aggregate mutated by a call that
  failed. The rule moved into `EnsureModificationFollowsRegistration`, called first by
  `Activate`, `Deactivate`, `RecordReliability`, `UpdateLicensing` and `UpdateCoverage`; `Touch`
  is now a pure assignment. Found while writing the test that asserts the throw — the test passed
  either way, which is exactly how this class of defect survives.

- **CA1826 fix (2026-08-25).** `SourceRanking.MostAuthoritative` called `FirstOrDefault()` on the
  `IReadOnlyList<DataSource>` returned by `MostAuthoritativeFirst`, which walks an enumerator to
  reach an element that is directly addressable. Rewritten to index the list
  (`ordered.Count > 0 ? ordered[0] : null`). Deriving the result from `MostAuthoritativeFirst`
  rather than scanning for a maximum was kept deliberately, so there is one definition of the
  ordering including its tie-breaks.

## 16. Dependencies on previous phases

- **Phase 1's epistemic model** is what the registry exists to serve: `Claim` and `Provenance`
  are where source identity ends up.
- **Phase 1's `AggregateRoot<TId>`**, domain exception hierarchy and `DateRange.EnsureUtc` are used
  directly by `DataSource`.
- **Phase 1's Action/Policy seam** is what ingestion will route through from stage 3 —
  `Capability.DataIngestion` already exists for exactly this.
- **Phase 0's configuration and secret pipeline** is where provider credentials will go from
  stage 4. No new mechanism should be invented.

## 17. Capabilities enabled for future phases

- Any claim can now name a registered origin with known authority, licensing and cadence.
- Ingestion can refuse to run against an inactive or unlicensed source before fetching anything.
- Multi-source agreement has a vocabulary (`VerificationPolicy.Classify` → `ConfirmationState`)
  and a deterministic ordering to build a resolver on.
- Freshness has a definition (`UpdateCadence.IsOverdue`) ready for a monitor to call.
- **Ingestion has a gate to call before it fetches anything** (`SourceAdmission`), so stage 3's
  contracts can assume licensing and coverage are already settled.
- **Every claim's origin resolves to a registry entry**, which is what stage 6's validation and
  stage 7's historical storage both need in order to answer "how much does this count?" from
  stored data alone.
- **Providers have a contract to satisfy** (stage 3), so stage 4 designs an adapter to a fixed
  shape rather than inventing the shape and the adapter at once.
- **Replay has an address space.** `ContentHash` plus `IRawResponseArchive` is the mechanism the
  phase's exit criterion is stated in terms of; stage 7 stores against it and Phase 7's backtesting
  reads from it.
- **Ingestion is already idempotent by construction.** `IngestionRequest.Fingerprint()` slots
  straight into the seam's existing idempotency key, so retry safety is inherited rather than
  rebuilt.
- **Refusals and partial results are visible**, which is what a freshness monitor and a source
  reliability score both need — and reliability scoring is how `DataSource.RecordReliability` stops
  being a declaration and becomes a measurement.
- **A connector is now a small, well-bounded thing to write** (stage 4): declare capabilities,
  fetch bytes. Every judgement about whether a fetch is allowed or possible has already been made
  in pure code that tests without a network, so stage 5 is transport and parsing only.
- **The whole data plane is testable without a network or a database** — the stage 4 tests
  exercise all four gates, the seam, paging, deduplication and failure against hand-written
  doubles.
- **Adding a provider is now one registration** (stage 5). Nothing above `ProviderCatalogue`
  enumerates connectors, so market data, news, macroeconomic series, product catalogues and future
  opportunity domains arrive without an architecture change — including paid providers, whose only
  additional need is a credential read inside Infrastructure.
- **There is a worked example to copy.** `SecEdgarProvider` shows the shape of a compliant
  connector: declare capabilities including a quota, identify yourself, fetch bytes, parse nothing.
- **Replay has a working mechanism** (stage 7), not just an address space. An analysis that records
  content hashes can be re-run against the exact bytes it originally read, which is what the phase's
  exit criterion and Phase 7's backtesting both depend on.
- **Freshness and reliability have their data source** (stage 7). `GetLatestForSourceAsync` returns
  the latest run whatever its outcome, so a monitor can distinguish "failing for a week" from
  "nobody asked" — and measured reliability can finally replace a declared grade.
- The category and region model already covers products, suppliers, commodities, FX and logistics,
  so the second domain does not require a foundation change.

## 18. Recommended next phase

### An open decision: raw archive retention

The archive is built and **deletes nothing**. That is the only direction that cannot lose evidence,
but it is not a policy, and it is now the last unresolved design question in the phase. The options
and their consequences are set out for a decision; none is implemented.

| Option | Storage | Replay | Licensing | Audit |
|---|---|---|---|---|
| **A. Keep everything, forever** | Unbounded. EDGAR alone is modest — one submissions document per company per fetch, deduplicated — but a market-data feed is not | Perfect, indefinitely | Fails any licence capping retention | Complete |
| **B. Keep everything for N years, then delete** | Bounded and predictable | Backtests older than N years become unreproducible — silently, unless refused | Satisfies most caps | A gap opens at the horizon |
| **C. Tiered by source authority** | Bounded, weighted toward what matters | Primary-source replay survives; vendor replay expires | Matches per-source terms, which is what licences actually attach to | Complete where it counts |
| **D. Keep only what a claim references** | Smallest | Perfect for anything analysed; nothing else is recoverable | Good | Cannot investigate what was fetched but never used |

**Recommendation: C, with a floor.** Retention belongs on `LicensingTerms`, because that is where
the obligation actually lives — a per-source `RetentionLimit` is enforceable by the same gate that
already refuses unlicensed ingestion, rather than by a global sweep that has to be kept in sync
with terms it cannot see. Primary regulatory sources like EDGAR are public-domain records with no
cap, so they keep everything; a vendor with a 12-month clause gets 12 months, enforced rather than
promised.

The floor matters as much as the tiering: **never delete a payload a stored claim still
references**, regardless of age. Deleting it does not save the analysis that used it — it just
makes that analysis permanently unexplainable. Where a licence requires deletion of something a
claim references, the claim should be marked unreplayable rather than the deletion silently
skipped, so the gap is visible.

D is tempting and wrong: "fetched but never used" is exactly what you need when investigating why
something was *missed*. B is the simplest to implement and the easiest to regret, because the
failure is silent — a backtest just quietly covers less history than it claims.

Once decided, this is a `RetentionLimit` on `LicensingTerms`, a sweep that consults it, and a
refusal path for claim-referenced payloads. **No code assumes any answer yet.**

### Then, in order

1. **Generate and validate the migration** (below) — the largest remaining gap.
2. **A source-registration command**, routed through the seam under
   `Capability.ReferenceDataManagement`, plus seeding the EDGAR definition. Until this exists the
   registry is empty and every run refuses. Nominally stage 9, but it is what makes stage 7
   demonstrable.
3. **Stage 6 — normalisation and validation.** It now has archived bytes to read.
4. **Stage 8 — freshness and data events**, which `GetLatestForSourceAsync` and
   `UpdateCadence.IsOverdue` were built for.
5. **Stages 9 and 10** — API surface, observability, and architecture tests for the data plane.

### Verification batch

```
cd C:\Users\localadmin\Desktop\AI-Investment-Analyst
dotnet restore AI-Investment-Analyst.sln
dotnet build AI-Investment-Analyst.sln -c Debug
dotnet test AI-Investment-Analyst.sln -c Debug
dotnet ef migrations add DataPlane --project src\AI.Investment.Infrastructure --startup-project src\AI.Investment.Api
dotnet ef database update --project src\AI.Investment.Infrastructure --startup-project src\AI.Investment.Api
```

The migration step is the one that exercises the new mappings — owned types, converters and the
shadow property are where an EF configuration written without a compiler is most likely to be
wrong, and `migrations add` is what surfaces that.

### Still outstanding before Phase 2 can close

- The build, test and migration gates, none of which has ever run.
- Provider slots for market data, news and macroeconomic series — one registration each, no
  architecture change.
- The Phase 2 exit criterion: *"50 tickers ingested with full provenance, and any analysis replays
  byte-identically from stored raw responses."* Reachable with EDGAR alone once the migration lands
  and a source can be registered.
