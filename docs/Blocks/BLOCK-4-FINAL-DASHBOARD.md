# Development Block 4 — Final Investment Dashboard

**Status: IMPLEMENTED.**
**Autonomy: L3. Unchanged by this block, and unchangeable by it.**
**Not a phase. There is no Phase 9. See [`../Phases/ROADMAP.md`](../Phases/ROADMAP.md) §9.**

---

## 1. Architecture, and why this one

**Blazor WebAssembly.** Not a preference — a constraint the build machine settled.

A toolchain probe found **no Node and no npm** on the machine this repository is built and verified
on; only .NET SDKs. A React, Vue or Angular application could not have been installed, built or
tested here, and a frontend that cannot be built is a placeholder rather than a deliverable. Blazor
WebAssembly is a real SPA — components, routing, dependency injection, a build step — that compiles
and tests through the same `dotnet build` and `dotnet test` the rest of the repository already uses.

| | |
| --- | --- |
| Project | `src/AI.Investment.Dashboard` (`Microsoft.NET.Sdk.BlazorWebAssembly`) |
| Tests | `tests/AI.Investment.Dashboard.Tests` (xUnit + bUnit), 57 tests |
| Served from | `wwwroot/dashboard` on the API, same origin — **no CORS is opened** |
| API client | One class, `PlatformClient`. Nothing else in the project makes an HTTP call. |
| State | Three singletons: `OperatorSession`, `LocalizationState`, `RefreshState` |
| Styling | One stylesheet, written entirely in CSS logical properties |
| Charts | Inline SVG components in this project. No charting library. |

**This is not the Operator Console.** That page still exists at `/`, unchanged, and still owns
every safety-sensitive action. The dashboard is read-only.

One solution-wide setting is overridden for this project: `InvariantGlobalization`, which
`Directory.Build.props` sets to `true`. Under it every culture formats identically, so Arabic dates
and numbers would silently render as English ones. The override is scoped to the two projects that
render text for people.

---

## 2. Pages

| Page | Backed by | Notes |
| --- | --- | --- |
| Overview | `api/portfolio`, `api/opportunities`, `api/operations/escalations`, `api/data-plane/freshness` | Autonomy, live-execution and observation-window state stated in a banner |
| Market data | `api/sources`, `api/data-plane/freshness`, `api/data-plane/runs` | Market date, publication time and ingestion time kept visibly distinct |
| Opportunities | `api/opportunities?status=` | Server-side status filter; detail view at `api/opportunities/{id}` |
| Portfolio | `api/portfolio` | Per-position valuation state; total withheld when it cannot be determined |
| Position detail | `api/portfolio` | Price provenance: session close and source publication time |
| Capital | `api/capital/ledger` | Balances by account, stated as ledger-derived, not market value |
| Risk | `api/portfolio` | Exposure by instrument at cost, with an SVG bar chart |
| Validation | `api/validation/report` | Every figure carries its availability |
| Operations | `api/operations/{escalations,cycles,shadow}` | Shadow decisions labelled as measurements |
| Safety and autonomy | `api/autonomy/promotion`, `api/operations/grants` | L3 / live execution unavailable / promotion state |

---

## 3. The rule that shaped every page

**Zero is not unknown.**

Every figure the platform can decline to know is nullable in the transport type and stays nullable
to the screen. A missing value renders as a muted "—" or a named state, never as `0.00`:

- `NoObservedPrice` — the platform holds no published close for this instrument.
- `NotHeld` — the position is closed, so no price is needed.
- `Not measured` — validation admitted no prediction; this is an unmeasured result, not a failed one.
- The **portfolio total is withheld** when any open position lacks a price, with the platform's own
  count of how many, because a total that quietly skipped them would be smaller than the truth and
  would still look like an answer.

Backend enum names are never rendered. Each has a display label in both languages, and an unmapped
value renders a visible marker rather than leaking the identifier.

---

## 4. Authentication

The existing mechanism, unchanged: `X-Operator-Key` against `GET api/operator/whoami`.

- The key is held in one private field on `OperatorSession`. Only `ApplyTo` reads it, and only ever
  to write a request header.
- Never in a URL, never logged, never rendered, and the input is cleared the moment the answer
  arrives. Asserted by test.
- **401 and 403 are separate everywhere.** A lost session and a missing privilege lead an operator
  to different actions, and merging them sends somebody to re-enter a key that was fine.
- Sign-out drops the credential, the identity, and the browser's session storage.
- `Session.Has(privilege)` decides what to render and never what is permitted; the backend refuses
  an unprivileged call regardless, and every page handles the 403.

Reading the portfolio needs the `ViewPortfolio` privilege added in Block 3.

