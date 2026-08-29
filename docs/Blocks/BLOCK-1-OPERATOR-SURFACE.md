# Development Block 1 — Operator surface and observation-window activation

**Status: IMPLEMENTED. Operationally blocked on external configuration.**
**Autonomy: L3 — prepare for approval. Unchanged by this block, and unchangeable by it.**
**Identified 2026-08-28 in [`../Phases/ROADMAP.md`](../Phases/ROADMAP.md) §9. Not a phase. There is
no Phase 9.**

---

## 1. Why this block exists

Phase 8 closed the three engineering prerequisites for an observation window — market price
observations, opportunity discovery, per-capability breaker signals — and then could not proceed,
because nothing could reach them. The platform had a policy engine, an audit trail, an escalation
store and a work-plan template, and no authenticated way for a person to point any of it at an
instrument or to answer anything it raised.

Phase 6 recorded that absence rather than papering over it: *"an endpoint that resolved an
escalation without knowing who was calling would make the record of who decided a fiction."* This
block supplies the identity, and then the endpoints.

Nothing here is new machinery. Every operator action is an `ActionProposal` through the existing
`IActionGateway`, judged by the existing `PolicyEngine`, written through the existing
`GuardWrites()` path, and audited by the existing sink. The surface is a way in, not a way round.

---

## 2. What was implemented

### 2.1 Operator identity and authentication (Api)

| File | What it does |
| --- | --- |
| `Security/OperatorOptions.cs` | The recognised accounts, from configuration section `Operators`. Holds **SHA-256 digests, never keys**. Ships empty. |
| `Security/OperatorAuthentication.cs` | Scheme name `OperatorKey`, header `X-Operator-Key`, claim type `operator:privilege`. |
| `Security/OperatorKeyAuthenticationHandler.cs` | Hashes the presented key and compares digests with `CryptographicOperations.FixedTimeEquals`. |
| `Security/OperatorPolicies.cs` | Four authorization policies, each requiring the scheme, an authenticated principal and one privilege claim. |
| `Security/HttpOperatorContext.cs` | Projects `HttpContext.User` into the application layer's `IOperatorContext`. |

This closes audit finding **F-03**. The original solution called `UseAuthorization()` with no
authentication scheme registered — a no-op that reads as security in review. Phase 0 removed it and
left a documented absence. `Program.cs` now registers the scheme, the policies, and
`UseAuthentication()` before `UseAuthorization()`.

**It fails closed in five ways.** No header → not authenticated. A hash that is not sixty-four
hexadecimal characters → that account matches nobody, rather than everybody. An unrecognised
privilege name → the whole account is refused, rather than granted the rest of its list. No key
matches → a message that says only that the key was not recognised, so a wrong key cannot be turned
into account enumeration. No accounts configured → nobody is authenticated, and that is the shipped
default.

**What it is not.** A bearer credential, not an identity provider: no rotation, no expiry, no
revocation list, no second factor. It is the smallest mechanism that puts a name on every sensitive
action, and it is shaped so that replacing it with OIDC touches the handler and the options beside
it and nothing below the controller — the application layer sees an `OperatorIdentity` either way.

### 2.2 The console (Application)

`Operators/OperatorConsole.cs` owns every operator action. `OperatorIdentity` carries the id, the
display name and a set of `OperatorPrivilege`. Each method checks the privilege, builds an
`ActionProposal` with `ProposedBy.Human(identity.Id)`, and dispatches through `IActionGateway`; the
effect runs only inside the authorisation window the gateway opens.

| Action | Action type | Capability | Privilege |
| --- | --- | --- | --- |
| Reject an opportunity | `operator.reject-opportunity` | `OpportunityManagement` | `DecideOpportunities` |
| Acknowledge an escalation | `operator.acknowledge-escalation` | `Operations` | `AnswerEscalations` |
| Resolve an escalation | `operator.resolve-escalation` | `Operations` | `AnswerEscalations` |
| Engage the kill switch | `operator.engage-kill-switch` | `Operations` | `AdministerKillSwitch` |
| Create a scheduled watch | `operator.create-watch` | `Operations` | `AdministerWatches` |

`OperatorOutcome` keeps every refusal distinct — not authenticated, not permitted, not found,
refused by the domain, denied by policy, approval required, duplicate suppressed, done. Collapsing
them would cost an operator an incident: a privilege problem that looked like a login problem sends
somebody to re-enter a key that was fine.

### 2.3 The HTTP surface (Api)

`Controllers/OperatorController.cs` — `GET api/operator/whoami`, `POST
opportunities/{id}/rejection`, `POST escalations/{id}/acknowledgement`, `POST
escalations/{id}/resolution`, `POST kill-switch/engagement`, `POST watches`. Each validates its
request shape, calls one console method, and maps the outcome to a status code. **No business logic
lives in the controller.** `POST api/sources/{id}/activation`, previously anonymous, now requires
`AdministerWatches`.

### 2.4 The operator console page (Api, `wwwroot/index.html`)

A single self-contained page served by the existing static-file middleware, reading the read models
that already existed: health, cycles, escalations, opportunities, shadow decisions, autonomy grants,
promotion state, freshness, ingestion runs, the capital ledger, validation results and sources.

It is not decorative, and it is explicit about what the platform is:

- A banner that reads **autonomy L3 — prepare for approval**, and does not change.
- **Live execution unavailable**, stated as a structural fact rather than a setting.
- Shadow decisions carry **"these are measurements — nothing here was executed."**
- Promotion state shows the Phase 7 criteria and what is missing, not a button.

