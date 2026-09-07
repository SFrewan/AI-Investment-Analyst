@echo off
setlocal
rem BATCH 2 OF THE RESUMED PRICE ACQUISITION - the approved one.
rem   SPENDS AT MOST 70 PROVIDER REQUESTS.
rem   eodhd-eod ONLY, the 70 symbols NXST.US..SIRE.US, 2021-09-01..2026-08-31.
rem   NO splits. NO dividends. NO SPY. NO SEC call. NO scoring, orders or research.
rem   The existing 704 ceiling is enforced, NOT amended and NOT superseded;
rem   the 279 already consumed are charged before anything is dispatched.
rem   The sealed universe and the identity table are neither modified nor resealed.
rem   No parser or normalisation rule is added; no quarantine is reprocessed.
rem   BATCH 3 DOES NOT RUN. It is marked unauthorised in the runner and needs its own approval.

call "%~dp0run-batch.cmd" 2
exit /b %ERRORLEVEL%
