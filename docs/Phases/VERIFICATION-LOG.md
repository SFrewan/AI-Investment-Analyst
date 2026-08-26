# Verification log

Append-only. One entry per verification event, newest last. Entries are never edited or removed —
a superseded result is corrected by a later entry that says so.

A phase is only documented as **Verified** once its build, test, runtime, integration, database
and safety gates have actually passed. Nothing in this log may record a result that was not
executed.

---

## 2026-08-25 — Environment capability probe

**Executed by:** assistant, in its own cloud environment.

| Tool | Result |
|---|---|
| `dotnet` | absent |
| `mono`, `mcs`, `csc`, `msbuild`, `nuget` | absent |
| `https://dot.net/v1/dotnet-install.sh` | blocked |
| `https://builds.dotnet.microsoft.com/...` | blocked |
| `https://api.nuget.org/v3/index.json` | blocked |
| Docker daemon | unavailable |
| `postgres` / `initdb` / `psql` | **present** (PostgreSQL 16.13) |
| `python3` | present |

**Conclusion.** No C# compiler exists in the assistant's environment in any form, the SDK cannot
be downloaded, and NuGet is unreachable — two independent walls, so a compiler alone would not be
enough. The developer-machine bridge available to the assistant provides file access only, with no
shell, so the developer's installed SDK cannot be driven remotely either.

This probe was run once, deliberately, to establish the limitation. It is not to be retried.

**Consequence.** `dotnet build`, `dotnet test` and `dotnet ef database update` must be run on the
developer machine. Everything not requiring a compiler is verified by the assistant.

---

## 2026-08-25 — Phase 1 migration cross-check (static)

**Subject:** `20260825023757_InitialCreate.cs` and `AppDbContextModelSnapshot.cs` against the four
`IEntityTypeConfiguration` classes.

**Method.** The migration was parsed mechanically by script rather than read by eye, and every
column compared against the configurations and the snapshot.

**Result: PASS.** 4 tables, 39 columns, 9 indexes. Zero discrepancies.

Confirmed specifically:

- Every length traces to its domain constant, not a literal — `CorrelationId.MaxLength` 128,
  `ActionType.MaxLength` 100, `ActionProposal.MaxIdempotencyKeyLength` 200,
  `AuditRecord.MaxSummaryLength` 1000, `Ticker.MaxLength` 12, `Exchange.MaxLength` 12,
  `Company.MaxNameLength` 200, `MaxClassificationLength` 100, `MaxDescriptionLength` 4000,
  `ActionExecution.MaxFailureReasonLength` 2000.
- Value converters applied: ids → `uuid`; `Ticker`, `Exchange`, `CorrelationId`, `ActionType` →
  `character varying`; enums → `character varying`; `_details` → `jsonb` via the backing field.
- `action_executions.decision_id` is **NOT NULL**.
- `ix_companies_ticker` is **UNIQUE**; `processed_actions` is keyed on `idempotency_key`.
- `action_executions.status` is nullable, which is correct rather than drift:
  `ActionExecutionStatus?` where null means in flight.
- The snapshot agrees with the migration.

---

## 2026-08-25 — Phase 1 schema validation against live PostgreSQL 16

**Executed by:** assistant. **Server:** PostgreSQL 16.13, cluster created with `initdb` in the
assistant's environment.

**Method.** DDL was generated from the migration file by script, applied to a real database, then
the resulting schema was read back from `information_schema` + `pg_attribute` and diffed against
the migration.

**Result: PASS.** All 4 tables and 9 indexes created without error — every type string EF emitted
is valid PostgreSQL 16. Introspection diff: **39 declared, 39 actual, 0 mismatches** in type,
length and nullability.

Twelve behavioural checks against the live schema:

| Check | Expected | Result |
|---|---|---|
| `action_executions` insert with `decision_id` NULL | reject | rejected — not-null violation |
| `action_executions` insert with `decision_id` | accept | accepted |
| Duplicate ticker | reject | rejected — `ix_companies_ticker` |
| 13-character ticker | reject | rejected — value too long for `varchar(12)` |
| Re-claim an idempotency key | reject | rejected — `PK_processed_actions` |
| Malformed JSON in `details` | reject | rejected — invalid json syntax |
| Valid JSON in `details` | accept | accepted |
| `details->>'model'` lookup | works | works |
| `timestamptz` offset normalisation | 12:00 UTC | 12:00 UTC |
| Ticker lookup plan | index scan | `Index Scan using ix_companies_ticker` |

**Interpretation.** The safety invariant "no execution without an authorising decision" is
genuinely enforced by the database, not only by the C#.

**What this does not prove.** That the application code compiles, that EF produces these
statements at runtime, or that `dotnet ef database update` succeeds against the developer's
database.

---

## 2026-08-25 — Write-guard defect found and fixed (static review)

**Subject:** `AppDbContext.GuardWrites()`.

