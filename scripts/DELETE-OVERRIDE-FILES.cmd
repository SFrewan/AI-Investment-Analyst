@echo off
setlocal
rem Removes the two files from the abandoned Development Schedule Override.
rem The other four files were already reverted in place; these two must be deleted.
cd /d "%~dp0.."
echo.
echo  Deleting the abandoned override files:
echo.
for %%F in (
  "src\AI.Investment.Application\Abstractions\IScheduleBoundaryOverride.cs"
  "src\AI.Investment.Api\Configuration\DevelopmentScheduleBoundaryOverride.cs"
) do (
  if exist %%F ( del /q %%F && echo   deleted %%F ) else ( echo   already gone %%F )
)
echo.
echo  Now run scripts\verify-reschedule-block.cmd
echo.
pause
endlocal
