@echo off
setlocal EnableExtensions

rem ======================================================================================================
rem  ECONOMY CALIBRATION SHORT BATCH (~1.5 hours on 12-core reference CPU, 5,000 games + 80/80 verify).
rem  Delegates to economy-overnight.cmd with smaller session counts weighted toward s01 (income) and
rem  s03/s06 (depth-3, which drives shipped cast rates and rare L1 samples). Produces a DRAFT ONLY
rem  analysis in runs\economy-short-diverse-v2\ (never applies prices; cannot be mixed with v1 logs).
rem
rem  Commit AttaxSimulator and AttaxShared first if you want the logs eligible for --apply, or set
rem  ALLOW_DIRTY_DRAFT=1 for a diagnostic-only trial on uncommitted edits.
rem ======================================================================================================

if not defined RUNDIR set "RUNDIR=runs\economy-short-diverse-v2"
if not defined S01_GAMES set "S01_GAMES=1000"
if not defined S02_GAMES set "S02_GAMES=600"
if not defined S03_GAMES set "S03_GAMES=400"
if not defined S04_GAMES set "S04_GAMES=500"
if not defined S05_GAMES set "S05_GAMES=500"
if not defined S06_GAMES set "S06_GAMES=1000"
if not defined VERIFY_GAMES set "VERIFY_GAMES=80"
if not defined HOLDOUT_GAMES set "HOLDOUT_GAMES=80"

call "%~dp0economy-overnight.cmd"
exit /b %errorlevel%