**Finding.** The append-only check sat after the `if (_writeAuthorization.IsAuthorized) return;`
early exit, so it could never run on an authorised write. An authorisation window stays open for
the whole duration of an action's effect, which meant the code best placed to rewrite the record
of what it had just done was precisely the code exempted from being checked.

The existing tests `An_audit_record_cannot_be_modified` and `An_audit_record_cannot_be_deleted`
open no authorisation window, so they exercised only the unauthorised branch. That is why the
defect survived them.

**Fix applied.** The append-only check now runs first and unconditionally, ahead of the
authorisation exit. The "must be written through their stores" check gained an explicit
`IsSeamBookkeeping` filter so it no longer depends on an earlier check throwing first.

**Tests added** (not yet executed):

- `An_audit_record_cannot_be_modified_even_inside_an_authorisation_window`
- `An_execution_record_cannot_be_deleted_even_inside_an_authorisation_window`

Both fail against the pre-fix code. Both require a Docker daemon for Testcontainers, or they
self-skip and report the reason.

**Confirmed safe.** `EfActionExecutionStore.RecordAsync` only ever `Add`s — the execution row is
written once, after completion — so making `Modified`/`Deleted` unconditional on the bookkeeping
types breaks no existing path.

**Status of this fix: unverified.** It is a static-review fix and must be exercised by
`dotnet test`.

---

## 2026-08-25 — CA1826 fix (reported by developer build)

**Subject:** `SourceRanking.MostAuthoritative`.

**Reported:** *"Do not use Enumerable methods on indexable collections. Instead use the collection
directly."*

**Cause.** `FirstOrDefault()` called on the `IReadOnlyList<DataSource>` returned by
`MostAuthoritativeFirst`.

**Fix.** Index the list directly: `ordered.Count > 0 ? ordered[0] : null`. The result is still
derived from `MostAuthoritativeFirst` rather than from a separate maximum scan, so there remains
one definition of the ordering including its tie-breaks.

A repository-wide sweep for the same pattern found one other match,
`HealthEndpointTests.cs:52`, where `HttpHeaders.GetValues` returns `IEnumerable<string>` and
CA1826 therefore does not apply. No change made there.

**Status: unverified** — the build that reports this rule has not been re-run by the assistant.

---

## 2026-08-25 — Second and final attempt at obtaining a .NET SDK

**Executed by:** assistant.

The earlier probe established that Microsoft's distribution hosts are blocked. This attempt tested
a different route: the **Ubuntu archive**, which ships `dotnet-sdk-8.0` independently of
Microsoft's feed.

| Attempt | Result |
|---|---|
| `apt-cache policy dotnet-sdk-8.0` | Package **is** available: 8.0.126 (noble-updates), 8.0.104 (noble) |
| `apt-get download dotnet-sdk-8.0` (security.ubuntu.com pool) | **403 Forbidden** |
| `apt-get download dotnet-sdk-8.0=8.0.104-0ubuntu1` (archive.ubuntu.com pool) | **403 Forbidden** |

**Conclusion.** The egress policy permits apt *metadata* but blocks *package payloads*, on both
pool hosts. Combined with the earlier probe this is four distinct distribution routes refused.
No further attempt will be made; the blocker is settled.

---

## 2026-08-25 — Phase 2 stage 2 implemented (provenance integration)

**Not verified.** Implementation and static review only.

**Changed:**

| File | Change |
|---|---|
| `Domain/Evidence/Provenance.cs` | `SourceId` retyped `string` → `SourceId`; `SourceRecordId` added; `MaxSourceIdLength` → `MaxSourceRecordIdLength` |
| `Domain/Sources/SourceType.cs` | `InternalDerivation = 13` added |
| `Domain/Sources/SourceAdmission.cs` | New — five ordered, versioned admission rules plus `Admissible` |
| `Domain/Sources/SourceAdmissionResult.cs` | New |
| `Domain/Sources/DataSource.cs` | Mutators now validate before mutating (defect below) |
| `Application/Abstractions/ISourceRegistry.cs` | New |
| `tests/.../Sources/*.cs` | New — 4 test files plus shared builders |
| `tests/.../Evidence/ProvenanceTests.cs` | New |
| `tests/.../Evidence/ClaimTests.cs` | Updated for the new `Provenance` shape |

**Defect found and fixed during the stage.** `DataSource` mutators called `DateRange.EnsureUtc`
and then `Touch(nowUtc)` last, and `Touch` held the "modification cannot precede registration"
rule. `Activate` with an impossible timestamp therefore set `IsActive = true` and *then* threw,
leaving the aggregate mutated by a failed call. The rule moved to
`EnsureModificationFollowsRegistration`, called first by all five mutators; `Touch` is now a pure
assignment.

Worth recording how it surfaced: it was found while writing the test that asserts the throw. That
test passes against both the broken and the fixed code, because it only checks the exception.
Writing the test was what prompted reading the code closely enough to see it — the test itself
proves nothing about it.

