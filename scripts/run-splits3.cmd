@echo off
setlocal
rem SPLIT BATCH 3 - the approved one.
rem   SPENDS AT MOST 70 PROVIDER REQUESTS.
rem   eodhd-splits ONLY, the 70 symbols GPC.US..MYFW.US, 2021-09-01..2026-08-31.
rem   NO prices. NO dividends. NO SPY. NO SEC call. NO scoring, orders or research.
rem   Charged against the successor authorisation's ceiling of 352, which is NOT amended.
rem   Expected prior consumption on that authorisation is 140 (batches 1 and 2) and is
rem   asserted before dispatch; 142 must remain after a fully dispatched batch.
rem   NO retry logic. NO transport change. NO parser or quarantine change. NO seam change.
rem   SPLIT BATCHES 1 AND 2 ARE SPENT and marked unauthorised. BATCHES 4 TO 6 DO NOT RUN.

call "%~dp0run-splits.cmd" 3
exit /b %ERRORLEVEL%
