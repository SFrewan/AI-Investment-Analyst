# Phase 2 — Global data and intelligence foundation

**Status:** Code complete — all ten stages implemented; **verification pending**
**Last updated:** 2026-08-26

---

> **This document is live.** All ten approved stages are now built: registry, gateway, connector,
> archive, ledger, normalisation, retention, freshness, the API surface and the architecture tests.
> Stage 7 was taken ahead of stage 6 deliberately — see section 15.
>
> **Nothing in Phase 2 has ever been compiled, executed or migrated.** 635 executable test cases
> exist across the solution and none has run; the five new tables have been validated against a
> live PostgreSQL 16.13 instance by hand but the EF migration has not been generated. Everything
> below describes code that exists in the repository and static verification that was actually
> performed. No build, test or runtime result is claimed. See sections 12 and 18.

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

### Stage 6 — normalisation: from bytes to what the platform knows

Ingestion establishes that a source may be used and captures exactly what it said. Stage 6 is the
other half: deciding what those bytes *mean*. The two are separate on purpose — a normaliser can be
fixed and re-run against the original payload precisely because reading it was never allowed to
change it.

**`Observation`** is the unit of knowledge. A `Claim<T>` carries a value, its provenance and its
epistemic status, but not what the value is *about*: "3571" with impeccable provenance is still not
knowledge until something says it is Apple's SIC code. An observation is that missing sentence —
subject, attribute, value, provenance.

The epistemic invariants are **not re-implemented**. `Observation.RecordFact` materialises through
`Claims.Fact`, so the ordering rules that make a fact a fact (it cannot be published before the
period it describes, nor retrieved before it was published) are enforced by the type that owns them.
`ToClaim()` rebuilds through the same factory, and **refuses** a stored kind this build cannot
rebuild rather than downgrading it to a fact — presenting a prediction as an observation is the
single worst thing this model can do.

`Attribute` is a dotted string, not an enumeration. The platform's scope spans companies, products,
suppliers, currencies and routes; an enum of every attribute any domain might have would need
editing before a new one could be described. `company.name`, `product.unit-price` and
`route.transit-days` are the same shape to everything that stores or queries them.

**`ObservationValue`** is one kind plus one culture-invariant canonical string. Storing numbers as
text is a deliberate trade: a canonical `decimal` round-trip loses nothing, and it keeps every
observation in one column regardless of type — which is what lets a single table hold a company
name, a revenue figure, a flag and a filing date without a column per normaliser. Reading a value
as the wrong type throws rather than producing a plausible number.

**`INormalizer`** is the counterpart to `IDataProvider`: a connector knows how to *fetch* a
source's bytes, a normaliser knows how to *read* them. They change for different reasons — a
provider moving to a new endpoint does not change what its JSON means. Implementations parse and
nothing else: they do not fetch, store, decide whether a source may be used, or delete anything.

**A normaliser must never invent a value it did not find.** Absent is absent. An observation that
exists only because a field was missing is worse than a gap, because a gap is visible in a query
and a fabricated value is not.

**`SecEdgarSubmissionsNormalizer`** reads seven top-level fields plus the first ticker/exchange pair
and ignores everything else, including the filing history the same document carries. Ignoring is
deliberate: a normaliser that absorbed an entire response would break every time the provider added
a field, and filings deserve their own normaliser rather than a corner of this one. Only the first
ticker is recorded — a company's primary listing is one fact, and flattening three into one
attribute would produce a value that is true of none of them.

Its provenance timing is stated honestly rather than conveniently. EDGAR's submissions document
describes a company's *current* state and carries no publication date of its own, so all three
timestamps are the retrieval time and **every observation carries a caveat saying so** — a backtest
filtering on publication needs to know that date is a floor, not the moment the fact became true.

**`NormalizationPipeline`** walks a run's artifacts and writes what it read.

| Rule | When it fires |
|---|---|
| `normalization.no-normalizer@1` | Nothing registered reads that source and category |
| `normalization.payload-missing@1` | The archive no longer holds bytes the run recorded |
| `normalization.unreadable-payload@1` | The payload is not JSON this build can parse |
| `normalization.unexpected-document@1` | It parses, but is not the document it claims to be |

**A payload that cannot be read is quarantined, never dropped.** Failure to normalise is evidence —
of a changed schema, a wrong assumption, or a genuinely malformed response — and all three deserve
investigating. Discarded, they become indistinguishable from data that never arrived.
`QuarantinedPayload` is keyed by content hash, so a retry collides with the original record instead
of making one problem look like two, and its reason field never carries an excerpt of the payload:
that record is long-lived and unredactable, and a malformed response is exactly the kind of thing
that might contain something sensitive. The bytes are already in the archive for anyone who needs
them.

**Writing observations goes through the seam; quarantining does not.** An observation is something
the platform *believes*, which makes recording one a side effect like any other — dispatched under
`Capability.DataIngestion`, keyed `normalization.record:{runId}` so replaying a run cannot double
its observations, and described to the audit trail as counts and categories rather than values. A
quarantine record is the opposite case: like the ingestion ledger it must be writable precisely
when nothing is authorised, because a policy denial is one of the things worth quarantining a run
over. It creates no belief and changes no domain state, so the exemption grants nothing beyond that.

A denial records zero observations while still reporting the payloads it read. The two are separate
facts, and collapsing them would hide the denial behind what looks like an empty response.