**Test counts.** 87 new executable cases (66 `[Fact]`, 21 `[InlineData]` across 7 `[Theory]`).
Solution total 189 → 276. **None executed.**

**Static verification performed:**

| Check | Result |
|---|---|
| Brace/paren balance on all 14 changed files, strings and comments stripped | **PASS** |
| Every domain member referenced by the new tests exists in the source | **PASS** (grep-verified) |
| Architecture rules reviewed for intra-`Domain` namespace constraints | None exist — `Evidence` → `Sources` is permitted |
| EF model snapshot drift | **PASS** — still exactly the four Phase 1 entities; no `DataSource` configuration was added |

The structural check is explicitly weak: it catches gross damage and says nothing about type
correctness. It is not offered as a substitute for a compiler.

**Stage 3 is gated.** Stage 2 modified `Provenance`, a Phase 1 type. Building further before
`dotnet build` and `dotnet test` have run increases the size of the eventual correction rather
than avoiding it.

---

## 2026-08-25 — Housekeeping

A `.deb` download attempt briefly wrote into the repository root before being redirected to
`/tmp`; removed, and the repository re-checked for stray files. Two test files were initially
written to a wrong relative path (`src/AI.Investment.Domain/Sources/tests/...`) because the shell
working directory had been left inside `src/`; the files were moved to
`tests/AI.Investment.Domain.UnitTests/Sources/` and the stray tree deleted. Absolute paths are
used from this point on.

## 2026-08-25 — Phase 2 stage 3 implemented (ingestion contracts)

**Not verified.** Implementation and static review only.

**Added** — six domain types in `Domain/Ingestion/` (`IngestionRunId`, `IngestionSubject`,
`ContentHash`, `IngestionOutcome`, `IngestionRequest`, `IngestionRun`) and two Application
abstractions (`IRawResponseArchive`, `IIngestionRunStore`), plus
`tests/.../Ingestion/IngestionContractTests.cs`.

**Scoped deliberately as purely additive.** No existing type was modified. With stages 1–2 still
uncompiled, adding coupling on top of unverified changes compounds the eventual correction; a
stage that only adds new files can sit beside that risk instead.

**Static verification performed:**

| Check | Result |
|---|---|
| Non-ASCII scan across all of `src/` and `tests/` | **PASS** — no matches, after one stray character was introduced and removed in `IRawResponseArchive` documentation |
| Every referenced member confirmed to exist (`CorrelationId.New`, `DateRange.Create`, `DateRange.EnsureUtc` accessibility, `SourceAdmissionResult` shape) | **PASS** |
| Brace/paren balance | **NOT RUN** — the shell became unavailable mid-stage; this check is outstanding for the stage 3 files only |

**Method note on the `ContentHash` tests.** They assert against the *published* SHA-256 digests of
the empty string and `"abc"`, not against the implementation's own output. A hash tested only
against itself is self-consistent and possibly wrong, and content addressing is worthless if a
future version computes the address differently.

**Test counts.** 37 new executable cases. Solution total roughly 313, from 189 before Phase 2
stage 2. **None executed.**

**Stage 4 is gated, on two things.** The build and test gates have never run, and stage 4 must
bind to the stage 3 contracts, so it is the first stage where continuing without a compiler
compounds rather than defers. Separately, stages 4 and 5 need real provider credentials and
licence terms — a commercial and legal decision, not an implementation one.

---

## 2026-08-25 — Phase 2 stage 4 implemented (provider abstractions and the ingestion gateway)

**PENDING LOCAL VERIFICATION.** Implementation and static review only; no compiler was available.

**Added** — `ProviderCapabilities`, `ProviderCapabilityCheck`, `ProviderCapabilityResult` and
`ProviderQuota` in `Domain/Ingestion/`; `IIngestionGateway`, `IngestionGateway`, `IDataProvider`,
`ProviderResponse`, `IProviderCatalogue`, `IProviderRateLimiter` and `IngestionParameters` in a new
`Application/Ingestion/`; two test files and one file of six hand-written doubles.

**Modified** — `IngestionRun.Refuse` generalised to `(ruleId, reason)` with the
`SourceAdmissionResult` overload retained; `AppDbContext.IsSeamBookkeeping` gained `IngestionRun`;
`ContentHash.Compute` takes a `ReadOnlySpan<byte>`; `IRawResponseArchive.StoreAsync` takes a
`ReadOnlyMemory<byte>`.

**Two analyzer-driven design changes made before a build could report them.** `ProviderResponse`
exposes `ReadOnlyMemory<byte>` rather than `byte[]` — CA1819 aside, an array property hands every
caller a mutable reference to the provider's answer, and an archived response must be unaltered.
`ContentHash.Compute` deliberately has no array overload, because two overloads would make
`Compute([])` ambiguous.

**Static verification executed** (whole solution: 163 files, 46 namespaces, 194 top-level types):

