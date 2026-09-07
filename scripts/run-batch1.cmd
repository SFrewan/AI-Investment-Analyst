@echo off
setlocal
rem BATCH 1 OF THE RESUMED PRICE ACQUISITION - the approved one.
rem   SPENDS AT MOST 70 PROVIDER REQUESTS.
rem   eodhd-eod ONLY, the 70 symbols CMTL.US..NVEE.US, 2021-09-01..2026-08-31.
rem   NO splits. NO dividends. NO SPY. NO SEC call. NO scoring, orders or research.
rem   The 704 ceiling is enforced, not amended; what the interrupted run spent is charged first.
rem   The sealed universe and the identity table are neither modified nor resealed.
rem   BATCH 2 DOES NOT RUN. It is marked unauthorised in the runner and needs its own approval.

call "%~dp0run-batch.cmd" 1
exit /b %ERRORLEVEL%