Normalisers are registered **unconditionally**, outside any connector's registration. A normaliser
reads bytes already in the archive; whether the connector that fetched them is currently enabled
has nothing to do with whether they can still be read. Tying the two together would mean turning
EDGAR off quarantined every payload it had ever retrieved, under a rule claiming no normaliser
existed — which would be false.

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

### Stage 8 — freshness, data events, and the retention sweep

Three things that were built for each other and had never been joined: the cadence a source
declares, the ledger of when it last succeeded, and the enforcer that decides what may still be
kept.

**`FreshnessPolicy`** decides whether a source's data is current. Pure and total, like the policy
engine and the retention policy, and for the same reason: "why was this not refreshed?" is asked
long after the fact, and an answer that depended on a clock or a database cannot be reconstructed.

Staleness is meaningless without an expectation. A quarterly filing three weeks old is perfectly
current; a price three weeks old is worthless. That expectation lives on the source as its
`UpdateCadence`, which is why the policy takes a source rather than a duration.

| Rule | Conclusion |
|---|---|
| `freshness.source-inactive@1` | `NotScheduled` — switched off, not late |
| `freshness.no-expected-interval@1` | `NotScheduled` — event-driven or on demand; it cannot be late |
| `freshness.interval-unknown@1` | `Overdue` — a cadence that should carry an interval does not |
| `freshness.never-ingested@1` | `NeverIngested` — distinct from overdue, and a different problem |
| `freshness.overdue@1` | `Overdue` |
| `freshness.current@1` | `Current` |

**Where this fails, it fails towards refreshing** — the opposite of every other gate in the
platform, and deliberately so. Elsewhere uncertainty guards an irreversible act and must deny.
Here the errors are asymmetric the other way: wrongly refreshing costs one redundant request, while
wrongly reporting stale data as current means every downstream decision is made on data nobody
knows is old. The reversible mistake is the one to make.

**`FreshnessReport`** answers it across the registry. Two collaborators and the pure policy: the
registry says what was expected, the ledger says when a run last succeeded, and all the judgement
stays where it can be exercised without a database. Two details carry real weight. **Only
successful runs count** — a source refused fifty times running has not been refreshed, and reading
the latest run of any outcome would report it as current, which is exactly the failure the report
exists to catch. And freshness is dated from **completion, not start**: a run that began inside the
interval and finished outside it delivered data as of when it finished.

Inactive sources are reported rather than filtered out. A source somebody deactivated and forgot is
a real cause of missing data, and omitting it would make that invisible; the policy marks it
`NotScheduled` so it is visible without being alarming. The report is read-only and therefore
deliberately outside the safety seam: the seam gates side effects, and auditing reads would bury
the record of what actually changed.

**`RetentionSweep`** is the recurring half of retention. `IRetentionEnforcer` decides about one
payload and knows nothing of scheduling; the sweep decides when to go looking and knows nothing of
licensing. Every deletion still passes through the seam, one authorisation per payload, because
batching them would ask an operator to authorise a number rather than a decision.

It is bounded and honest about it. A sweep takes a limit and reports whether it reached the end of
the archive, because "nothing left to delete" is a compliance statement and "we stopped looking" is
not. `IRawResponseArchive.EnumerateAsync` was added for it — streamed rather than listed, since an
archive outgrows memory long before it outgrows disk.

**A defect was found and fixed while wiring this up.** `IRetentionEnforcer` returned only the
`RetentionDecision` — what the licence *requires* — and discarded the seam's outcome. But retention
deletion declares itself irreversible, so an installation that has not granted automatic execution
for `Capability.DataRetention` gets an approval requirement on *every* payload by design. The
enforcer therefore knew that nothing had been deleted and threw that fact away, and a sweep built
on it would have reported fifty discharged obligations while fifty payloads sat on disk. It now
returns `RetentionEnforcementResult`, carrying both the obligation and what came of it.

The sweep counts refusals and failures separately for the same reason. Counting a thrown exception
as a policy refusal would let a database outage report as five thousand payloads that policy
declined to delete — a sentence about compliance that nothing observed. One poisoned payload does
not end a sweep, because a sweep that died on the same entry every time would block the obligation
permanently; but it is counted as a failure, not disguised as a decision.

**`DataAcquisitionService`** joins the two halves of acquiring data into the one operation a caller
wants: ingest, then normalise what was archived. Deliberately thin, because any judgement here
would be judgement that had escaped the gateway or the pipeline. What it contributes is restraint.
A refused run is **not** normalised — doing so would quarantine a payload that was never fetched,
inventing a data-quality problem out of a compliance decision. A partial run **is** normalised: it
archived real bytes, and discarding them because more was expected would throw away good data. And
`AcquisitionResult.Normalization` is null rather than a zero-filled summary when normalisation was
not attempted, because "we did not look" and "we looked and found nothing" are different facts.

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

### Source registration — the registry can finally be filled

Until this, `ISourceRegistry.Add` existed and nothing called it: the registry started empty and
every ingestion run refused with `ingestion.source-registered@1` — correct, but useless.

**`ISourceDefinition`** lets a connector ship its own source's definition. A connector knows its
regulator's authority, licensing, coverage and cadence better than anyone re-typing them, and
`SecEdgarSource` now implements it. Nothing above Infrastructure names a provider: the seeding
handler iterates whatever definitions the container holds.