The key is held in `sessionStorage` and sent as `X-Operator-Key`. Every control the page offers is
one of the endpoints above, so **the page can do nothing a `curl` with the same key could not**. It
has no privileged path of its own.

### 2.5 One safety change, and its justification

`AppDbContext.GuardWrites()` refuses to modify operations records except through a per-type
allow-list of progress columns. `Escalation` had no allow-list, so answering an escalation could
never have committed. This block adds one:

```csharp
private static readonly string[] EscalationAnswerFields =
[
    nameof(Escalation.AcknowledgedAtUtc),
    nameof(Escalation.AcknowledgedBy),
    nameof(Escalation.ResolvedAtUtc),
    nameof(Escalation.Resolution),
];
```

Four columns, all of them the answer. The escalation's identity, capability, reason and expiry
remain unwritable, and escalations remain undeletable. `OperatorWritePathTests` proves both against
Postgres: rewriting `ExpiresAtUtc` throws `UnauthorizedWriteException`, and a delete is refused.

---

## 3. What is deliberately not here

**Approve.** An approval token binds to the fingerprint of the exact proposal a person was shown,
and proposals are not persisted. An approve endpoint would either refuse every token or would have
to loosen the binding that makes a token mean anything. Phase 5 §13.1 recorded this and named its
prerequisite — persisted proposals — which is a block of its own. Rejecting needs no token, so
rejecting is here.

**Disengage the kill switch.** The policy engine denies every action while the switch is engaged, so
a disengage proposal would be refused by the state it exists to clear. The only implementation that
would work is one that bypassed the gate, and a bypass whose purpose is turning the kill switch off
is the last thing this platform should own. Disengaging stays out of band, with whoever has database
or environment access. `IKillSwitchAdministration` has one method, and
`OperatorSurfaceSafetyTests` asserts that neither it nor the console ever grows a member named
*Disengage*, *Clear*, *Reset*, *Force* or *Override*.

**Positions, portfolio and unrealised P&L.** Not required to observe. A future development block.

**A notification transport.** The outbox and its handler seam already exist; no transport was added,
because every candidate needs credentials this environment does not have. A future development
block.

---

## 4. How the observation window is activated

Engineering is complete. **The remaining steps are operational, and one of them requires something
this environment does not have.**

1. **Obtain a licensed daily-close export.** `PriceHistoryFileProvider` reads
   `session_close_utc,close,published_at_utc` CSV files from a directory. **This is the external
   blocker.** The platform will not fabricate prices and has no vendor credentials.
2. **Configure the provider** under `Providers:PriceHistory`: `Enabled`, `HistoryDirectory`,
   `LicensingNotes`, `RedistributionAllowed`, `RetentionDays`. The options refuse to enable without a
   directory and licensing notes.
3. **Admit and activate the source** — `POST api/sources/{id}/activation`, now authenticated.
4. **Register watches** — `POST api/operator/watches` against the `equity-price-review` template,
   under `Capability.OpportunityManagement`.
5. **Configure a policy** for `Capability.OpportunityManagement`. A capability with no configured
   policy is denied, which is the correct default and also means nothing runs until this is done.
6. **Wait.** The window is elapsed time. Shadow decisions, audit records and breaker signals
   accumulate through the Phase 6 mechanisms with no further engineering.

**No performance, hit rate, calibration or breach rate is claimed by this block.** None can be, until
step 6 has actually elapsed against real data.

---

## 5. Operating the console

Serve the API and open its root. Paste an operator key; it is held for the browser session only and
sent as `X-Operator-Key`. The page shows what the platform is doing; the actions it offers are the
five above, and each returns the console's own outcome text on refusal — including *why* it was
refused, which is the distinction between a policy denial and a missing privilege.

To add an operator: `echo -n "the-key" | sha256sum`, then add `{ Id, DisplayName, KeySha256,
Privileges }` to `Operators:Accounts`. Grant the fewest privileges the person needs. **Do not put
the key itself in configuration.**

---

## 6. Verification performed

| Check | Result |
| --- | --- |
| Release build, `TreatWarningsAsErrors` | 0 errors, 0 warnings |
| Full suite, 6 assemblies | 1824 tests, 0 failed, 0 skipped |
| Anonymous access to every operator write endpoint | 401 |
| Unrecognised key | 401 |
| Authenticated without the privilege | 403 |
| Operator actions audited under the operator's own name | asserted against a real `ActionGateway` + `PolicyEngine` |
| Nothing written when policy denies | asserted |
| Nothing written when the kill switch is Engaged or Unknown | asserted |
| Escalation answers persist; identity fields do not | asserted against Postgres |
| Escalations cannot be deleted | asserted against Postgres |
| Kill-switch engagement is idempotent and visible to the read side | asserted against Postgres |
| Engaging outside the seam is refused | asserted against Postgres |
| No `Approve`/`Disengage`/`Clear`/`Reset`/`Force`/`Override` member exists | asserted reflectively |
| Migrations | none required; no schema change |

Stryker was not re-run: of its 17 safety-critical files only `AppDbContext` changed, and the change
adds a refusal allow-list that the integration suite covers directly on both sides.

---

## 7. What remains blocked

| Blocker | Kind |
| --- | --- |
| A licensed daily-close price export | **External configuration.** No engineering remains. |
| Approve endpoint | **Architectural.** Needs persisted proposals (Phase 5 §13.1). |
| Disengage the kill switch | **Deliberate.** Stays out of band, by design. |
| Notification transport | **External configuration.** Needs credentials. |
| Positions and portfolio | **Scope.** A future development block. |
| L4 promotion | **Evidence.** Phase 7 remains the authority; the window has not elapsed. |
