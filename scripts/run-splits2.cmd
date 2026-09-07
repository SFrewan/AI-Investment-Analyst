@echo off
setlocal
rem SPLIT BATCH 2 - SPENT. Marked unauthorised in the runner, and refuses before the network.
rem   SPENDS AT MOST 70 PROVIDER REQUESTS.
rem   eodhd-splits ONLY, the 70 symbols SAMG.US..ZS.US, 2021-09-01..2026-08-31.
rem   NO prices. NO dividends. NO SPY. NO SEC call. NO scoring, orders or research.
rem   Charged against the ACTIVE authorisation
rem   eodhd-sample400-splits-final-2021-09-to-2026-08, ceiling 73, NOT amended.
rem   Expected prior consumption under THAT authorisation is 1 (the MYO.US recovery) and
rem   is asserted before dispatch; the ledger must owe 72 before and 2 after, and 2
rem   units of ceiling must remain.
rem   It ran on 2026-09-05 and all 70 requests succeeded, so the prior-consumption pin now
rem   reads 71 against a batch that expects 1 and this script stops before dispatching.
rem   NO retry logic anywhere in this path: a failed request is recorded, not repeated.
rem   NO transport change. NO parser or quarantine change. NO seam change.
rem   BATCH 1 IS SPENT and marked unauthorised. BATCH 3 DOES NOT RUN.

call "%~dp0run-splits.cmd" 2
exit /b %ERRORLEVEL%