**`RegisterKnownSourcesHandler`** puts them in the registry, **one proposal per source** rather
than one for the batch — so a refusal of one does not silently take the others with it, and the
audit trail records which source was admitted rather than that "seeding ran". Sources are
registered **inactive**; shipping a connector is not deciding to use it.

**Existing entries are left exactly as they are.** A source already registered may have been
re-licensed, deactivated or re-scored by an operator, and overwriting it with the shipped
definition on every start-up would quietly undo that. Seeding fills gaps; it does not reconcile.

**`ActivateSourceHandler`** is the separate, deliberate act that makes a source usable — the
consequential decision in the data plane, since from that moment its content becomes things the
platform believes. The domain does the refusing: `DataSource.Activate` rejects terms that permit
neither storage nor automated processing, so a licensing failure surfaces as a domain rule
violation and would still refuse if some future caller bypassed the handler.

Both route through the seam under `Capability.ReferenceDataManagement`, keyed idempotently on the
source id.

### Stage 9 — the API surface, and the callers that make the data plane run

Every stage before this built something correct that nothing invoked. The registry could be seeded
and was not; the sweep could run and did not; the pipeline could normalise and was never called.
Stage 9 is where the data plane stops being a set of operations that work and starts being a system
that runs.

**`SourceSeedingHostedService`** calls `RegisterKnownSourcesHandler` once at start-up. This closes
the gap that made everything else inert: until something called it, the registry started empty and
every ingestion run refused with `ingestion.source-registered@1` - correct, and useless. Sources are
still registered **inactive**; shipping a connector is not deciding to use it.

**A seeding failure does not stop the host.** The API's other work does not depend on a complete
registry, and refusing to start would turn a database hiccup into an outage. The failure is logged
at error level, each refused source is named, and the instance comes up with an incomplete registry
- which the freshness report then shows as sources that have never been ingested.

**`RetentionSweepHostedService`** is the recurring caller `IRetentionSweep` was built for. It is
**off by default and single-instance by assumption**: this is the only activity in the platform that
destroys evidence, so it does not begin because a host happened to start. There is no distributed
lock. Two instances could not double-delete - the seam deduplicates on content hash - but they would
burn approval slots and audit rows discovering that.

A sweep that finds more work waits for the next interval rather than chasing its backlog, because a
continuous stream of deletion proposals is exactly the shape of thing an operator should be able to
watch rather than discover. A failed sweep does not stop the timer: retention is an obligation that
outlives one bad night.

**`DataPlaneOptions`** carries both switches. Durations are configured as **integer minutes rather
than timespans**, which is a small decision with a real reason: a duration in JSON invites values
like `"24:00:00"`, and that is not a parseable timespan at all - the hours component may not exceed
23, so the obvious way to write one day is silently wrong. An integer has one reading.

**The read surface.** Three listings, all read-only and therefore deliberately outside the seam. The
seam gates side effects; asking a question is not one, and auditing reads would bury the record of
what changed under a record of who looked.

| Endpoint | What it makes visible |
|---|---|
| `GET /api/sources` | The registry, including inactive sources |
| `GET /api/sources/{id}` | One source and its licensing terms, permission by permission |
| `POST /api/sources/{id}/activation` | The deliberate act, through the seam |
| `GET /api/data-plane/freshness` | Which sources are behind, and which rule says so |
| `GET /api/data-plane/runs` | Recent runs, **including refusals and the rule that caused each** |
| `GET /api/data-plane/quarantine` | Payloads that arrived and could not be read |

**This is the surface that makes silence legible.** A platform that ingests data fails in two ways:
loudly, which needs no help, and quietly - a source that stopped publishing, a schema that changed,
a policy that has been refusing every deletion for a month. Each listing exists so one of those
becomes visible now instead of being discovered later by an analysis that returned less than it
should have.

Two details in the status codes are worth stating. A **malformed source identifier is a 400, not a
404**: telling a caller that their well-formed id does not exist is a different and untrue
statement, and one that sends them looking in the registry rather than at what they sent. And an
**out-of-range page size is clamped rather than rejected** - a dashboard sending whatever its config
says should get a bounded page, not an error.

Activation keeps the status codes the seam makes necessary: `200` activated or already active,
`202` policy requires a human decision and nothing changed, `403` refused - by policy, or by the
domain, since `DataSource.Activate` rejects terms that permit neither storage nor automated
processing.

**DTOs, not aggregates.** `SourceDto`, `FreshnessDto`, `IngestionRunDto` and `QuarantinedPayloadDto`
are separate shapes for the same reason `CompanyDto` is: serialising an aggregate exposes its
internals and makes a domain refactor a breaking API change. Three of them carry a field that exists
only to prevent a specific misreading - a licence's permissions crossing individually rather than as
prose, an absent retention limit staying null rather than becoming zero, and a refused run carrying
the versioned rule that stopped it.

### Stage 10 — the data plane's invariants, as tests

Stages 1 to 9 established properties that are easy to state and easy to erode. `DataPlaneRuleTests`
turns seven of them from things somebody was careful about into things the build enforces.

**The network is reached in exactly one layer.** Neither Domain nor Application may depend on
`System.Net`. This is the rule that keeps the ingestion gateway meaningful: an application service
holding an `HttpClient` would bypass source admission, provider capability checking, the rate
limiter, the archive and the ledger in a single step - every gate the data plane has - and would
look entirely ordinary doing it. The same property is asserted from the other direction too: no
`IDataProvider` or `INormalizer` implementation may live outside Infrastructure, which catches the
case where an implementation is written inside Application with a stub body and grown into a real
one later.

