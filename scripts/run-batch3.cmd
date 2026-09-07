@echo off
setlocal
rem BATCH 3 OF THE RESUMED PRICE ACQUISITION - the approved one, and the last price batch.
rem   SPENDS AT MOST 60 PROVIDER REQUESTS.
rem   eodhd-eod ONLY, the 60 symbols SIRI.US..ZYME.US, 2021-09-01..2026-08-31.
rem   NO splits. NO dividends. NO SPY. NO SEC call. NO scoring, orders or research.
rem   The existing 704 ceiling is enforced, NOT amended and NOT superseded;
rem   the 349 already consumed are charged and asserted before anything is dispatched.
rem   The sealed universe and the identity table are neither modified nor resealed.
rem   No parser or normalisation rule is added; no quarantine is reprocessed or reclassified.
rem   THE SPLITS PHASE DOES NOT RUN. It needs its own decision and a superseding authorisation.

call "%~dp0run-batch.cmd" 3
exit /b %ERRORLEVEL%
