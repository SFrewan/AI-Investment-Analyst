# Development Block 3 — Position and portfolio read model

**Status: IMPLEMENTED. Nothing external is blocking it.**
**Autonomy: L3. Unchanged by this block, and unchangeable by it.**
**Not a phase. There is no Phase 9. See [`../Phases/ROADMAP.md`](../Phases/ROADMAP.md) §9.**

---

## 1. What this closes

The capability review of 2026-08-29 found three things blocked on one missing piece:

- **Portfolio-level risk** — `ExposureSnapshot.ExposureTo(instrument)` answered zero for every
  instrument, because `LedgerExposureProvider` passed `exposureByInstrument: null`. The
  concentration ceiling could never bind.
- **Unrealised P&L** — no position state to compute it from.
- **The future dashboard** — no portfolio read model to render.

All three needed the same thing: a record of what is held. This block adds it, and nothing else.

---

## 2. The shape, and why

**A holding is not stored. It is replayed.**

| | |
| --- | --- |
| `PositionEvent` (Domain) | One fill, recorded as it affected a holding. Append-only. |
| `Position` (Domain) | A projection: quantity, cost basis, realised P&L. Never a row. |
| `PositionCalculator` (Domain) | The one replay, used by the read model, the exposure provider and every test. |
| `IPositionEventStore` / `EfPositionEventStore` | Append and read. No update, no delete. |
| `PortfolioReader` (Application) | Composes events, the capital ledger and the price read. |
| `PortfolioController` (Api) | `GET /api/portfolio`, `GET /api/portfolio/positions`. |

This follows the rule the ledger already states: *"There is no balance column, here or anywhere.
Balances are projections of these rows."* A stored quantity can be wrong while every event behind it
is right, and nothing in the data would say so.

**There is no second execution mechanism and no second ledger.** A fill has two consequences — money
moves and a holding changes — and both are now recorded inside the one authorised window in
`OpportunityExecutor`, immediately after the postings.

---

## 3. Idempotency

**The venue's own reference is the identity**, and a unique index on it is the mechanism:

```
ux_position_events_venue_reference  UNIQUE (venue_reference)
```

Not a check-then-insert in application code — two concurrent callers both pass that. The store
attempts the insert and catches PostgreSQL's `23505`, so the race is decided by the constraint,
which is the only party that can decide it. `AppendAsync` returns whether the event was new.

Minting a second identity would have created two answers to "was this fill applied?", and the one
the database enforced uniqueness on would be the one nobody reconciles against.

Proven against a real PostgreSQL: the same fill twice records once; three concurrent appends record
once; the resulting quantity is unchanged in both cases.

---

## 4. P&L semantics

**Average cost, long only, fees excluded.**

- **Acquire**: quantity increases; cost basis increases by the consideration.
- **Dispose**: cost is relieved *in proportion to the basis*, not by an average cost × quantity.
  Realised P&L is proceeds less relieved cost.
- **Close**: disposing of the whole holding relieves the whole basis by construction, so a closed
  position reports a basis of **exactly zero** rather than a rounding residue.
- **Reopen**: a fresh basis; realised P&L is retained.

`CostBasis` is authoritative and `AverageCost` is derived from it — never the other way round. An
average rounded to four decimal places and multiplied back does not reproduce what was paid, and the
error accumulates over every partial disposal.

**Fees are carried on the event but excluded from cost and from realised P&L**, because the capital
ledger posts them to their own account rather than into `Positions`. This model therefore agrees
with the ledger about what a holding cost.

**Long only.** A disposal larger than the holding is refused, not clamped. Clamping would convert a
defect — a fill applied against the wrong instrument, a missing acquisition — into a plausible
position. Nothing in this platform's execution path can open a short, so the model does not
represent one.

**The ledger is unchanged.** Its realised gain and loss postings still use the caller-supplied
`ExecutionRequest.CostBasis`. Deriving that from the position model instead would be a change to
capital accounting, which is out of scope here — see §8.

---

## 5. The current-price dependency

Prices come from the **existing** observation store through the **existing** `PriceSeriesReader` —
the same point-in-time, restatement-aware read the opportunity discoverer uses. No second price
store, no cache, and the portfolio layer never calls a provider.

**When there is no price, there is no number.**