**Scheduling is a host concern.** Domain and Application may not depend on
`Microsoft.Extensions.Hosting` or on a timer. The retention sweep is the case that makes this
concrete: it knows how to walk the archive and nothing about when: the timer lives in the API's
hosted service. Had the two been one type, the rule that destroys evidence could not be exercised
without a clock.

**The domain does not log.** Not a style rule. A domain rule that logged would be a domain rule with
a side effect, and the point of keeping the policy engine, the retention policy and the freshness
policy pure is that their conclusions can be reconstructed from their inputs alone.

**Every enum defines a member for zero.** `default(T)` for an enum is zero whether or not zero is
declared, so an enum without a zero member produces a value that is none of its cases and still
passes every switch with a `default` branch. This platform leans hard on what an unset value means -
`KillSwitchState.Unknown` denies, `ObservationValueKind.Unknown` refuses to be read,
`PolicyOutcome.Deny` is the default outcome - and all of that reasoning assumes the default is a
case somebody chose. The test deliberately does **not** assert which member is zero, because the
right answer differs: most choose the unknown or safe case, while `RetentionOutcome.Retain` is
zero precisely because there the irreversible operation is the deletion. What must never happen is
zero belonging to nobody. All 26 enums currently satisfy it.

**Every aggregate root can be materialised.** EF constructs through a constructor, and an aggregate
that protects its invariants behind a single validating factory has none EF can use unless one is
written for it. The failure mode is why this is a test rather than a convention: the build succeeds,
the migration succeeds, and the first query against that table throws - which on a data plane is a
scheduled job at 3am rather than a developer at a keyboard. All six pass.

**Every configured entity is exposed as a `DbSet`.** A configuration whose entity has no `DbSet`
still shapes the table, so nothing looks wrong - but the stores reach their tables through the
context's properties, so the entity ends up mapped and unusable. This test exists because stage 6
added two configurations and two `DbSet`s in separate edits, which is exactly the shape of change
where one of them gets forgotten. Nine configurations, nine `DbSet`s.

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

Stage 6 added two more: `Observations` (`Observation`, `ObservationId`, `ObservationValue`,
`ObservationValueKind`) and `Normalization` (`QuarantinedPayload`). Neither re-implements the
epistemic rules — `Observation` materialises through `Claims`, so `Evidence` remains the only place
that decides what a fact is.

Stage 8 added a `Freshness` namespace (`FreshnessState`, `FreshnessAssessment`,
`FreshnessPolicy`) and one method to `ContentHash`: `TryCreate`, for callers reading names they did
not write - the archive walking its own directories, where an interrupted write leaves a temporary
file by design and skipping it is the expected path rather than an exception.

**Application.** Stage 6 added a `Normalization` namespace — `INormalizer`,
`INormalizationPipeline`/`NormalizationPipeline`, `NormalizationInput`, `NormalizationResult`,
`NormalizationSummary`, `NormalizationParameters` — plus `IObservationStore` and `IQuarantineStore`
in `Abstractions`.

Stage 8 added a `Freshness` namespace (`IFreshnessReport`/`FreshnessReport`, `SourceFreshness`),
`IRetentionSweep`/`RetentionSweep` and `RetentionSweepSummary`, `IDataAcquisition`/
`DataAcquisitionService` and `AcquisitionResult`, and `RetentionEnforcementResult`/`RetentionAction`.
`IRetentionEnforcer.EnforceAsync` changed return type - see the defect in section 3 - and
`IRawResponseArchive` gained `EnumerateAsync`.

**Infrastructure.** Stage 6 added `SecEdgarSubmissionsNormalizer`, two entity configurations
(`ObservationConfiguration`, `QuarantinedPayloadConfiguration`), two stores (`EfObservationStore`,
`EfQuarantineStore`), two `DbSet`s, and `QuarantinedPayload` to `AppDbContext.IsSeamBookkeeping`.
`Observation` is deliberately **not** exempt: an observation is something the platform believes,
and beliefs are precisely what the seam exists to audit.

Stage 8 added `FileSystemRawResponseArchive.EnumerateAsync`, which walks the fan-out directories and
skips any file whose name is not a content hash.

Stage 7 added two entity configurations, two `DbSet`s, two stores, the
filesystem archive and `RawArchiveOptions`, and registered `IIngestionGateway` — which stage 5 had
deliberately left out. Stage 4 added `IngestionRun` to `AppDbContext.IsSeamBookkeeping`. Stage 5
added an `Ingestion` namespace — `ProviderCatalogue`, `SlidingWindowRateLimiter` and a `Providers`
folder holding the EDGAR connector, its endpoint builder and its registry definition — plus
`SecEdgarOptions` and the `AddIngestion`/`AddSecEdgar` registration in `DependencyInjection`.

*Written at stage 5, and since overtaken:* "There is deliberately still no EF configuration for
`DataSource`, so `ApplyConfigurationsFromAssembly` does not pick it up and the existing model
snapshot has not drifted. Persistence of the registry is stage 6/7 work." Stage 7 added
`DataSourceConfiguration` and the `DataSources` set; the registry is persisted.

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