| Check | Result |
|---|---|
| Brace/paren balance, strings and comments stripped | **PASS** |
| Namespace declaration matches folder path | **PASS**, every file |
| Every `using AI.Investment.*` resolves to a declared namespace | **PASS** |
| Dependency direction (Domain -> nothing; Application -> no Infrastructure/Api) | **PASS** |
| Duplicate type names within a namespace | **PASS** — two hits, both generic-arity pairs |
| Stray `.cs` outside `src/` and `tests/` | **PASS** — none |
| Interface members implemented by implementers | **PASS** for all six new doubles |
| Non-ASCII anywhere in `src/` or `tests/` | **PASS** — none |
| EF model snapshot drift | **PASS** — still exactly the four Phase 1 entities |

This also closed the brace check left outstanding for the stage 3 files when the shell became
unavailable mid-stage.

**Test counts.** 43 new executable cases. Solution total **356**, from 189 before Phase 2 stage 2.
**None executed.**

**Stage 5 is blocked on a decision, not on engineering.** It needs a provider choice, its licence
terms and credentials — commercial and legal rather than technical. SEC EDGAR is the one option
needing none of them and is recommended as the starting point.

---

## 2026-08-25 — Phase 2 stage 5 implemented (SEC EDGAR connector)

**PENDING LOCAL VERIFICATION.** Implementation and static review only.

**Added** — `SecEdgarProvider`, `SecEdgarEndpoints`, `SecEdgarSource`, `ProviderCatalogue`,
`SlidingWindowRateLimiter`, `SecEdgarOptions`, `AddIngestion`/`AddSecEdgar` in Infrastructure's
`DependencyInjection`, the `Providers:SecEdgar` block in `appsettings.json`, a `DataIngestion`
capability policy in `appsettings.Development.json`, and
`tests/.../Ingestion/SecEdgarProviderTests.cs`.

**Added dependency** — `Microsoft.Extensions.Http` 8.0.0, for a typed `HttpClient`. First package
added since Phase 1; reasoning recorded in `Directory.Packages.props`.

**Compliance decisions recorded** — the connector sends a `User-Agent` with application name and
contact address on every request, declares the SEC's published rate ceiling as a quota so the
limiter honours it before fetching, and is **not registered at all** unless a contact address is
configured. No credential is hard-coded; EDGAR needs none, and the contact address is deployment
configuration. `appsettings.json` documents the shape with empty values and `Enabled: false`.

**No HTTP is made from the test suite**, deliberately: a CI run must not consume somebody's
fair-access quota.

**A service-graph review was performed and changed the outcome.** `IIngestionGateway` was going to
be registered in this stage; its three storage dependencies have no implementations, and ASP.NET
Core validates the container on build in Development, so registering it would have failed the whole
host's start-up rather than leaving one feature absent. It is left unregistered with a comment
naming what unblocks it. This is the kind of defect a build would have caught only at run time and
only in one environment.

**Static verification executed** (whole solution: 170 files, 49 namespaces, 205 top-level types):
brace balance, namespace-versus-folder, `using` resolution, dependency direction, duplicate types,
stray files, interface completeness, non-ASCII, EF snapshot drift — **all PASS**, with the same two
generic-arity false positives and the expression-bodied-property false positives noted before.

**Test counts.** 31 new executable cases. Solution total **387**. **None executed.**

---

## 2026-08-25 — Phase 2 stage 7 implemented (historical persistence)

**PENDING LOCAL VERIFICATION** for build, test and migration. Schema validated against a live
server; see below.

**Added** — `DataSourceConfiguration`, `IngestionRunConfiguration`, `EfSourceRegistry`,
`EfIngestionRunStore`, `FileSystemRawResponseArchive`, `RawArchiveOptions`, two `DbSet`s, and the
`IIngestionGateway` registration that stage 5 deliberately withheld. `appsettings` gained a
`RawArchive` section.

**No domain type was changed.** The private constructors on `LicensingTerms`,
`VerificationPolicy`, `UpdateCadence`, `IngestionRequest`, `IngestionSubject` and `DateRange`
already bind by parameter name, which is what EF's constructor binding requires — verified by
inspection before any mapping was written.

**Service-graph review: PASS.** `IngestionGateway`'s seven dependencies all resolve
(`ISourceRegistry`, `IProviderCatalogue`, `IProviderRateLimiter`, `IRawResponseArchive`,
`IIngestionRunStore`, `IActionGateway`, `IClock`), and lifetimes are compatible — a scoped service
depending on scoped and singleton registrations. This is the check that stopped the gateway being
registered in stage 5; it now passes, so the registration was enabled.

---

### Stage 7 schema validated against live PostgreSQL 16

**Executed by:** assistant. **Server:** PostgreSQL 16.13, `initdb` cluster in the assistant's
environment.

**Method.** The schema was derived by hand from the two entity configurations, applied to a real
database, and exercised. This is not a substitute for `dotnet ef migrations add` — it proves the
column types and constraints are valid and behave as intended, not that EF will emit exactly them.

**Result: PASS.** 2 tables (21 + 17 columns), 5 indexes, created without error.