| State | Meaning | Market value / unrealised P&L |
| --- | --- | --- |
| `Available` | A published close was found | Computed |
| `NoObservedPrice` | Nothing published for this instrument | **`null`** |
| `NotHeld` | Nothing is held, so none is needed | `null` |

No fallback to cost, no last-known price carried forward, no zero. `NoObservedPrice` and `NotHeld`
are kept apart so a portfolio of settled trades does not look like a portfolio with a broken feed.

**The portfolio total is `null` unless every open position was valued.** A total that quietly omitted
the unpriced positions would be smaller than the truth and would still look like an answer;
`valuedPositions` and `unvaluedPositions` say how much of the book the valuation covers.

Since the observation store is empty until the window is activated, **every position will report
`NoObservedPrice` today.** That is correct behaviour, not a defect.

---

## 6. Exposure and risk

`LedgerExposureProvider` now fills `exposureByInstrument` by replaying the same events, so
`LimitEngine`'s concentration check sees real instrument exposure.

**At cost, not at market value, deliberately.** The total it is compared against is the ledger's
`Positions` balance, which is at cost; mixing bases would produce a ratio of two different things.
It also means the ceiling does not depend on a price being observable — **a concentration limit that
silently loosened whenever a price feed went quiet would be worse than one that could not be
computed at all.**

Closed positions are omitted rather than reported as zero exposure. No limit was weakened, no
kill-switch or policy behaviour changed.

---

## 7. Safety

One guard was **added** to `AppDbContext.GuardWrites()`, none weakened:

> Position events are append-only. Modifying or deleting one rewrites a quantity, a cost and a
> realised profit at once, with no counter-entry anywhere.

Unlike the seam's own bookkeeping, position events are **not** exempt from needing an authorised
decision to be created — a fill moves money. So:

- Written only inside the authorisation window `IActionGateway` opens.
- Never modified. Never deleted. Both refused even inside that window.
- Proven against PostgreSQL, in both directions.

The API surface is read-only and requires a new privilege, `ViewPortfolio` — the only read privilege
in the system, separate from the four decision ones because reading the book and being able to act
on it are different grants. `POST` and `DELETE` on the portfolio routes return 405.

---

## 8. Known limitations

| | |
| --- | --- |
| **Realised P&L is computed twice** — once by the ledger from `ExecutionRequest.CostBasis`, once here from the replay. They agree when the caller supplies a basis equal to the relieved cost, and nothing yet enforces that. Reconciling them means changing capital accounting. | Future block |
| **No corporate actions.** A split or a dividend would silently corrupt quantity and cost. | Future block |
| **Single currency per instrument.** A position in two currencies is two positions; the model does not convert. | By design |
| **No short positions, leverage, margin or derivatives.** | By design |
| **No mark-to-market P&L in the capital ledger.** Unrealised profit is a read-model figure and is never posted. | By design |
| **No portfolio optimisation, rotation or rebalancing.** | Out of scope |

---

## 9. Verification performed

| Check | Result |
| --- | --- |
| Release build, `TreatWarningsAsErrors` | 0 errors, 0 warnings |
| Full suite, 6 assemblies | 1992 tests, 0 failed, 0 skipped (was 1912) |
| Open, increase, reduce, close, reopen | asserted |
| Average cost; closed position leaves exactly zero basis | asserted over four awkward divisors |
| Realised gain and loss | asserted |
| Zero, negative and over-disposal quantities | refused |
| Currency mismatch | refused |
| Determinism: same events, same position | asserted |
| Fill → position update inside the executor's authorised window | asserted |
| Same fill twice records once | asserted against PostgreSQL |
| Three concurrent appends of one fill record once | asserted against PostgreSQL |
| Sequential fills, multiple instruments, close-and-reopen | asserted against PostgreSQL |
| Save and reload | asserted against PostgreSQL |
| Event cannot be modified or deleted, even when authorised | asserted against PostgreSQL |
| Event cannot be written without an authorised decision | asserted against PostgreSQL |
| Price available / no observed price / not held | asserted |
| One unpriced position makes the portfolio total unavailable | asserted |
| A price published after now is not used | asserted |
| Cash comes from the capital ledger | asserted |
| Anonymous, bad key, and authenticated-without-privilege refused | asserted |
| Portfolio routes accept no writes | asserted |
| No response carries a credential | asserted |
| No public member can mutate an event or the store | asserted reflectively |
| Migration | one new table, `position_events`; no unrelated schema touched |
| Secret scan | 0 findings in the working tree |