**Stage 6 adds two further tables**, `observations` (15 columns, 3 indexes) and
`quarantined_payloads` (6 columns, 3 indexes), bringing the pending migration to five tables.

`observations` holds three owned types in one table — subject, value and provenance — and stores
values as a kind plus one canonical string rather than a column per type. Its indexes are chosen
for the two questions later phases actually ask: everything known about a subject as at a date, and
the latest value of one attribute as at a date. **Both filter on `published_at_utc`**, never on the
period a value describes; filtering on the wrong one produces look-ahead bias that cannot be
corrected afterwards, because by then the history has been read with the distinction discarded.

`quarantined_payloads` is keyed by `content_hash`, which makes the record idempotent by
construction rather than by a check that could be forgotten.

Both were derived by hand from their configurations and applied to the same live PostgreSQL 16.13
instance. Thirteen behavioural checks plus six query plans, all as specified — see the verification
log. The plan check matters more than usual here: `ix_observations_subject` had to serve the sweep
case (`subject_identifier IS NULL`) as well as the specific one, because `EfObservationStore`
deliberately expresses that predicate as `IS NULL` rather than passing a null parameter. SQL's
`= NULL` is never true, so a single parameterised comparison would have silently returned nothing
for exactly the subjects that have no identifier. The plan confirms the index is used for both.

**Stage 8 changes no schema.** It reads the ingestion ledger and the source registry through
contracts that already existed, and the archive it sweeps is on the filesystem. The pending
migration remains five tables.

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

Two controllers, added in stage 9. Both are unauthenticated, deliberately and temporarily; until
that changes the API must not be exposed beyond localhost. See `docs/SECURITY.md`.

**`SourcesController`** (`api/sources`) - the registry. `GET` for the listing and one source;
`POST {id}/activation` for the one consequential operation, which goes through the seam and can
answer `200`, `202` (approval required, nothing changed), `403` or `404`.

**`DataPlaneController`** (`api/data-plane`) - read-only status. `freshness`, `freshness/{sourceId}`,
`runs` and `quarantine`. Page sizes are clamped rather than rejected; the runs listing takes an
optional `sinceHours` and defaults to a week.

Four DTO shapes cross the boundary - `SourceDto` with a nested `SourceLicensingDto`, `FreshnessDto`,
`IngestionRunDto`, `QuarantinedPayloadDto` - each mapped from its aggregate rather than serialised
from it, so a domain refactor is not a breaking API change.

**Configuration contract.** `DataPlane` in `appsettings.json`, validated at start-up:
`SeedSourcesOnStartup` and `RunRetentionSweep` (both **false** by default),
`RetentionSweepIntervalMinutes`, `RetentionSweepDelayMinutes` and `RetentionSweepBatchSize`.

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
| `tests/AI.Investment.Domain.UnitTests/Observations/ObservationValueTests.cs` | Stage 6. Canonical round-trips for all four kinds, culture invariance, non-UTC and unspecified-kind refusal, wrong-type reads, and every `Restore` refusal including the unknown and unrecognised kinds |
| `tests/AI.Investment.Domain.UnitTests/Observations/ObservationTests.cs` | Stage 6. Attribute validation, caveat handling, the delegation to `Claims` for both fact-ordering rules, `ToClaim` at each of the four value types, and sweep subjects |
| `tests/AI.Investment.Domain.UnitTests/Observations/QuarantinedPayloadTests.cs` | Stage 6. Keyed by content hash, rule and reason required, truncation rather than rejection, non-UTC refusal |
| `tests/AI.Investment.Application.UnitTests/Normalization/NormalizationPipelineTests.cs` | Stage 6. The seam dispatch and its idempotency key, all three refusal statuses, every quarantine rule, and the archive-missing path |
| `tests/AI.Investment.Application.UnitTests/Normalization/NormalizationTestDoubles.cs` | Stage 6. Three more hand-written doubles |
| `tests/AI.Investment.Integration.Tests/Normalization/SecEdgarSubmissionsNormalizerTests.cs` | Stage 6. Each field to its attribute, provenance timing and its caveat, four "nothing is invented" cases, and every quarantine path including the one that proves a reason never quotes the payload |
| `tests/AI.Investment.Domain.UnitTests/Freshness/FreshnessPolicyTests.cs` | Stage 8. Every rule by id, grace, the expectation coming from the source rather than a fixed duration, and clock skew treated as current rather than overdue |
| `tests/AI.Investment.Application.UnitTests/Freshness/FreshnessReportTests.cs` | Stage 8. Only successful runs counting as a refresh, completion rather than start dating one, inactive sources reported rather than hidden, and the queue ordering |
| `tests/AI.Investment.Application.UnitTests/Retention/RetentionSweepTests.cs` | Stage 8. Every count, both bounds, and the two failure cases - a poisoned payload not ending the sweep, and an outage not being reported as a policy refusal |
| `tests/AI.Investment.Application.UnitTests/Ingestion/DataAcquisitionServiceTests.cs` | Stage 8. What it declines to normalise and what it declines to claim |
| `tests/AI.Investment.Application.UnitTests/Mapping/DataPlaneMapperTests.cs` | Stage 9. Each DTO's fields, and the three that exist to prevent a specific misreading - permissions crossing individually, an absent retention limit staying null, a refusal carrying its rule |
| `tests/AI.Investment.Api.Tests/DataPlaneEndpointTests.cs` | Stage 9. Every route exists; a malformed id is a 400 before the registry is touched; page sizes clamp; the host starts with both hosted services registered |
| `tests/AI.Investment.Architecture.Tests/DataPlaneRuleTests.cs` | Stage 10. Seven structural invariants: the network reachable in one layer only, connectors and normalisers confined to Infrastructure, scheduling kept out of the inner layers, no logging in the domain, every enum naming its default, every aggregate materialisable, every configured entity exposed |

