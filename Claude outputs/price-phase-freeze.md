# Post-batch-1 review

**Read-only. No provider was called and no connector was enabled.** Nothing
was reprocessed, reclassified, persisted or removed; no parser rule was applied;
the authorisation was neither amended nor consumed; no batch was authorised.

Every figure below is recomputed from the ledger, the archive, the identity
table and the authorisation. None is copied from an earlier report.

## A. The remaining acquisition, reconciled

| | Recomputed |
| --- | ---: |
| Planned by the authorisation | 710 |
| Suppressible by the ledger today | **358** |
| …prices | 355 |
| …splits | 3 |
| **Outstanding prices** | **0** |
| **Outstanding splits** | **352** |
| **Total outstanding** | **352** |

| | Recomputed |
| --- | ---: |
| Authorised ceiling | 704 |
| Consumed by the interrupted run | 209 |
| Consumed by batch 1 | 205 |
| **Consumed in total** | **414** |
| **Remaining** | **290** |
| **Shortfall against the outstanding work** | **62** |

Price dispatches at the authorised window that produced no completed run: **60 failed** and **2 refused**. Each still spent its authorisation, because the runner charges at the point of intent so that real vendor spend can never be under-counted. That, and nothing else, is where the shortfall of 62 comes from.

Failed price dispatches anywhere in the ledger: 61. The difference of 1 belongs to earlier stages at other windows and is not charged to this authorisation.

## B. The shortfall: what the store can and cannot say about closing it

| | |
| --- | ---: |
| Prices first, then splits: prices need | 0 of 290 |
| …ceiling left after the price phase | 290 |
| …against outstanding splits | 352 |
| **Shortfall after the price phase** | **62** |
| Split runs already satisfied | 40 |
| Outstanding requests outside the authorised 355 | **0** |
| Ready members classified as non-equity | **0** |
| Ready symbols whose vendor price series came back empty | 8 |
| …of those, still owed a split request | 5 |

The shortfall does not move with ordering: prices and splits draw on one
ceiling, so completing prices first leaves the same arithmetic at the splits
phase. Ordering changes *what is finished when the ceiling runs out*, not
how far short it falls.

Nothing in the store reduces the split requirement on its own. A split
request becomes suppressible only by being dispatched and succeeding, so
suppression cannot arrive without spending the authorisation it would save.

The one candidate for exclusion the evidence even suggests: 5 ready symbols for which the vendor returned an empty end-of-day series. That the vendor holds no prices for a symbol is **not proof** that it holds no splits for it - the two endpoints are different datasets - so this is a hypothesis that would cost one request each to test and is recorded here as a hypothesis, not a saving:

- `BPYU.US`
- `KIN.US`
- `LBRA.US`
- `MSOF.US`
- `USCR.US`

## C. Every `eodhd-eod` payload that parsed as rows

| Symbols | Rows | Read | Bad | First bad | Terminal | Interior bad | Last read | Gap to first bad |
| --- | ---: | ---: | ---: | --- | --- | ---: | --- | ---: |
| `AAPL.US` | 1 | 0 | 1 | row 1 () | **no** | 0 | - | - |
| `CCF.US` | 557 | 556 | 1 | row 557 (2023-11-27) | yes | 0 | 2023-11-15 | 12 day(s) |
| `EVBG.US` | 713 | 711 | 2 | row 712 (2024-07-15) | yes | 0 | 2024-07-01 | 14 day(s) |
| `GPP.US` | 593 | 592 | 1 | row 593 (2024-01-25) | yes | 0 | 2024-01-09 | 16 day(s) |
| `LGIQ.US` | 903 | 900 | 3 | row 901 (2025-04-30) | yes | 0 | 2025-04-03 | 27 day(s) |
| `NGM.US` | 654 | 653 | 1 | row 654 (2024-04-23) | yes | 0 | 2024-04-08 | 15 day(s) |
| `ONEM.US` | 381 | 378 | 3 | row 379 (2023-03-08) | yes | 0 | 2023-03-03 | 5 day(s) |
| `QUMU.US` | 368 | 363 | 5 | row 364 (2023-02-21) | yes | 0 | 2023-02-09 | 12 day(s) |
| `SDC.US` | 571 | 569 | 2 | row 570 (2023-12-20) | yes | 0 | 2023-12-05 | 15 day(s) |
| `SHPW.US` | 989 | 969 | 20 | row 750 (2024-08-26) | **no** | 20 | 2025-08-11 | -350 day(s) |
| `VLDR.US` | 371 | 369 | 2 | row 370 (2023-02-24) | yes | 0 | 2023-02-17 | 7 day(s) |
| `WIRE.US` | 714 | 712 | 2 | row 713 (2024-07-15) | yes | 0 | 2024-07-02 | 13 day(s) |