| Check | Expected | Result |
|---|---|---|
| Source with a jsonb category set | accept | accepted |
| Duplicate source id | reject | rejected — `PK_data_sources` |
| Malformed jsonb in `categories` | reject | rejected — invalid json |
| `cadence_interval` round-trip | `1 day` | `1 day` |
| `categories @> '["RegulatoryFilings"]'` | finds the source | found — GIN index available later |
| Source id longer than 64 characters | reject | rejected — varchar(64) |
| Refused run with no window | accept | accepted |
| Successful windowed run with artifacts | accept | accepted |
| Run with no `outcome` | reject | rejected — NOT NULL |
| Run with no `request_fingerprint` | reject | rejected — NOT NULL |
| Fingerprint lookup plan | index scan | `Index Scan using ix_ingestion_runs_request_fingerprint` |
| Sweep run with null subject identifier | accept | accepted |

**What this does not prove.** That the C# compiles, that EF's model builder accepts the owned-type
and shadow-property configuration, or that the generated migration matches this schema. Owned
types, value converters and shadow properties are precisely where an EF configuration written
without a compiler is most likely to be wrong, and `dotnet ef migrations add` is what surfaces it.

---

### Retention: presented, not decided

The archive deletes nothing. Four options with their storage, replay, licensing and audit
consequences are set out in the Phase 2 document, section 18, with a recommendation
(per-source retention on `LicensingTerms`, with a floor forbidding deletion of any payload a stored
claim references). **No code assumes an answer.**

---

## 2026-08-25 — Retention implemented (Option C approved)

**PENDING LOCAL VERIFICATION** for build and test. Schema and query validated against a live
server.

**Added** — `RetentionLimit` on `LicensingTerms`; `RetentionPolicy`, `RetentionOutcome`,
`RetentionDecision`, `UnreplayableEvidence` in a new `Domain/Retention`; `IRetentionEnforcer` /
`RetentionEnforcer`, `RetentionParameters`, `IPayloadReferenceIndex`,
`IUnreplayableEvidenceStore`; `EfUnreplayableEvidenceStore`, `EfPayloadReferenceIndex`,
`UnreplayableEvidenceConfiguration`; `DescribeAsync` and `DeleteAsync` on the archive;
`Capability.DataRetention`.

**Composition tidied.** `IIngestionGateway` and `IRetentionEnforcer` are registered in
`AddApplication` alongside `IActionGateway`, where Application services belong. What had made them
wait was Infrastructure, not their own layer.

**Two decisions worth recording.**

*The retention default is `Retain`, not deny.* Everywhere else the fail-safe direction is refusal.
Here the irreversible operation is the deletion, so the zero value of `RetentionOutcome` is
`Retain` and an unset value can never read as "delete".

*Deletion declares itself irreversible.* `ReversibilityClass.Irreversible` on the proposal means
`policy.irreversible-requires-approval@1` applies, so every retention deletion requires human
approval unless an operator has explicitly granted `AllowIrreversibleAutoExecute`. That fell out of
declaring the truth rather than being added as a special case.

**Validated against live PostgreSQL 16.13:**

| Check | Expected | Result |
|---|---|---|
| `unreplayable_evidence` table and two indexes | create | created |
| A marker | accept | accepted |
| Duplicate marker for the same payload | reject | rejected — primary key |
| Marker with no reason | reject | rejected — NOT NULL |
| `artifacts @> '["<hash>"]'::jsonb` for a referenced hash | true | true |
| The same query for an unreferenced hash | false | false |

The last two matter most: that containment query is exactly what `EfPayloadReferenceIndex` runs,
and it is the one piece of raw SQL in the data plane. It now has a real result against real rows
rather than an assumption.

**Test counts.** 31 new executable cases. Solution total **418**. **None executed.**

---

## 2026-08-25 — Source registration implemented

**PENDING LOCAL VERIFICATION** for build and test.

**Added** — `ISourceDefinition`; `RegisterKnownSourcesHandler`, `ActivateSourceHandler` and their
parameters and results; `SecEdgarSource` converted from a static class to a sealed class
implementing `ISourceDefinition`; both handlers and the definition registered.

EDGAR's definition now states `RetentionLimit.Unlimited` explicitly rather than defaulting to it,
so the registry records a fact about the licence instead of the absence of a decision.

**Design notes.** One proposal per source, not one for the batch — a refusal of one must not take
the others with it, and the audit trail should name the source admitted. Existing registry entries
are left untouched, because an operator may have re-licensed or deactivated one and start-up
seeding must not undo that. Seeding uses its own `CorrelationId` rather than `ICorrelationContext`:
it runs outside any request, and the HTTP implementation would have nothing to give it.

**Static verification:** 198 files, 56 namespaces, 243 types — brace balance, namespace/folder,
using resolution, dependency direction, duplicate types, stray files, interface completeness and
non-ASCII all **PASS**.

**Test counts.** 11 new executable cases. Solution total **429**. **None executed.**

---