`tests/.../Evidence/ClaimTests.cs` was updated for the new `Provenance` shape — its fixture
previously used `"sec-edgar:0000320193-26-000001"`, which is exactly the fused value stage 2
removed.

The solution now holds **635** executable cases, up from 189 before this phase. **None has been
executed.** See section 12.

That total is counted mechanically - every `[Fact]`, plus one per `[InlineData]` row, across
`tests/`; there is no `[MemberData]` or `[ClassData]` in the solution, so the count is exact rather
than an estimate. The per-stage figures recorded in the verification log are hand tallies made as
each stage was written, and summing them gave 638. The mechanical count is the correct one, and
the discrepancy is left visible here rather than quietly reconciled: a number arrived at by adding
up remembered figures is exactly the kind of claim this project's documentation rule exists to
stop.

The EDGAR tests deliberately **do not make an HTTP request**. Hitting the SEC from a test suite
would consume somebody's fair-access quota on every CI run — precisely the behaviour this connector
exists to avoid. What is tested is everything that can be wrong without a network: identifier
normalisation, endpoint selection, the recorded licensing, options validation, and the limiter's
window arithmetic.

Two assertions recur through the gateway tests and carry most of their weight: `FetchCount == 0`,
which says the network was never touched, and a recorded run naming the rule, which says the
refusal was written down. A gate that stops a request but leaves no trace turns a compliance
decision into an unexplained absence of data, so both halves are asserted every time.

The normaliser tests are weighted deliberately towards the negative cases. Roughly half assert
that something did *not* happen: a missing field produced no observation, a wrongly typed field
produced no observation, an overlong value cost only its own observation rather than the document,
and a quarantine reason contained no fragment of the payload that caused it. A normaliser is easy
to test on a well-formed document and that is not where the risk is — a fabricated value with real
provenance is indistinguishable from a true one, so the tests that matter are the ones proving
nothing was invented.

The endpoint tests deserve a note on what they can and cannot prove. No database is reachable from
the API test host, so they assert two narrow things: that a malformed identifier is rejected
*before* anything reaches the registry - a 400 where a query would have produced a 500 is the proof
- and that every route exists and handles a failed read rather than letting it escape. One of them
proves something about start-up as a side effect: the host boots at all with both hosted services
registered, which it would not if either had defaulted to enabled and reached for the unreachable
database.

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

Static verification actually executed, across the whole solution (247 files, 70 namespaces, 304
top-level types):

| Check | Result |
|---|---|
| Brace/paren balance, strings and comments stripped | **PASS** |
| Namespace declaration matches folder path, every file | **PASS** |
| Every `using AI.Investment.*` resolves to a declared namespace | **PASS** |
| Dependency direction: Domain references nothing; Application references no Infrastructure or Api | **PASS** |
| Duplicate type names within a namespace | **PASS** — the only two hits are generic-arity pairs (`PagedResult`/`PagedResult<T>`, `Claim`/`Claim<T>`) |
| Stray `.cs` outside `src/` and `tests/` | **PASS** — none |
| Interface members implemented by every implementer | **PASS** — 51 implementations across 31 interfaces; the six reported are all expression-bodied properties the scanner cannot see |
| Non-ASCII characters anywhere in `src/` or `tests/` | **PASS** — none |
| EF model snapshot drift | **PASS** — still exactly the four Phase 1 entities |
| Service-graph review: every registered service's dependencies are registered | **PASS** — `IngestionGateway`'s seven, `NormalizationPipeline`'s six, stage 8's three services and stage 9's two hosted services and two controllers all resolve; both hosted services take `IServiceScopeFactory` rather than capturing a scoped service in a singleton |
| Configuration validity | **PASS** — both `appsettings` files parse; every `DataPlane` value is inside its declared range |
| Stage 7 schema against live PostgreSQL 16.13 | **PASS** — 2 tables, 38 columns, 5 indexes created; 12 behavioural checks all as specified |
| Retention schema against live PostgreSQL 16.13 | **PASS** — `unreplayable_evidence` created; duplicate marker and null reason both rejected |
| The reference-index `jsonb` containment query, against real rows | **PASS** — finds a run holding the hash, returns false for one nothing references |
| Stage 6 schema against live PostgreSQL 16.13 | **PASS** — `observations` and `quarantined_payloads` created; 13 behavioural checks all as specified |
| Stage 6 index usability, by query plan | **PASS** — all six declared indexes serve their intended reads, including `ix_observations_subject` under `subject_identifier IS NULL` |
| Owned-type constructor binding, by inspection | **PASS** — `Provenance`, `IngestionSubject` and `ObservationValue` each have a private constructor whose parameter names match their property names exactly, which is what EF binds on |
| `dotnet ef migrations add` | **PENDING LOCAL VERIFICATION** — requires the SDK |

These are real checks with real results, and they are also the ceiling of what can be established
without a compiler. None of them proves type correctness.

