# Development Block 2 — EODHD market-data provider

**Status: ENGINEERING READY. Observation window NOT active. External configuration required.**
**Autonomy: L3. Unchanged by this block, and unchangeable by it.**
**Not a phase. There is no Phase 9. See [`../Phases/ROADMAP.md`](../Phases/ROADMAP.md) §9.**

---

## 1. What this adds

The first real external market-data vendor. Until now the only source of closing prices was
`PriceHistoryFileProvider`, which reads a CSV the operator exported by hand. This adds EODHD's
end-of-day API through the same contracts — one more `IDataProvider`, one more `ISourceDefinition`,
one more `INormalizer`, and a registration. **No second pipeline, no new architecture.**

| File | What it is |
| --- | --- |
| `Infrastructure/Configuration/EodhdOptions.cs` | Section `Providers:Eodhd`. Key, host, quota, exchange sessions, licensing terms. |
| `Infrastructure/Ingestion/Providers/EodhdProvider.cs` | `IDataProvider`, source id `eodhd-eod`. Fetches bytes; parses nothing. |
| `Infrastructure/Ingestion/Providers/EodhdSource.cs` | The registry entry, **inactive**. |
| `Infrastructure/Normalization/EodhdDailyPriceNormalizer.cs` | Archived JSON → `security.close` observations. |
| `Infrastructure/DependencyInjection.cs` | `AddEodhd(...)`, plus the normaliser registration. |
| `Api/appsettings.json` | The section, shipped disabled and **with no key**. |

EDGAR is untouched and still registered. The operator's file export is untouched and still
registered. Which source a run uses is decided by the source on the request.

---

## 2. The problem this block actually had to solve

EODHD's end-of-day row is:

```json
{"date":"2026-08-27","open":2.5,"high":3.1,"low":2.4,"close":2.75,"adjusted_close":2.6,"volume":11}
```

**A trading date, and no times.** No session close, no timezone, no statement of when the row
became public. The platform's provenance needs two instants for every observation, and one of them
— `PublishedAtUtc` — is what every point-in-time judgement in the system is made from. Phase 7's
evidence guard admits a prediction on that field and nothing else.

Three ways to fill it, two of them wrong:

- **Retrieval time as publication time.** Forbidden. It would make every historical row look as if
  it were published the moment we fetched it.
- **A trading calendar compiled into the connector.** It would assert exchange hours nobody
  configured, and be silently wrong for any market whose hours it got out of date on.
- **The operator states the exchange's session.** What this block does.

`EodhdOptions.Exchanges` holds, per exchange code, a `SessionCloseUtc` and a `PublicationDelay`.
The normaliser computes `AsOfUtc = tradingDate + SessionCloseUtc` and `PublishedAtUtc = AsOfUtc +
PublicationDelay`, and **writes the assumption onto every observation as a caveat**, naming the
exchange and both values. An exchange nobody stated quarantines the payload under
`market-data.unstated-session@1` rather than being guessed at.

**Erring late is safe; erring early is not.** A publication time earlier than the truth lets a
backtest act on a price before anybody could have seen it. The default delay is four hours and is
meant to be generous.

Two further deliberate choices:

- **The raw `close`, never `adjusted_close`.** The adjusted figure is retroactively rewritten by
  every later split and dividend, so the same row would mean different things on different days —
  exactly what a bitemporal ledger exists to prevent.
- **Close only.** Open, high, low and volume are in the archived payload and stay there. The
  existing discovery and validation paths read `security.close` and nothing else; a later block can
  normalise more out of the archive without re-fetching a byte.

---

## 3. Configuration

### The secret

**The API key is never committed.** `appsettings.json` ships the section with no `ApiKey` at all.

```powershell
cd src\AI.Investment.Api
dotnet user-secrets set "Providers:Eodhd:ApiKey" "<your EODHD API token>"
```

Or, for a deployment, the environment variable `Providers__Eodhd__ApiKey`. Nothing else is
acceptable: see [`../SECURITY.md`](../SECURITY.md).

The connector refuses to run without it, naming the setting and never the value. The key is
redacted out of anything the connector throws — in both its raw and its URL-escaped form, because
the escaped form is what appears in a URI.

### The rest

```jsonc
"Providers": {
  "Eodhd": {
    "Enabled": true,
    "BaseAddress": "https://eodhd.com/",
    "MaxRequestsPerMinute": 60,
    "LicensingNotes": "EODHD <plan name>; storage and automated processing permitted, redistribution not.",
    "RedistributionAllowed": false,
    "RetentionDays": null,
    "Exchanges": [
      { "Code": "US", "SessionCloseUtc": "20:00:00", "PublicationDelay": "04:00:00" }
    ]
  }
}
```

`LicensingNotes` is **required** when enabled — the terms depend on which subscription this
installation bought, and a registry entry that guessed would record a licensing claim nobody made.
`SessionCloseUtc` is a wall-clock UTC time; a market whose close moves with daylight saving needs
the value adjusted, because encoding a timezone rule here would be the connector inventing a
trading calendar.

`MaxRequestsPerMinute` becomes the connector's declared `ProviderQuota`, which the **existing**
gateway rate limiter enforces. The connector adds no limiter of its own and **does not retry**: the
gateway owns the run and its pacing, and retrying underneath it would spend quota nothing was
counting.

---

## 4. Symbols and watches