## 2026-08-26 — Stage 6: normalisation and validation

**PENDING LOCAL VERIFICATION** for build and test.

**Added** — Domain: `Observation`, `ObservationId`, `ObservationValue`, `ObservationValueKind`,
`QuarantinedPayload`. Application: `INormalizer`, `INormalizationPipeline`/`NormalizationPipeline`,
`NormalizationInput`, `NormalizationResult`, `NormalizationSummary`, `NormalizationParameters`,
`IObservationStore`, `IQuarantineStore`. Infrastructure: `SecEdgarSubmissionsNormalizer`,
`ObservationConfiguration`, `QuarantinedPayloadConfiguration`, `EfObservationStore`,
`EfQuarantineStore`, two `DbSet`s, and four registrations.

**Changed** — `AppDbContext.IsSeamBookkeeping` now exempts `QuarantinedPayload` as a fifth type.
`Observation` is deliberately not exempt.

**Design notes.** Observations go through the seam because an observation is something the platform
believes; quarantine records do not, because a policy denial is one of the things worth
quarantining a run over and the record must be writable when nothing is authorised. Normalisers are
registered unconditionally rather than inside a connector's registration: disabling EDGAR must not
make the payloads it already fetched unreadable.

**Defects found and fixed during this stage.**

1. `ObservationConfiguration` declared indexes on owned-type properties from the *owner's* builder,
   which EF rejects. Moved inside their `OwnsOne` blocks.
2. Two em dashes had reached `ObservationConfiguration`'s XML docs. Removed; the repo-wide
   non-ASCII scan is clean again.

**Static verification:** 222 files, 63 namespaces, 270 types — brace balance, namespace/folder,
using resolution, dependency direction, duplicate types, stray files, interface completeness and
non-ASCII all **PASS**. Service-graph review: `NormalizationPipeline`'s six dependencies all
resolve, lifetimes compatible.

**Owned-type constructor binding, by inspection.** `Provenance`, `IngestionSubject` and
`ObservationValue` each expose a private constructor whose parameter names match their property
names exactly, which is what EF binds on. `AggregateRoot<TId>` has a protected parameterless
constructor, so both new aggregates can be materialised. This is inspection, not a round-trip, and
is recorded as the weaker instrument it is.

**Live PostgreSQL 16.13 validation.** Schema hand-derived from both configurations and applied to
the same instance used in stage 7. `observations` (15 columns, 3 indexes) and
`quarantined_payloads` (6 columns, 3 indexes) created without error.

Thirteen behavioural checks, all as specified:

| # | Check | Result |
|---|---|---|
| 1 | `subject_identifier = NULL` matches nothing | 0 rows, as SQL requires |
| 2 | `subject_identifier IS NULL` finds the sweep subject | 1 row |
| 3 | Point-in-time read as at 2026-04-01 returns the earlier name | `Apple Inc.` |
| 4 | The same read as at 2026-07-01 returns the later one | `Apple Incorporated` |
| 5 | Both names still exist; history is not overwritten | 2 rows |
| 6 | `caveats` round-trips as a jsonb array | length 1, element intact |
| 7 | `confidence numeric(5,4)` stores 0.8750 and rejects 10.0 | stored; overflow rejected |
| 8 | An attribute over 120 characters is rejected, not truncated | rejected |
| 9 | A value over 4000 characters is rejected | rejected |
| 10 | The same content hash cannot be quarantined twice | duplicate key rejected |
| 11 | A quarantine with no reason is rejected | not-null violation |
| 12 | The operator queue reads newest first | correct order |
| 13 | Timestamps come back as UTC | `+00` |

Every failure above is the assertion, not an accident: checks 7-11 pass *because* the database
refused the write.

**Index usability, by query plan** (with `enable_seqscan = off`, since the row counts are too small
for the planner to choose an index on merit — the question is whether the index *can* serve the
predicate, not whether it would today):

| Query | Plan |
|---|---|
| Subject lookup, specific identifier | Index Scan using `ix_observations_subject` |
| Subject lookup, `IS NULL` | Index Scan using `ix_observations_subject` |
| Attribute lookup | Index Scan using `ix_observations_attribute` |
| `published_at_utc <= X` | Bitmap Index Scan on `ix_observations_published_at_utc` |
| Quarantine queue, newest first | Index Scan Backward on `ix_quarantined_payloads_quarantined_at_utc` |
| Quarantine existence by hash | Index Only Scan using `PK_quarantined_payloads` |

The second row is the one worth having. `EfObservationStore` expresses the sweep case as `IS NULL`
rather than passing a null parameter, precisely because `= NULL` is never true in SQL and a single
parameterised comparison would have returned nothing for exactly the subjects that have no
identifier. Check 1 proves the trap is real and this plan proves the chosen expression still uses
the index.

**Test counts.** 113 new executable cases. Solution total **542**. **None executed.**

---

## 2026-08-26 — Stage 8: freshness, data events, and the retention sweep