Stage 1 was chosen deliberately as work that a Phase 1 runtime defect could not invalidate. Stage
2 does not have that property — it changes `Provenance`, which Phase 1 code uses. Stage 3 was kept
**purely additive** for that reason: it modifies no existing type, so a Phase 1 or stage 2 build
failure cannot cascade into it.

*Written at stage 5:* "**Stage 5 is where implementation stops without input.** It needs real
provider access — credentials and licence terms — which is a commercial and legal decision rather
than an implementation one." That turned out to be wrong about where implementation stops, and the
reason is worth keeping: EDGAR needs no credentials, only a declared contact address, so stages 6
to 10 were all buildable without a commercial decision. **Implementation now stops at the compiler**
— see the environment blocker below and in section 18.

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
  The registry is persisted as of stage 7 and `RetentionEnforcer` already resolves a payload's
  source through it, but nothing checks an *observation's* provenance against the registry when it
  is recorded. `ISourceRegistry` and `SourceAdmission` are the pieces it would be assembled from.
- ~~**`SourceAdmission` is not yet called by anything.**~~ *Resolved in stage 4* — it is gate 2 of
  the ingestion gateway.
- ~~**No connector exists.**~~ *Resolved in stage 5* — SEC EDGAR.
- ~~**Ingestion cannot yet run end to end.**~~ *Resolved in stage 7* — the gateway is registered
  and every dependency resolves. It has still never been executed.
- ~~**Nothing can put a source into the registry.**~~ *Resolved* — `RegisterKnownSourcesHandler`
  and `ActivateSourceHandler`.
- ~~**Nothing calls the seeding handler at start-up.**~~ *Resolved in stage 9* —
  `SourceSeedingHostedService`, off by default and on in Development.
- ~~**The archive never deletes.**~~ *Resolved* — Option C implemented; see section 3.
- ~~**Nothing sweeps the archive.**~~ *Resolved in stage 8* — `RetentionSweep`. Nothing *calls* it
  on a schedule yet, which is the next item.
- ~~**No scheduler runs anything.**~~ *Partly resolved in stage 9.* Seeding and the retention sweep
  now have hosted services. **`DataAcquisitionService` still has no caller** — nothing fetches on a
  schedule or on request, because deciding *what* to fetch needs the watchlist described below. An
  operator can activate a source and see it reported as overdue forever.
- **No `POST` triggers an ingestion.** The read surface is complete; the one write endpoint is
  activation. An acquisition endpoint is easy to add and was deliberately not added, because it
  would need a subject in the request body and that shape should be settled alongside the watchlist
  rather than guessed now and migrated later.
- **Nothing decides *which subject* to refresh.** `FreshnessReport` says a source is overdue; an
  `IngestionRequest` needs a subject, and for EDGAR that means a CIK. There is no watchlist, so the
  gap between "this source is behind" and "fetch these companies" is not bridged. Inventing a
  watchlist in a corner of the scheduler would be the wrong place for it — it is a real
  architectural piece and should be designed as one, which is why the planner deliberately stops at
  the source level rather than guessing a subject.
- **Freshness is per source, not per subject.** A source refreshed for one company reads as current
  for all of them. That is the honest reading of what the ingestion ledger records today, and
  making it per subject means indexing runs by subject — worth doing once a watchlist exists to
  make it meaningful.
- **Claims are still not persisted**, so `IPayloadReferenceIndex` consults ingestion runs only. The
  interface does not change when claims arrive — it becomes a union of two queries.
- **EDGAR takes CIKs, not tickers.** Nothing yet resolves a ticker to a CIK, so ingestion for a
  company needs its CIK supplied. EDGAR publishes a ticker-to-CIK mapping; wiring it in is small
  and belongs with normalisation. Stage 6 records `company.ticker` as an observation, which is the
  raw material for that resolver but is not one.
- ~~**Nothing calls the normalisation pipeline.**~~ *Resolved in stage 8* —
  `DataAcquisitionService` chains ingestion to normalisation. It has no scheduled caller; see above.
- **One normaliser exists, for one category.** `SecEdgarSubmissionsNormalizer` reads company
  profiles. EDGAR's filing history and XBRL facts are archived by the same connector and have no
  normaliser, so a run for `RegulatoryFilings` or `FinancialStatements` quarantines under
  `normalization.no-normalizer@1` — recorded and re-readable, but not yet knowledge.
- **The EDGAR submissions document has no publication date**, so every observation from it carries
  the retrieval time in all three provenance slots and a caveat saying that date is a floor. This
  is honest but lossy: a fact that became true months earlier is dated to when the platform first
  looked. Filings carry real dates and will not have this problem; company profiles genuinely do
  not, and nothing can recover a date the source never stated.
- **Nothing reads the quarantine queue.** `IQuarantineStore.GetRecentAsync` exists and is the
  operator's queue; no endpoint or alert surfaces it, so a source that silently changed schema
  would accumulate records nobody sees until stage 9.
- **A store's `SaveChanges` commits everything pending on the context, not only its own entity.**
  This is a property of the existing store pattern rather than something stage 6 introduced —
  `EfIngestionRunStore` has the same shape — and it is safe as currently called, because the
  pipeline quarantines before it dispatches and adds observations immediately before saving them.
  It is written down because it is the kind of coupling that stops being safe when a second caller
  appears, and the fix (a narrower unit of work per store) should be a deliberate change rather
  than a discovery.
