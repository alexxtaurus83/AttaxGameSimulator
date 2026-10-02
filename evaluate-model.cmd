@echo off
setlocal EnableExtensions
cd /d "%~dp0"

rem ======================================================================================================
rem  EVALUATE A MODEL (no training, no data written): checks the model file, then plays it against reference opponents.
rem
rem    evaluate-model.cmd <model.onnx> [pairs] [other-model.onnx]
rem
rem    pairs  = opening pairs per arena (default 100). Each opening is played twice with colours swapped, so
rem             100 pairs = 200 games. Fewer pairs = wider confidence interval.
rem    other  = optional second model for a model-vs-model match.
rem    VALUE_PAIRS (environment variable, off by default) = pairs for the slow value-mode depth-2 test.
rem    VALUE_PAIRS (environment variable, default 12) = pairs for the slow value-mode depth-2 test; 0 skips it.
rem
rem  Reading the result: "score" is P1's share of points (win 1, draw 0.5). The 95% CI is computed over opening pairs;
rem  "NOT significant" means the interval contains 50%. classic:depth=3 in the arena is the SHIPPED engine (normal mode + quiescence).
rem  All games use blocked cells 0..6 as in the game (set BLOCKED=0,0 for open boards, or another range).
rem ======================================================================================================

if "%~1"=="" (
  echo usage: evaluate-model.cmd model.onnx [pairs] [other-model.onnx]
  exit /b 2
)
set "MODEL=%~1"
set "PAIRS=%~2"
if "%PAIRS%"=="" set "PAIRS=100"
set "CON=Attax.Console\bin\Release\net9.0\publish\Attax.Console.exe"
if not exist "%CON%" dotnet publish Attax.Console\Attax.Console.csproj -c Release --nologo
if errorlevel 1 exit /b 1

"%CON%" verify-model --model "%MODEL%" --threads 1
if errorlevel 1 exit /b 1

rem classic:depth=N in the arena now runs what the game ships (normal mode + quiescence, about 20x more nodes at depth 3). The references below use quiescence=false so results stay comparable with earlier runs and fast.
rem The last entry, classic:depth=3, is the shipped engine (about 5 s per pair): the number that matters.
if not defined BLOCKED set "BLOCKED=0,6"
for %%o in ("random" "classic:depth=1,quiescence=false" "classic:depth=2,quiescence=false" "classic:depth=3,quiescence=false" "classic:depth=3") do (
  echo.
  echo ===== policy model vs %%~o =====
  "%CON%" arena --p1 "policy:model=%MODEL%" --p2 %%o --pairs %PAIRS% --blocked %BLOCKED% --openingPlies 4 --seed 7 | findstr /c:"P1 wins" /c:"verdict" /c:"by colour" /c:"P1 (" /c:"P2 ("
)
echo.
echo ===== VALUE MODE (the value head as the evaluator inside negamax; a model call per search node) =====
echo --- depth 1 vs classic depth 1 (%PAIRS% pairs, about a minute)
"%CON%" arena --p1 "value:model=%MODEL%,depth=1" --p2 "classic:depth=1,quiescence=false" --pairs %PAIRS% --blocked %BLOCKED% --openingPlies 4 --seed 7 | findstr /c:"P1 wins" /c:"verdict" /c:"P1 (" /c:"P2 ("
rem Depth 2 costs seconds per move (measured 1.4-7.5 s), so it is OFF unless you set VALUE_PAIRS (for example set VALUE_PAIRS=12).
if defined VALUE_PAIRS echo --- depth 2 vs classic depth 2 (%VALUE_PAIRS% pairs, slow)
if defined VALUE_PAIRS "%CON%" arena --p1 "value:model=%MODEL%,depth=2" --p2 "classic:depth=2,quiescence=false" --pairs %VALUE_PAIRS% --blocked %BLOCKED% --openingPlies 4 --seed 7 | findstr /c:"P1 wins" /c:"verdict" /c:"P1 (" /c:"P2 ("
if not "%~3"=="" (
  echo.
  echo ===== model vs other model =====
  "%CON%" arena --p1 "policy:model=%MODEL%" --p2 "policy:model=%~3" --pairs %PAIRS% --blocked %BLOCKED% --openingPlies 4 --seed 7 | findstr /c:"P1 wins" /c:"verdict" /c:"by colour"
)
exit /b 0
