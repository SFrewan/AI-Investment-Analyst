@echo off
setlocal
rem THE FINAL PRICE REQUEST - the approved one-symbol completion.
rem   SPENDS AT MOST 1 PROVIDER REQUEST.
rem   eodhd-eod ONLY, exactly NXST.US, 2021-09-01..2026-08-31.
rem   NO retry logic is added: it is attempted once, exactly as before.
rem   NO splits. NO dividends. NO SPY. NO SEC call. NO scoring, orders or research.
rem   NO transport change: handler lifetime, pooling, keep-alive, timeout, DNS and
rem   rate limits are all exactly as they were. NO parser or quarantine change.
rem   The existing 704 ceiling is enforced, NOT amended and NOT superseded;
rem   the 413 already consumed are charged and asserted before anything is dispatched.
rem   The runner asserts the ledger owes exactly 1 price request, that its fingerprint
rem   is the one earlier attempts recorded, and that its correlation is not one any
rem   earlier attempt already claimed.
rem   THE SPLITS PHASE DOES NOT RUN. It needs its own decision and a superseding authorisation.

call "%~dp0run-batch.cmd" 5
exit /b %ERRORLEVEL%
