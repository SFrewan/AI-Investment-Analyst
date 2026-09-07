@echo off
setlocal
rem THE PRICE COMPLETION - the approved two-request remainder.
rem   SPENDS AT MOST 2 PROVIDER REQUESTS.
rem   eodhd-eod ONLY, exactly NXST.US and SIRI.US, 2021-09-01..2026-08-31.
rem   These are the two dispatches that failed on transport in batches 2 and 3.
rem   NO retry logic is added: each is attempted once, exactly as before.
rem   NO splits. NO dividends. NO SPY. NO SEC call. NO scoring, orders or research.
rem   The existing 704 ceiling is enforced, NOT amended and NOT superseded;
rem   the 409 already consumed are charged and asserted before anything is dispatched.
rem   The runner also asserts the ledger owes exactly 2 price requests, so a third
rem   outstanding symbol would stop it rather than be swept in.
rem   No manifest, identity, parser, quarantine, rate-limit, retry, handler-lifetime,
rem   pooling, timeout or keep-alive change is made by this script.
rem   THE SPLITS PHASE DOES NOT RUN. It needs its own decision and a superseding authorisation.

call "%~dp0run-batch.cmd" 4
exit /b %ERRORLEVEL%