### The five questions asked of the zero-priced rows

| | |
| --- | ---: |
| Zero-priced rows found | 42 |
| …with **every** numeric field zero | **38** |
| …with a **non-zero volume** against zero OHLC and adjusted_close | **4** |
| …that are terminal in their payload | **22** |
| …that are **interior** | **20** |
| …dated after the payload's last readable session | **22** |
| …falling on a day other symbols in this store traded | **42** |

Every zero-priced row, verbatim. A price row carries a date and six numbers
and no credential, so it is shown in full - this is the evidence any parser
policy would have to be written against.

| Symbols | Row | Date | All numeric zero | Volume | Terminal | After last read | Market open | The row |
| --- | ---: | --- | --- | ---: | --- | --- | --- | --- |
| `CCF.US` | 557 | 2023-11-27 | yes | 0 | yes | yes | yes | `{"date":"2023-11-27","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `EVBG.US` | 712 | 2024-07-15 | yes | 0 | yes | yes | yes | `{"date":"2024-07-15","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `EVBG.US` | 713 | 2024-07-16 | yes | 0 | yes | yes | yes | `{"date":"2024-07-16","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `GPP.US` | 593 | 2024-01-25 | yes | 0 | yes | yes | yes | `{"date":"2024-01-25","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `LGIQ.US` | 901 | 2025-04-30 | **no** | 500 | yes | yes | yes | `{"date":"2025-04-30","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":500}` |
| `LGIQ.US` | 902 | 2025-06-02 | **no** | 16676 | yes | yes | yes | `{"date":"2025-06-02","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":16676}` |
| `LGIQ.US` | 903 | 2025-07-03 | **no** | 250 | yes | yes | yes | `{"date":"2025-07-03","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":250}` |
| `NGM.US` | 654 | 2024-04-23 | yes | 0 | yes | yes | yes | `{"date":"2024-04-23","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `ONEM.US` | 379 | 2023-03-08 | yes | 0 | yes | yes | yes | `{"date":"2023-03-08","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `ONEM.US` | 380 | 2023-03-09 | yes | 0 | yes | yes | yes | `{"date":"2023-03-09","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `ONEM.US` | 381 | 2023-03-10 | yes | 0 | yes | yes | yes | `{"date":"2023-03-10","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `QUMU.US` | 364 | 2023-02-21 | yes | 0 | yes | yes | yes | `{"date":"2023-02-21","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `QUMU.US` | 365 | 2023-02-22 | yes | 0 | yes | yes | yes | `{"date":"2023-02-22","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `QUMU.US` | 366 | 2023-02-23 | yes | 0 | yes | yes | yes | `{"date":"2023-02-23","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `QUMU.US` | 367 | 2023-02-24 | yes | 0 | yes | yes | yes | `{"date":"2023-02-24","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `QUMU.US` | 368 | 2023-02-27 | yes | 0 | yes | yes | yes | `{"date":"2023-02-27","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `SDC.US` | 570 | 2023-12-20 | yes | 0 | yes | yes | yes | `{"date":"2023-12-20","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `SDC.US` | 571 | 2023-12-21 | yes | 0 | yes | yes | yes | `{"date":"2023-12-21","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `SHPW.US` | 750 | 2024-08-26 | **no** | 202 | **no** | no | yes | `{"date":"2024-08-26","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":202}` |
| `SHPW.US` | 751 | 2024-08-27 | yes | 0 | **no** | no | yes | `{"date":"2024-08-27","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `SHPW.US` | 752 | 2024-08-28 | yes | 0 | **no** | no | yes | `{"date":"2024-08-28","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `SHPW.US` | 753 | 2024-08-29 | yes | 0 | **no** | no | yes | `{"date":"2024-08-29","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `SHPW.US` | 754 | 2024-08-30 | yes | 0 | **no** | no | yes | `{"date":"2024-08-30","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `SHPW.US` | 755 | 2024-09-03 | yes | 0 | **no** | no | yes | `{"date":"2024-09-03","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `SHPW.US` | 756 | 2024-09-04 | yes | 0 | **no** | no | yes | `{"date":"2024-09-04","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `SHPW.US` | 757 | 2024-09-05 | yes | 0 | **no** | no | yes | `{"date":"2024-09-05","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `SHPW.US` | 758 | 2024-09-06 | yes | 0 | **no** | no | yes | `{"date":"2024-09-06","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `SHPW.US` | 759 | 2024-09-09 | yes | 0 | **no** | no | yes | `{"date":"2024-09-09","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `SHPW.US` | 760 | 2024-09-10 | yes | 0 | **no** | no | yes | `{"date":"2024-09-10","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `SHPW.US` | 761 | 2024-09-11 | yes | 0 | **no** | no | yes | `{"date":"2024-09-11","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `SHPW.US` | 762 | 2024-09-12 | yes | 0 | **no** | no | yes | `{"date":"2024-09-12","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `SHPW.US` | 763 | 2024-09-13 | yes | 0 | **no** | no | yes | `{"date":"2024-09-13","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `SHPW.US` | 764 | 2024-09-16 | yes | 0 | **no** | no | yes | `{"date":"2024-09-16","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `SHPW.US` | 765 | 2024-09-17 | yes | 0 | **no** | no | yes | `{"date":"2024-09-17","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `SHPW.US` | 766 | 2024-09-18 | yes | 0 | **no** | no | yes | `{"date":"2024-09-18","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `SHPW.US` | 767 | 2024-09-19 | yes | 0 | **no** | no | yes | `{"date":"2024-09-19","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `SHPW.US` | 768 | 2024-09-20 | yes | 0 | **no** | no | yes | `{"date":"2024-09-20","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `SHPW.US` | 769 | 2024-09-23 | yes | 0 | **no** | no | yes | `{"date":"2024-09-23","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `VLDR.US` | 370 | 2023-02-24 | yes | 0 | yes | yes | yes | `{"date":"2023-02-24","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `VLDR.US` | 371 | 2023-02-27 | yes | 0 | yes | yes | yes | `{"date":"2023-02-27","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `WIRE.US` | 713 | 2024-07-15 | yes | 0 | yes | yes | yes | `{"date":"2024-07-15","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |
| `WIRE.US` | 714 | 2024-07-16 | yes | 0 | yes | yes | yes | `{"date":"2024-07-16","open":0,"high":0,"low":0,"close":0,"adjusted_close":0,"volume":0}` |

**4 of 42 zero-priced rows carry a non-zero volume.** "All-zero sentinel" is therefore false as a description of this data, and any rule written to that shape would not have matched them. Whatever a zero-priced row means, it is not uniformly "the vendor wrote a row of zeroes".

Bad rows that are **not** zero-priced: 1. They are a different failure and are listed below.

| Symbols | Row | Kind | The row |
| --- | ---: | --- | --- |
| `AAPL.US` | 1 | no usable date | `{"warning":"Data is limited by one year as you have free subscription"}` |

## D. Gate 6, with the rule exactly as it stands

| | |
| --- | ---: |
| Acquirable members holding a series | 339 of 355 |
| `not-yet-acquired` - never requested | **0** |
| `no-series` - requested and empty | **16** |
| `interior-gap` - beyond 4 sessions | **114** |
| `unexplained-discontinuity` | **0** |
| **Faulted members** | **24** |
| Gate 6 with acquisition incomplete | **FAIL** |
| Gate 6 if acquisition were declared complete | **FAIL** |

The two rows differ only in the `not-yet-acquired` safeguard added earlier;
the tolerances, the zero-fault threshold and the membership rule are untouched.

Every faulted member, grouped by what is actually wrong with it:

| Bucket | Members |
| --- | ---: |
| terminal/trailing anomaly | 10 |
| interior gap | 8 |
| requested, vendor returned an empty series | 5 |
| other | 1 |

| Member | Bucket | Fault | Reason |
| --- | --- | --- | --- |
| `CRMZ.US` | interior gap | interior-gap | an interior gap of 9 session(s) exceeds the 4 a holiday run can explain |
| `DYNR.US` | interior gap | interior-gap | an interior gap of 15 session(s) exceeds the 4 a holiday run can explain |
| `FCCN.US` | interior gap | interior-gap | an interior gap of 35 session(s) exceeds the 4 a holiday run can explain |
| `OMTK.US` | interior gap | interior-gap | an interior gap of 10 session(s) exceeds the 4 a holiday run can explain |
| `QPRC.US` | interior gap | interior-gap | an interior gap of 15 session(s) exceeds the 4 a holiday run can explain |
| `RBCN.US` | interior gap | interior-gap | an interior gap of 320 session(s) exceeds the 4 a holiday run can explain |
| `SDRL.US` | interior gap | interior-gap | an interior gap of 43 session(s) exceeds the 4 a holiday run can explain |
| `UFAB.US` | interior gap | interior-gap | an interior gap of 7 session(s) exceeds the 4 a holiday run can explain |
| `SHPW.US` | other | no-series | acquirable, a request for it succeeded, and no closing price is held for any session in its membership span |
| `BPYU.US` | requested, vendor returned an empty series | no-series | acquirable, a request for it succeeded, and no closing price is held for any session in its membership span |
| `KIN.US` | requested, vendor returned an empty series | no-series | acquirable, a request for it succeeded, and no closing price is held for any session in its membership span |
| `LBRA.US` | requested, vendor returned an empty series | no-series | acquirable, a request for it succeeded, and no closing price is held for any session in its membership span |
| `MSOF.US` | requested, vendor returned an empty series | no-series | acquirable, a request for it succeeded, and no closing price is held for any session in its membership span |
| `USCR.US` | requested, vendor returned an empty series | no-series | acquirable, a request for it succeeded, and no closing price is held for any session in its membership span |
| `CCF.US` | terminal/trailing anomaly | no-series | acquirable, a request for it succeeded, and no closing price is held for any session in its membership span |
| `EVBG.US` | terminal/trailing anomaly | no-series | acquirable, a request for it succeeded, and no closing price is held for any session in its membership span |
| `GPP.US` | terminal/trailing anomaly | no-series | acquirable, a request for it succeeded, and no closing price is held for any session in its membership span |
| `LGIQ.US` | terminal/trailing anomaly | no-series | acquirable, a request for it succeeded, and no closing price is held for any session in its membership span |
| `NGM.US` | terminal/trailing anomaly | no-series | acquirable, a request for it succeeded, and no closing price is held for any session in its membership span |
| `ONEM.US` | terminal/trailing anomaly | no-series | acquirable, a request for it succeeded, and no closing price is held for any session in its membership span |
| `QUMU.US` | terminal/trailing anomaly | no-series | acquirable, a request for it succeeded, and no closing price is held for any session in its membership span |
| `SDC.US` | terminal/trailing anomaly | no-series | acquirable, a request for it succeeded, and no closing price is held for any session in its membership span |
| `VLDR.US` | terminal/trailing anomaly | no-series | acquirable, a request for it succeeded, and no closing price is held for any session in its membership span |
| `WIRE.US` | terminal/trailing anomaly | no-series | acquirable, a request for it succeeded, and no closing price is held for any session in its membership span |

## E. Gate 9 and Gate 12

| Gate 9 - the second run, empirically | |
| --- | ---: |
| Requests re-planned | 710 |
| …the ledger would suppress | **358** |
| …that would still dispatch | **352** |
| Ingestion runs before / after re-planning | 1914 / 1914 |
| Observations before / after re-planning | 1055167 / 1055167 |
| **New runs created by re-planning completed work** | **0** |
| **New observations created** | **0** |

| Gate 12 - de-duplication | Rows | Distinct identities | Excess |
| --- | ---: | ---: | ---: |
| financials | 649597 | 649597 | 0 |
| prices | 399126 | 399126 | 0 |
| splits | 5 | 5 | 0 |

## F. Transport, and the parser policy, as evidence only

| Price dispatches at the authorised window | Count |
| --- | ---: |
| Runs recorded | 434 |
| …succeeded | 372 |
| …failed on transport | 60 |
| …refused by the seam | 2 |

**Transport.** `socket:HostNotFound` was recorded on the first dispatch of
several freshly started processes, and at least one cold first dispatch
succeeded. Process-start name-resolution instability is therefore a supported
hypothesis and **not** a proven deterministic cause. No DNS warm-up, retry,
handler-lifetime, pooling, keep-alive, timeout or rate-limit change has been
made, and the diagnostic architecture is exactly as it was.

**Parser policy.** Terminal zero-priced rows exist; some of them carry a
non-zero volume against zero prices; and at least one payload has a genuine
interior bad-row run with valid data on both sides of it. A partial-acceptance
rule keyed on "terminal" or on "all fields zero" would match neither case, so
**no partial-acceptance rule is currently justified** and none is implemented.
Nothing was reprocessed or reclassified.

## G. The authorisation counter

| | |
| --- | ---: |
| Consumed by batch 1, from its own artefact | 205 |
| Consumed in total | **414 of 704** |
| Remaining | **290** |
| A freshly loaded authorisation starts at | 0 |
| …and after charging what is on disk | 414, leaving 290 |
| Public methods on the type | Covers, RecordPriorConsumption, RecordSuppressed, TryConsume |
| Public settable properties | **none** |

A new process does start the counter at zero - that is exactly why the batch
runner charges what earlier processes wrote before it dispatches anything. The
guarantee is not that the object remembers; it is that the runner refuses to
begin until the record on disk has been charged against it. Nothing on the type
can credit a dispatch back: there is no setter and no method that decreases the
count, and over-charging throws rather than clamping.

## H. The sealed universe and the identity table

| | |
| --- | ---: |
| Manifest bytes | 245126 against 245126 |
| SHA-256 | `c3667215b1e28c2093ed430e7ade180c40dfe616aef1f8d64e485721938f68db` |
| Matches the approved digest | **yes** |
| Members | 400 |
| Acquisition-ready | 355 |
| Not ready | 45 |
| Ready members classified as non-equity | **0** |
| Authorisation digest | `473c49f97eb8c84e638536affe836a7f5262790e0f2e4ac9444d9ad87c7ce465` |
| Authorisation consumed by this review | **0** |

## I. Scope integrity

| | |
| --- | ---: |
| Price observations outside the authorised window | **0** |
| Price observations for an **excluded** member | **0** |
| Price observations under an identifier outside the ready 355 | 21319 |
| …of which belong to no member of this universe at all | 21319 - earlier stages, predating this manifest |
| Sources this phase's batches dispatched | eodhd-eod |
| Sources present anywhere in the ledger | eodhd-eod, eodhd-splits, sec-edgar |
| Dividend or benchmark runs | **0** - no such source exists in the ledger |

## J. The superseding authorisation, drafted only

Written to `artifacts/universe/proposed-acquisition-eodhd-splits.json`, **not** to `declarations/`.
Nothing loads it, and the existing declaration is untouched.

| | |
| --- | ---: |
| Authorisation id | `eodhd-sample400-splits-2021-09-to-2026-08` |
| Supersedes | `eodhd-sample400-prices-and-splits-2021-09-to-2026-08` |
| Source | `eodhd-splits` |
| Symbols | 355 |
| Window | 2021-09-01..2026-08-31 |
| Planned | 355 |
| Already satisfied | 3 |
| **Dispatch ceiling** | **352** |
| Already consumed, carried as evidence | 414 |
| Digest | `f96e62c76aa424a6103b7abb9e898bbb2899a944e7ac8aa2044bd56699799c1e` |

The ceiling is not chosen; it is forced. The loader refuses any declaration
whose ceiling is not planned less already-satisfied, and planned is symbols
times sources - so a ceiling of one more than the outstanding work cannot be
written down at all. There is no discretionary headroom to spend.