**PENDING LOCAL VERIFICATION** for build and test.

**Added** — Domain: `FreshnessState`, `FreshnessAssessment`, `FreshnessPolicy`, and
`ContentHash.TryCreate`. Application: `IFreshnessReport`/`FreshnessReport`/`SourceFreshness`,
`IRetentionSweep`/`RetentionSweep`/`RetentionSweepSummary`, `IDataAcquisition`/
`DataAcquisitionService`/`AcquisitionResult`, `RetentionEnforcementResult`/`RetentionAction`.
Infrastructure: `FileSystemRawResponseArchive.EnumerateAsync`. Three new registrations.

**Changed** — `IRawResponseArchive` gained `EnumerateAsync`; `IRetentionEnforcer.EnforceAsync`
changed its return type. No schema change.

**Defect found and fixed during this stage.**

`IRetentionEnforcer` returned only the `RetentionDecision` — what a licence *requires* — and
discarded the outcome of the seam dispatch that was supposed to carry it out. Retention deletion
declares itself irreversible, so `policy.irreversible-requires-approval@1` applies and an
installation that has not granted `AllowIrreversibleAutoExecute` for `Capability.DataRetention`
gets an approval requirement on every payload by design. The enforcer therefore knew that nothing
had been deleted and threw that fact away.

Nothing had called it yet, so nothing was wrong in production — but the sweep being built on top of
it would have reported discharged obligations for payloads still on disk, which is a compliance
statement nothing observed. Fixed by returning `RetentionEnforcementResult`, carrying both the
obligation and what came of it. The nine existing enforcer tests were updated and three new cases
added covering denial, approval-required and duplicate suppression.

**A second, related decision.** `RetentionSweep` counts refusals and failures separately rather
than folding both into "not deleted". Counting a thrown exception as a policy refusal would let a
database outage report as thousands of payloads that policy declined to delete. One poisoned
payload does not end a sweep — a sweep that died on the same entry every time would block the
obligation permanently — but it is counted as a failure, not disguised as a decision.

**Design notes.** `FreshnessPolicy` is the one gate in the platform that fails *towards* action.
Everywhere else uncertainty guards an irreversible act and must deny; here wrongly refreshing costs
one request while wrongly reporting stale data as current corrupts every decision made downstream.
The reversible mistake is the one to make, and the asymmetry is documented on the type and asserted
in the tests.

`FreshnessReport` reads only *successful* runs. A source refused fifty times running has not been
refreshed, and reading the latest run of any outcome would report it as current — precisely the
failure the report exists to catch. Freshness is dated from completion rather than start, for the
same class of reason.

**Static verification:** 236 files, 67 namespaces, 288 types — brace balance, namespace/folder,
using resolution, dependency direction, duplicate types, stray files, interface completeness and
non-ASCII all **PASS**. 51 implementations across 31 interfaces checked; the six reported are the
known expression-bodied-property false positives, unchanged from previous stages.

**Service-graph review:** `RetentionSweep` (2 dependencies), `DataAcquisitionService` (2) and
`FreshnessReport` (3) all resolve; every dependency is registered and no scoped service is captured
by a singleton.

**No database validation this stage**, because there is no schema change. Stated rather than
omitted: a stage that quietly skipped a gate would be indistinguishable from one that failed it.

**Test counts.** 55 new executable cases. Solution total **597**. **None executed.**

---

## 2026-08-26 — Stage 9: the API surface and the missing callers

**PENDING LOCAL VERIFICATION** for build and test.

**Added** — Api: `DataPlaneOptions`, `SourceSeedingHostedService`, `RetentionSweepHostedService`,
`SourcesController`, `DataPlaneController`, and a `DataPlane` section in both `appsettings` files.
Application: `SourceDto`/`SourceLicensingDto`/`SourceMapper`, `FreshnessDto`/`FreshnessMapper`,
`IngestionRunDto`/`QuarantinedPayloadDto`/`IngestionMapper`.

**Changed** — `Program.ConfigureServices` registers the two hosted services and validates
`DataPlaneOptions` at start-up. No schema change.

**What this stage is actually for.** Every stage before it built something correct that nothing
invoked. The registry could be seeded and was not; the sweep could run and did not. Both now have
callers, and both are **off by default** - the sweep because it is the only activity that destroys
evidence, seeding because writing to a database at start-up should be a decision rather than a
default. Development enables seeding and still not the sweep.

**Three decisions made during self-review, before any of it could be compiled.**

1. **Method-group conversions on overloaded mappers were replaced with explicit lambdas.**
   `SourceMapper.ToDto` and `IngestionMapper.ToDto` are each overloaded, and inferring `Select`'s
   result type from an overloaded method group is at the edge of what C# type inference guarantees.
   With no compiler available, the version that cannot be argued about is the right one.
2. **`TimeSpan` options became integer minutes.** `RangeAttribute` over `TimeSpan` relies on a type
   converter, and - more importantly - a duration in JSON invites `"24:00:00"`, which is not a
   parseable timespan at all: the hours component may not exceed 23, so the obvious way to write one
   day throws. This was caught in the first draft of `appsettings.json` and would have been a
   start-up failure. An integer has one reading.
