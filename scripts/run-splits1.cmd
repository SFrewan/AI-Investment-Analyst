@echo off
setlocal
rem SPLIT BATCH 1 - the approved one. THE GPC.US RECOVERY.
rem   SPENDS AT MOST 1 PROVIDER REQUEST.
rem   eodhd-splits ONLY, the single symbol GPC.US, 2021-09-01..2026-08-31.
rem   NO prices. NO dividends. NO SPY. NO SEC call. NO scoring, orders or research.
rem   Charged against the ACTIVE successor authorisation
rem   eodhd-sample400-splits-remainder-2021-09-to-2026-08, ceiling 143, NOT amended.
rem   Expected prior consumption under THAT authorisation is 0 and is asserted before
rem   dispatch; 142 must remain after it, and the ledger must then owe 142.
rem   GPC.US failed once under the superseded authorisation. This is a NEW attempt with a
rem   NEW attempt identity, not a retry: there is no retry logic anywhere in this path.
rem   NO transport change. NO parser or quarantine change. NO seam change.
rem   SPLIT BATCHES 2 TO 4 DO NOT RUN. They are marked unauthorised in the runner.

call "%~dp0run-splits.cmd" 1
exit /b %ERRORLEVEL%
