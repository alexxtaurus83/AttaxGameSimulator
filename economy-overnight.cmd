@echo off
setlocal EnableExtensions
cd /d "%~dp0"

rem ======================================================================================================
rem  ECONOMY CALIBRATION OVERNIGHT SUITE (plan 01d, Phase A -> user run -> Phase B).
rem  Generates six balanced economy log sessions over ALL 20 void-only boards, then a DRAFT analysis (no --apply).
rem  The training pipeline (selfplay / overnight-pipeline.cmd / ATLG v4 logs) is not involved.
rem
rem   Step 0  publish the console
rem   Step 1  s01_income_d2q0      d2 mirror, abilities OFF        clean income / board-scaling baseline        ~10 percent
rem   Step 2  s02_small_d1q1_vs_d2q0  d1+Q vs d2, swap colours     fast small games                              ~10 percent
rem   Step 3  s03_d2q0_vs_d3q1      weak vs strong, swap colours   comeback / saving behaviour                   ~20 percent
rem   Step 4  s04_mirror_d2q0       d2 mirror, dense value samples                                               ~15 percent
rem   Step 5  s05_mirror_d2q1       d2+Q mirror                                                                  ~15 percent
rem   Step 6  s06_shipped_d3q1      d3+Q mirror (the shipped engine): authoritative cast rates                   ~30 percent
rem   Step 7  analysis\economy-report.{md,json}  economy-analyze WITHOUT --apply (review before applying)
rem
rem  TIME: game counts below come from the 4-game pilot + 12-game probe measured on a 12-core machine (see IMPLEMENTATION_CHECKLIST.md):
rem        d2q0 4.4 s, d2q1 4.2 s, d3q0 11.4 s, d3q1 17.9 s per single-threaded game (default board, 10 value samples); 12 games of d3q1
rem        on all boards at --parallel 0 took 33.9 s (2.83 s/game, speed-up K = 6.3). Per-session probes on all boards then set the counts below.
rem        Target at most 4.5 hours for the six sessions, plus draft analysis.
rem        On a slower or faster machine the run is proportionally longer or shorter; Ctrl+C is safe (see below).
rem
rem  RESUMABLE: re-running skips every session whose .jsonl already exists. Logs are written to .tmp and renamed when complete,
rem  so an interrupted session is redone from its start, never half-used.
rem
rem  USAGE:   run economy-overnight.cmd (any folder). Close heavy programs and disable PC sleep.
rem  LIVE LOG: a second window opens (NO_LOGWINDOW=1 disables), or: powershell -c "Get-Content runs\economy-overnight-diverse-v2\pipeline.log -Wait -Tail 20"
rem  RESULTS:  runs\economy-overnight-diverse-v2\SUMMARY.txt and analysis\economy-report.md under that directory.
rem  THEN:     review opening diversity, actual balances, producing build, hashes and holdout before economy-analyze --apply.
rem
rem  Overrides (set before running), e.g. a smoke test:
rem     set "RUNDIR=runs\economy-smoke-v2" & set "S01_GAMES=2" & set "S02_GAMES=2" & set "S03_GAMES=2" & set "S04_GAMES=2" & set "S05_GAMES=2" & set "S06_GAMES=2" & set "VERIFY_GAMES=0" & set "HOLDOUT_GAMES=0" & set "NO_LOGWINDOW=1"
rem ======================================================================================================

if not defined RUNDIR set "RUNDIR=runs\economy-overnight-diverse-v2"
rem Historical game counts; seeded openings can change lengths, so re-pilot for a strict runtime budget.
if not defined S01_GAMES set "S01_GAMES=2500"
if not defined S02_GAMES set "S02_GAMES=2400"
if not defined S03_GAMES set "S03_GAMES=1000"
if not defined S04_GAMES set "S04_GAMES=2500"
if not defined S05_GAMES set "S05_GAMES=2600"
if not defined S06_GAMES set "S06_GAMES=1700"
if not defined VERIFY_GAMES set "VERIFY_GAMES=100"
if not defined HOLDOUT_GAMES set "HOLDOUT_GAMES=100"
if not defined OPENING_PLIES set "OPENING_PLIES=4"
if not defined SEED set "SEED=20261124"
set /a VERIFY_SEED=SEED+1009
set "PUBLISH=%RUNDIR%\console"
set "CON=%PUBLISH%\Attax.Console.exe"
set "LOG=%RUNDIR%\pipeline.log"

if not exist "%RUNDIR%" mkdir "%RUNDIR%"
type nul >> "%LOG%"
if not defined NO_LOGWINDOW start "economy log" powershell -NoProfile -Command "Get-Content -LiteralPath '%LOG%' -Wait -Tail 40"
echo Live log is in a second window, or run: powershell -c "Get-Content %LOG% -Wait -Tail 20"
echo To STOP: press Ctrl+C here and answer Y to "Terminate batch job". Re-running resumes.
call :stamp "economy-overnight start: RUNDIR=%RUNDIR% S01=%S01_GAMES% S02=%S02_GAMES% S03=%S03_GAMES% S04=%S04_GAMES% S05=%S05_GAMES% S06=%S06_GAMES% OPENING_PLIES=%OPENING_PLIES% VERIFY_GAMES=%VERIFY_GAMES% HOLDOUT_GAMES=%HOLDOUT_GAMES% SEED=%SEED% VERIFY_SEED=%VERIFY_SEED%"