3. **A helper that allocated a discarded `OkResult`** on its success path was rewritten to return
   the problem or null.

**Design notes.** A malformed source identifier is a `400`, not a `404`: telling a caller that their
well-formed id does not exist is a different and untrue statement, and one that sends them looking
in the registry rather than at what they sent. An out-of-range page size is clamped rather than
rejected, because a dashboard sending whatever its config says should get a bounded page, not an
error.

Every listing is read-only and deliberately outside the seam. The seam gates side effects; asking a
question is not one, and auditing reads would bury the record of what changed under a record of who
looked.

**Static verification:** 246 files, 70 namespaces, 303 types — brace balance, namespace/folder,
using resolution, dependency direction, duplicate types, stray files, interface completeness and
non-ASCII all **PASS**. Both `appsettings` files parse as JSON and every `DataPlane` value is inside
its declared range.

**Service-graph review:** both hosted services take `IServiceScopeFactory` rather than capturing a
scoped service in a singleton, and resolve their scoped collaborators inside a created scope. Both
controllers' dependencies are registered. Nothing outside `Program.cs` references an Infrastructure
type, so the existing architecture test still holds.

**No database validation this stage**, because there is no schema change.

**Test counts.** 34 new executable cases. Solution total **631**. **None executed.**

---

## 2026-08-26 — Stage 10: the data plane's invariants as tests

**PENDING LOCAL VERIFICATION** for build and test.

**Added** — `tests/AI.Investment.Architecture.Tests/DataPlaneRuleTests.cs`, seven structural
assertions. No production code changed.

**What each one protects, and what it was checked against by hand before being written:**

| Rule | Verified by inspection |
|---|---|
| Domain and Application cannot reach the network | No `System.Net`, `HttpClient` or socket reference in either tree |
| Connectors and normalisers live only in Infrastructure | `SecEdgarProvider` and `SecEdgarSubmissionsNormalizer` are the only implementations, both in Infrastructure |
| Domain and Application do not schedule themselves | No `Microsoft.Extensions.Hosting` or timer reference; the Application csproj references only DI abstractions |
| The domain does not log | No `Microsoft.Extensions.Logging`, Serilog or `Console` reference in Domain |
| Every enum names its default | All **26** enums across Domain and Application declare an explicit `= 0` member |
| Every aggregate root is materialisable | All **6** (`Company`, `DataSource`, `IngestionRun`, `UnreplayableEvidence`, `Observation`, `QuarantinedPayload`) have a private parameterless constructor |
| Every configured entity is exposed as a `DbSet` | **9** configurations, **9** `DbSet`s, matched one to one |

Each was confirmed against the repository before the assertion was written, so these are tests that
encode a property already established rather than tests hoped to pass. That distinction matters
here more than usual: with no compiler available, a test written blind and expected to go green is
a guess, and a guess in a file named after architecture rules is worse than no file.

**A note on what the enum rule deliberately does not say.** It asserts that zero belongs to a named
member, not which member. The right answer differs by type: most choose the unknown or safe case,
while `RetentionOutcome.Retain` is zero precisely because there the irreversible operation is the
deletion. Worth recording that the audit also surfaced `ReversibilityClass.Reversible = 0` and
`RiskTier.Low = 0` - permissive defaults, from Phase 1. Both are unreachable in practice because
`ActionEconomics.Create` requires an explicit reversibility, and `PolicyOutcome.Deny = 0` keeps the
decision itself fail-closed. Noted rather than changed: relitigating a Phase 1 decision in passing,
without a compiler, is not the way to do it.

**Static verification:** 247 files, 70 namespaces, 304 types — all checks **PASS**, non-ASCII
clean, brace balance clean.

**Test counts.** 7 new executable cases. Solution total **635**, counted mechanically across
`tests/` (every `[Fact]`, plus one per `[InlineData]` row; the solution uses no `[MemberData]`,
so the count is exact). Summing the per-stage tallies in the entries above gives 638 — those
were hand counts made as each stage was written, and the mechanical number is the correct one.
**None executed.**

---

## Outstanding gates

| Gate | Phase | Owner | Notes |
|---|---|---|---|
| `dotnet build` | 0, 1, 2 | developer machine | Warnings are errors; must be clean |
| `dotnet test` | 1, 2 | developer machine | 635 executable cases, none ever run |
| `dotnet ef migrations add` + `database update` | 1, 2 | developer machine | All five Phase 2 tables validated against live PG16; the migration has not been generated |
| Runtime startup | 0, 1 | developer machine | Options validation runs at startup |
| Integration tests | 1 | developer machine | Need a Docker daemon, else they self-skip |
| Data-plane tests | 2 | written, not run | 407 cases covering stages 1-10 |
| CI workflow execution | 0 | GitHub | Present, never triggered |