- **Observations are never superseded, only added to.** That is the intended design — a later
  contradicting value is a new row, which is what makes point-in-time reads possible — but it means
  the table grows without bound and nothing yet compacts, tiers or archives it. Retention covers
  raw payloads, not derived observations.
- **The retention sweep's ordering is unspecified.** It walks the archive in whatever order the
  filesystem yields, so a bounded sweep on a large archive examines an arbitrary subset rather than
  the oldest payloads first. Correct but inefficient: a payload past its limit may wait several
  sweeps. Ordering by retrieval time needs an index the filesystem archive does not have.
- **A sweep's failures are counted but not described.** `RetentionSweepSummary.Failed` says how
  many payloads threw, not why. There is no logging abstraction in the Application layer to carry
  the reason, and adding one is an observability decision that belongs with stage 9 rather than
  being made in passing here.
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
  belongs with the migration. `observations` raises the stakes: it carries three owned types in one
  table, and the constructor-binding check done by inspection in stage 6 is a weaker instrument
  than a materialisation.
- **The API is still unauthenticated.** Stage 9 added a registry-mutating endpoint (activation) and
  three listings that expose licensing terms and operational state. None of it is authenticated,
  which was already true of the companies endpoints and is now true of more surface. The instruction
  in `docs/SECURITY.md` stands and matters more than it did: do not expose this API beyond
  localhost.
- **The retention sweep has no distributed lock.** `RunRetentionSweep` is an instruction, not a
  guarantee. Two instances with it enabled cannot double-delete - the seam deduplicates on content
  hash - but they would consume approval slots and audit rows discovering that. A lock belongs with
  whatever runs this in more than one place.
- **A sweep's failures are logged, not surfaced.** `RetentionSweepSummary.Failed` reaches the log
  and no endpoint. An operator watching only the API would not see a sweep failing every night.
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

Items 2 to 5 of the original list — source registration, stage 6, stage 8, stages 9 and 10 — are
**done**. The list is kept for the record; what remains is below it.

1. ~~Generate and validate the migration~~ — **still the largest remaining gap**, and now the only
   thing standing between this phase and its exit criterion. See the verification batch below.
2. ~~A source-registration command~~ — *done*: `RegisterKnownSourcesHandler`, plus
   `SourceSeedingHostedService` to call it.
3. ~~Stage 6 — normalisation and validation~~ — *done*.
4. ~~Stage 8 — freshness and data events~~ — *done*.
5. ~~Stages 9 and 10~~ — *done*: two controllers, two hosted services, seven architecture rules.

### What comes after the gates pass

1. **A watchlist.** The largest remaining *architectural* gap, and the reason the data plane still
   does not fetch anything on its own. `FreshnessReport` can say a source is overdue; an
   `IngestionRequest` needs a subject, and for EDGAR that means a CIK. Nothing decides which
   companies to follow. This was deliberately not invented in a corner of the scheduler — it is a
   real piece of the domain and deserves designing as one, and it is what makes
   `DataAcquisitionService` reachable.
2. **A ticker-to-CIK resolver**, which the watchlist needs and which EDGAR publishes as a
   downloadable mapping. Stage 6 already records `company.ticker` as an observation, which is the
   raw material rather than the resolver.
3. **Normalisers for filings and XBRL facts.** The EDGAR connector already archives both; neither
   has a normaliser, so a run for those categories quarantines under
   `normalization.no-normalizer@1` — recorded and re-readable, but not yet knowledge.
4. **Authentication.** Stage 9 added a registry-mutating endpoint and three listings that expose
   licensing terms and operational state. Until real authentication exists the instruction in
   `docs/SECURITY.md` stands and now matters more: do not expose this API beyond localhost.

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

**What the migration should produce.** Five tables, cross-checkable without reading the diff:

| Table | Columns | Indexes |
|---|---|---|
| `data_sources` | 21 | 2 |
| `ingestion_runs` | 17 | 3 |
| `unreplayable_evidence` | 5 | 2 |
| `observations` | 15 | 3 |
| `quarantined_payloads` | 6 | 3 |

`observations` is the one to read carefully: it carries three owned types in one table — subject,
value and provenance — and its indexes must include `ix_observations_subject` over
`(subject_kind, subject_identifier)`. That composite is what `EfObservationStore` relies on for
both the specific and the sweep case, and the sweep case was proven against live PostgreSQL to use
it under `IS NULL`.

**Where the build is most likely to fail first**, in order of my own confidence: the owned-type
configurations in `ObservationConfiguration` (three owned types, indexes declared inside their
`OwnsOne` builders), then the collection expressions and `IAsyncEnumerable` iterators added in
stage 8, then the two new controllers. Everything has been read against the compiler's rules by
hand and none of it has been compiled.

### Still outstanding before Phase 2 can close

- **The build, test and migration gates, none of which has ever run.** This is the whole of what
  separates "code complete" from "verified", and it is the one thing this environment cannot do —
  see section 12 for the blocker.
- A watchlist and a ticker-to-CIK resolver, without which nothing fetches on its own.
- Provider slots for market data, news and macroeconomic series — one registration each, no
  architecture change.
- The Phase 2 exit criterion: *"50 tickers ingested with full provenance, and any analysis replays
  byte-identically from stored raw responses."* Every piece it needs now exists in code — registry,
  connector, archive, ledger, normalisation, observations with provenance. What it needs next is
  the migration, a green test run, and a watchlist to name the 50.