---

## 5. English and Arabic

Both are first-class. Choosing Arabic changes the document's `lang` and `dir`, mirrors the layout,
and reformats every date and number.

- Two resource files, one flat dictionary each, ~200 keys. **A test asserts the two key sets are
  identical**, so a missing translation fails the build rather than rendering in the wrong language.
- A missing key renders as `«key»` rather than falling back to English — an untranslated Arabic
  screen should look wrong, not deliberate.
- The stylesheet uses logical properties throughout (`margin-inline-start`, `border-inline-end`,
  `text-align: start`), so RTL is a real mirror rather than a translated LTR layout.
- The chart's plot group is flipped under RTL so bars grow in the reading direction; its labels,
  being text, are not mirrored.
- The language preference is kept in `sessionStorage` and cleared on sign-out. Every storage access
  is guarded — a browser that blocks site data still gets a working dashboard.

---

## 6. Refresh

One signal for the whole shell: a manual button in the top bar, a loading state, a last-refreshed
timestamp, and an error state per panel.

**A refresh already in flight is not started again** — two overlapping loads would double the
traffic and let the older response win the race to render.

**There is no automatic polling.** The platform's data changes when an operating cycle runs, not
continuously. Polling would imply a liveness the data does not have while multiplying load on it.

---

## 7. Running it

```powershell
# Publish the dashboard into the API's static files
dotnet publish src\AI.Investment.Dashboard -c Release -o artifacts\dashboard
xcopy /E /I /Y artifacts\dashboard\wwwroot src\AI.Investment.Api\wwwroot\dashboard

# Then run the API as usual
dotnet run --project src\AI.Investment.Api --launch-profile https
```

| | |
| --- | --- |
| Dashboard | `https://localhost:44367/dashboard/` |
| Operator console (unchanged) | `https://localhost:44367/` |

The API base address is runtime configuration in `wwwroot/appsettings.json`. Empty means "the
origin this page came from", which is the case when the API serves it. Point it elsewhere to run
the dashboard against another host — note that doing so requires a CORS policy the platform does not
currently open.

For frontend-only work, `dotnet run --project src\AI.Investment.Dashboard` starts the dev server.

**No secret belongs in any dashboard file.** The operator key is entered at sign-in and never
stored on disk.

---

## 8. Verification

| Check | Result |
| --- | --- |
| Release build, `TreatWarningsAsErrors` | 0 errors, 0 warnings |
| Full suite, 7 assemblies | 2049 tests, 0 failed, 0 skipped (was 1992) |
| Dashboard tests | 57, all passing |
| Sign in with a recognised key | asserted |
| Sign in refused, empty key not sent | asserted |
| Sign out clears the session | asserted |
| Key absent from the DOM, present in the header, absent from every URL | asserted |
| 401 vs 403 rendered differently | asserted |
| 5xx offers retry and shows no exception text | asserted |
| Every status code classified | asserted, ten cases |
| Every failure has a message in both languages | asserted |
| English default, Arabic switch, direction changes | asserted |
| Empty and error states localized in Arabic | asserted |
| No backend enum name rendered, in either language | asserted |
| Resource key parity between languages | asserted |
| Unknown values never render as zero | asserted |
| Portfolio total withheld and explained | asserted |
| Refresh reloads; concurrent refresh suppressed | asserted |
| Migrations | none; this block adds no schema and no write path |
| Live market data | **none.** No test touches a network. |

---

## 9. Known limitations

- **Time-series charts do not exist**, because the data does not. The only series the platform will
  hold is a price history that begins when the observation window is activated. Adding a chart
  library for a dataset that is empty would be a dependency bought for a placeholder.
- **Evidence drill-down is a count, not a list.** The opportunity endpoints expose
  `EvidenceCount` but not the cited observation identifiers, so the dashboard shows the count. A
  read-model addition exposing the identifiers, and an endpoint resolving one to its observation
  and provenance, is a **future backend block** — the dashboard does not invent them.
- **No per-instrument position endpoint.** The detail view selects its row from the portfolio
  response rather than defining a second notion of a position in the client.
- **No limit ceilings on the Risk page.** The limit engine's configured ceilings are not exposed by
  any read endpoint; the page shows exposure and each instrument's share of it. Exposing
  `LimitSet` through a read model is a **future backend block**.
- **No operator actions.** Approving, rejecting, answering escalations and engaging the kill switch
  stay in the Operator Console, where they route through the action gateway and the policy engine.
  The dashboard has no write path at all, and no promote, live-execution, broker or kill-switch
  disengage control anywhere.
- Cross-origin hosting would need a CORS policy that is deliberately not opened.