rem ---------------------------------------------------------------------------------------------- build
if defined REBUILD del "%RUNDIR%\.built" 2>nul
if exist "%CON%" if exist "%RUNDIR%\.built" goto :built
call :stamp "STEP 0  build console (publish)"
dotnet publish Attax.Console\Attax.Console.csproj -c Release --nologo -o "%PUBLISH%" >> "%LOG%" 2>&1
if errorlevel 1 goto :fail
echo built > "%RUNDIR%\.built"
:built

rem ---------------------------------------------------------------------------------------------- sessions
rem :session <step> <name> <games> <red> <blue> <abilities> <swap> <samples> <seed offset>
call :session 1 s01_income_d2q0 %S01_GAMES% "classic:depth=2,quiescence=false" "classic:depth=2,quiescence=false" false false 0 0
if errorlevel 1 goto :fail
call :session 2 s02_small_d1q1_vs_d2q0 %S02_GAMES% "classic:depth=1,quiescence=true" "classic:depth=2,quiescence=false" true true 8 1
if errorlevel 1 goto :fail
call :session 3 s03_d2q0_vs_d3q1 %S03_GAMES% "classic:depth=2,quiescence=false" "classic:depth=3,quiescence=true" true true 12 2
if errorlevel 1 goto :fail
call :session 4 s04_mirror_d2q0 %S04_GAMES% "classic:depth=2,quiescence=false" "classic:depth=2,quiescence=false" true false 16 3
if errorlevel 1 goto :fail
call :session 5 s05_mirror_d2q1 %S05_GAMES% "classic:depth=2,quiescence=true" "classic:depth=2,quiescence=true" true false 12 4
if errorlevel 1 goto :fail
call :session 6 s06_shipped_d3q1 %S06_GAMES% "classic:depth=3,quiescence=true" "classic:depth=3,quiescence=true" true false 10 5
if errorlevel 1 goto :fail

rem ---------------------------------------------------------------------------------------------- draft analysis (no --apply)
if exist "%RUNDIR%\analysis\economy-report.json" goto :analysis_done
call :stamp "STEP 7  draft analysis (no --apply), verify games %VERIFY_GAMES%, holdout games %HOLDOUT_GAMES%"
"%CON%" economy-analyze --in "%RUNDIR%" --out "%RUNDIR%\analysis" --verify-games %VERIFY_GAMES% --holdout-games %HOLDOUT_GAMES% --seed %VERIFY_SEED% >> "%LOG%" 2>&1
if errorlevel 1 goto :fail
:analysis_done

rem ---------------------------------------------------------------------------------------------- summary
call :stamp "SUMMARY"
> "%RUNDIR%\SUMMARY.txt" echo ECONOMY OVERNIGHT RESULTS. Draft analysis only: nothing was applied. Report: %RUNDIR%\analysis\economy-report.md
for %%f in ("%RUNDIR%\s0*.jsonl") do echo log: %%~nxf >> "%RUNDIR%\SUMMARY.txt"
findstr /b /c:"SUMMARY games=" "%LOG%" >> "%RUNDIR%\SUMMARY.txt"
findstr /b /c:"CONVERGED" /c:"NOT CONVERGED" /c:"DRAFT ONLY" "%LOG%" >> "%RUNDIR%\SUMMARY.txt"
echo Next: review the draft and holdout before deciding whether to apply. See ECONOMY_CALIBRATION.md. >> "%RUNDIR%\SUMMARY.txt"
type "%RUNDIR%\SUMMARY.txt"
call :stamp "economy-overnight finished OK. Results: %RUNDIR%\SUMMARY.txt"
exit /b 0

rem ======================================================================================================
:session
rem %1 step, %2 name, %3 games, %4 red, %5 blue, %6 abilities, %7 swap, %8 samples, %9 seed offset
if exist "%RUNDIR%\%~2.jsonl" exit /b 0
set /a SESSION_SEED=%SEED%+%~9
call :stamp "STEP %~1  %~2: %~3 games (red %~4 vs blue %~5, abilities=%~6, swap=%~7, samples=%~8)"
"%CON%" economy-sim --boards all --games %~3 --red %4 --blue %5 --abilities %~6 --swap-colors %~7 --sample-values %~8 --opening-plies %OPENING_PLIES% --seed %SESSION_SEED% --parallel 0 --tag %~2 --out "%RUNDIR%\%~2.jsonl" >> "%LOG%" 2>&1
exit /b %errorlevel%

:stamp
echo [%date% %time%] %~1
echo [%date% %time%] %~1 >> "%LOG%"
exit /b 0

:fail
echo.
echo [%date% %time%] ECONOMY PIPELINE FAILED. Last lines of %LOG%:
powershell -NoProfile -Command "Get-Content '%LOG%' -Tail 25"
echo [%date% %time%] ECONOMY PIPELINE FAILED >> "%LOG%"
exit /b 1