Symbols are `TICKER.EXCHANGE` — `AAPL.US`, `BP.LSE`. **No ticker universe is compiled in.** The
instruments observed are the ones an operator puts a watch on, through the existing surface added
in Block 1:

```
POST /api/operator/watches
{
  "name": "AAPL daily review",
  "targetKind": "Security",
  "targetIdentifier": "AAPL.US",
  "intervalMinutes": 1440,
  "cooldownMinutes": 240,
  "capability": "OpportunityManagement",
  "cycleTemplate": "equity-price-review"
}
```

Three to five symbols is enough to validate the pipeline. Nothing in Domain changes to add or
remove one.

An identifier that is not `TICKER.EXCHANGE` — a bare ticker, two dots, a leading dot, a query
character, a path traversal — is **refused, not escaped**. Escaping turns a malformed identifier
into a valid request for something else.

---

## 5. Failure handling

| Condition | Result |
| --- | --- |
| No key configured | Refused before anything is sent, naming the setting. |
| 401 / 403 | Failure saying the key is wrong, expired, or not covered by the plan. The key is not shown. |
| 429 | Failure saying it was rate-limited and to lower `MaxRequestsPerMinute`. **One attempt, no retry.** |
| 404 | Failure saying the ticker or exchange suffix is wrong. |
| 5xx | Failure carrying the status code. |
| Transport error | Re-thrown with a message this connector wrote, redacted. |
| 200 with an empty body | **Refused, not archived.** An empty body and an empty series are different facts. |
| Not JSON, or an error document | Quarantined `eodhd.unexpected-shape@1`. Nothing from the body is copied into the reason — an auth-failure page can carry the token that failed. |
| Empty array | Quarantined `market-data.empty-series@1`. |
| A row missing a date or a usable close | Quarantined `market-data.unreadable-row@1`. **The whole payload** — a hole in a time series produces confident, wrong returns. |
| Exchange not configured | Quarantined `market-data.unstated-session@1`. |
| Stated publication still in the future | Quarantined `market-data.impossible-ordering@1`. |

Every one of these is recorded through the existing ingestion ledger and quarantine mechanism.

---

## 6. Activating it

Engineering is complete. **These steps are operational.**

1. Hold an EODHD subscription and its API token.
2. Set the token in user-secrets or the environment. **Not in a file.**
3. Set `Enabled`, `LicensingNotes` and at least one exchange session in `appsettings` or the
   environment.
4. Restart. `AddEodhd` registers the connector only when `Enabled` is true.
5. **Activate the source** — `POST /api/operator/sources/{id}/activation` for `eodhd-eod`
   (authenticated, `AdministerWatches`). The definition is seeded inactive.
6. **Register watches** on `TICKER.EXCHANGE` symbols against `equity-price-review`.
7. Ensure a policy exists for `Capability.OpportunityManagement`. A capability with no configured
   policy is denied.
8. Let it run. The observation window is elapsed time.

---

## 7. What this block does not claim

**The observation window is not active.** This environment holds no EODHD credential, so no live
call has been made and none was faked. Everything below the network boundary is verified against
deterministic fixtures; the network boundary itself is verified against a stubbed transport.

- **ENGINEERING READY** — the connector, normaliser, registration, configuration and tests exist
  and pass.
- **EXTERNAL CONFIGURATION REQUIRED** — a subscription, a token, and the exchange sessions.
- **OBSERVATION WINDOW ACTIVE** — not yet, and it cannot be until steps 1 to 8 are done and time
  has passed.

No performance, hit rate, calibration or breach rate is claimed. No autonomy level changed. No
broker, no venue, no execution.

---

## 8. Verification performed

| Check | Result |
| --- | --- |
| Release build, `TreatWarningsAsErrors` | 0 errors, 0 warnings |
| Full suite, 6 assemblies | 1912 tests, 0 failed, 0 skipped (was 1835) |
| Response parsing, valid OHLC | asserted |
| Malformed JSON, error document, non-array, empty array | asserted, each with its own rule id |
| Bad row: missing date, bad date format, non-numeric / zero / negative close | asserted |
| Quoted numeric close | asserted |
| 401, 403, 404, 429, 500, 502 | asserted, each distinct |
| No retry on failure | asserted (exactly one transport call) |
| Empty body on a 200 | asserted refused |
| Timestamps from the stated session, never retrieval time | asserted |
| The assumption appears as a caveat on every observation | asserted |
| Unstated exchange quarantines | asserted |
| Symbol validation, including traversal and query injection | asserted |
| Provenance: source id, record id, three instants | asserted |
| Raw-source traceability (exact bytes returned, media type, record id) | asserted |
| Admission on the activated source | asserted |
| Missing API key | asserted refused before any request |
| Key absent from thrown messages, raw and escaped | asserted |
| Key absent from the registry entry and every public member | asserted |
| Shipped defaults carry no credential | asserted |
| Secret scan | 0 findings in the working tree |
| Migrations | none required; no schema change |

No live EODHD call is made by any test. Stryker was not re-run: none of its 17 safety-critical
files changed.

---

## 9. Left for a future block

- The other OHLCV attributes, read out of the archive rather than re-fetched.
- Corporate-action handling, which is what would make an adjusted series safe to store.
- A daylight-saving-aware exchange calendar, if a market with a moving close is ever watched.
- Every other EODHD endpoint: fundamentals, news, options, forex, crypto. None is needed here.
