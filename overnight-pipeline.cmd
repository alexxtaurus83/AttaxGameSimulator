@echo off
setlocal EnableExtensions
cd /d "%~dp0"

rem ======================================================================================================
rem  WEEKEND PIPELINE (v3, 30 hour window): train a policy/value model that imitates a DEPTH 4 teacher (normal mode + capture quiescence),
rem  on boards WITH blocked cells (0..6, as in the game). The ONLY question the arenas ask is: does the model beat the shipped engine
rem  (classic depth 3 + quiescence)? If it does, the model is stronger than depth 3 and about 60x faster per move.
rem
rem  Depth appears in exactly one place, the TEACHER (set TEACHER=... to change it). No arena plays depth 1, depth 2 or depth 4.
rem
rem  What is different from the first overnight run (each point was measured, see Readme):
rem   0. Teacher labels break root ties deterministically (see --teacherRandomTies below); the game and the arena opponent break them at random.
rem   1. The teacher is a search in the SHIPPED engine mode (train=false: null-move + quiescence), here at depth 4. The first run imitated depth 3 in
rem      TRAINING mode, which picks a different move than the shipped engine in 38 percent of positions.
rem   2. Every game and every label uses --blocked 0,6. The first run's models never saw a blocked cell.
rem   3. The value head learns the teacher's SEARCH SCORE (tanh(score / 4e6)), not the game outcome (pilot: value R2 0.87 vs 0.14).
rem   4. The expensive teacher labels only a sample of positions (16 per game in gen0, 15 percent of positions later).
rem
rem    gen0  GAMES games played by the cheap engine (depth 3, training mode), 16 kept positions per game, each labelled by the teacher
rem          (policy label + search score)                                                    -> train m0 -> arena vs shipped depth 3
rem    gen1  policy rollouts from m0 (no search), 15 percent of positions labelled by the teacher -> train m1 on gen0+gen1 -> arena
rem    gen2  same from m1                                                                    -> train m2 on gen0..gen2 -> arena
rem    gen3  same from m2                                                                    -> train m3 on gen0..gen3 -> arena
rem  Every model is evaluated as soon as it exists, so an interrupted run still leaves finished, evaluated models.
rem
rem  ESTIMATED TIME on a free 12-core machine with an RTX 2080 Ti. NOTHING BELOW IS A MEASURED END-TO-END TIME. Rates come from pilots at depth 3, scaled to
rem  depth 4 by the cost ratio per teacher label. Two ratios are known and they DISAGREE: 3.3x (node counts of the shipped engine on open boards, 1.27M vs
rem  383k) and 4.9x (a 12-game smoke run on blocked boards: 1,072,643 vs 220,836 nodes per label, only 192 labels, so noisy). Both are shown:
rem                                  ratio 3.3x     ratio 4.9x
rem     gen0 data (20000 games)         6.6 h          9.3 h      (the cheap mover part does not slow down, only the labelling does)
rem     3 relabels (15 percent: 171k labels each, at 169k/h divided by the ratio)
rem                                    10.0 h         14.8 h
rem     3 rollouts                      0.25 h         0.25 h
rem     4 trainings (60 epochs)         1.0 h          1.0 h      (8 + 13 + 17 + 22 min)
rem     4 arenas (200 pairs vs the shipped depth 3, about 3 s per pair)      0.7 h          0.7 h
rem     TOTAL                          18.6 h         26.1 h     The window is 30 h: buffer 11 h at 3.3x, 4 h at 4.9x.
rem  LABEL_FRACTION is 0.15 (not 0.20) for that reason: at 4.9x and 0.20 the total would be about 31 h and miss the window.
rem  If it runs long: set LAST_GEN=2 (stop after m2, saves 3.3-5.0 h) or press Ctrl+C. Every model is evaluated as soon as it exists.rem  Ctrl+C keeps every finished step; re-running continues where it stopped.
rem
rem  RESUMABLE: re-running skips every step whose output file already exists. Files are written under a temporary name and
rem  renamed when complete, so an interrupted step is redone, never half-used.
rem
rem  USAGE:   weekend-pipeline: just run overnight-pipeline.cmd (any folder; do NOT let the PC sleep: Settings > Power; close other heavy programs)
rem  LIVE LOG: opens automatically in a second window (NO_LOGWINDOW=1 disables), or: powershell -c "Get-Content runs\weekend\pipeline.log -Wait -Tail 20"
rem  RESULTS:  runs\weekend\SUMMARY.txt  (also the arena_*.txt files; the number that matters is arena_mN_vs_shipped_d3.txt)
rem
rem  Overrides (set before running, e.g. for a quick smoke test):
rem     set RUNDIR=runs\smoke & set G0_GAMES=24 & set G1_GAMES=24 & set G2_GAMES=24 & set G3_GAMES=24 & set EPOCHS=1 & set PAIRS=2
rem ======================================================================================================

if not defined RUNDIR set "RUNDIR=runs\weekend"
if not defined G0_GAMES set "G0_GAMES=20000"
if not defined G1_GAMES set "G1_GAMES=20000"
if not defined G2_GAMES set "G2_GAMES=20000"
if not defined G3_GAMES set "G3_GAMES=20000"
if not defined EPOCHS set "EPOCHS=60"
rem Arena size in pairs (each pair = 2 games: one opening, colours swapped). The arena is the only evaluation, so 200 pairs (about +/-4.4 points of score).
if not defined PAIRS set "PAIRS=200"
if not defined LABEL_FRACTION set "LABEL_FRACTION=0.15"
if not defined BLOCKED set "BLOCKED=0,6"
rem Last generation to build (0..3). 3 = everything.
if not defined LAST_GEN set "LAST_GEN=3"

set "CON=Attax.Console\bin\Release\net9.0\publish\Attax.Console.exe"
rem The teacher. train=false = the shipped engine mode (null-move pruning on + quiescence). Depth 4 here; override with: set TEACHER=classic:depth=3,train=false
rem Tie-break: the ENGINE default is to play one of several equally scored root moves at random (it is not an engine parameter). The console option
rem --teacherRandomTies defaults to false, so every TEACHER label takes the FIRST of the tied best moves (spec key ties=false: consistent policy labels, and a re-search
rem reproduces them), while the cheap gen0 mover and the arena opponent keep the random tie-break (more varied games; the arena tests the engine as the game plays it).
if not defined TEACHER set "TEACHER=classic:depth=4,train=false"
set "LOG=%RUNDIR%\pipeline.log"
rem Training settings: 128x8 net, cosine lr decay, AMP, batch 1024, 60 epochs (pilot on 145k rows: 40 epochs top-1 0.535, 80 epochs 0.552, still improving).
rem Value target = teacher search score (tanh scale 4e6), value loss weight 1.0 (the score target is learnable, unlike the outcome the first run used).
set "TRAIN=--device cuda --amp --channels 128 --layers 8 --batch-size 1024 --epochs %EPOCHS% --lr 1e-3 --lr-schedule cosine --value-target score --score-scale 4e6 --value-weight 1.0 --val-fraction 0.05 --keep-best --overwrite --gpu-log 10"

if not exist "%RUNDIR%" mkdir "%RUNDIR%"
rem The tool output goes to the log file, so this console only shows STEP lines. Open a second window that follows the log live
rem (close it any time, the pipeline is not affected). Set NO_LOGWINDOW=1 to skip it.
type nul >> "%LOG%"
if not defined NO_LOGWINDOW start "weekend log" powershell -NoProfile -Command "Get-Content -LiteralPath '%LOG%' -Wait -Tail 40"
echo Live log is in a second window, or run: powershell -c "Get-Content %LOG% -Wait -Tail 20"
echo To STOP: press Ctrl+C here and answer Y to "Terminate batch job". Do not press it just because the screen is quiet.
call :stamp "pipeline start: RUNDIR=%RUNDIR% TEACHER=%TEACHER% G0=%G0_GAMES% G1=%G1_GAMES% G2=%G2_GAMES% G3=%G3_GAMES% EPOCHS=%EPOCHS% PAIRS=%PAIRS% LABEL_FRACTION=%LABEL_FRACTION% BLOCKED=%BLOCKED% LAST_GEN=%LAST_GEN%"

rem ---------------------------------------------------------------------------------------------- build
rem Rebuilds whenever this run folder has no marker; set REBUILD=1 to force it.
if defined REBUILD del "%RUNDIR%\.built" 2>nul
if exist "%CON%" if exist "%RUNDIR%\.built" goto :built
call :stamp "STEP 0  build console (publish)"
dotnet publish Attax.Console\Attax.Console.csproj -c Release --nologo >> "%LOG%" 2>&1
if errorlevel 1 goto :fail
echo built > "%RUNDIR%\.built"
:built

rem ---------------------------------------------------------------------------------------------- gen0 data
rem The mover is the CHEAP engine (training mode): it only has to produce realistic positions. The teacher labels 16 positions per game
rem (phase-aware sampling: 20 percent opening, 60 percent midgame, 20 percent late). A mover that was also the teacher would make labels free
rem but costs several times more per game (depth 3 measured 9.5 h per 20000 games against 2.7 h), for fewer, correlated positions.
if exist "%RUNDIR%\gen0.bin" goto :gen0_done
call :stamp "STEP 1  gen0 data: %G0_GAMES% games (depth 3 training-mode mover, blocks %BLOCKED%), 16 positions/game labelled by %TEACHER% (estimate 6.6-9.3 h for 20000)"
"%CON%" selfplay --games %G0_GAMES% --seed 1100 --generation 0 --blocked %BLOCKED% --out "%RUNDIR%\gen0.tmp" --red classic:depth=3,train=true --teacher %TEACHER% --samples 16 --symmetry none --epsilonStart 0.25 --epsilonMid 0.10 --epsilonLate 0.02 --epsilonPly1 10 --epsilonPly2 25 >> "%LOG%" 2>&1
if errorlevel 1 goto :fail
"%CON%" validate-log --path "%RUNDIR%\gen0.tmp" >> "%LOG%" 2>&1
if errorlevel 1 goto :fail
move /y "%RUNDIR%\gen0.tmp" "%RUNDIR%\gen0.bin" >nul
:gen0_done

rem ---------------------------------------------------------------------------------------------- m0
if exist "%RUNDIR%\m0\model.onnx" goto :m0_done
call :stamp "STEP 2  train m0 on gen0 (~8 min)"
python train.py --data "%RUNDIR%\gen0.bin" --out "%RUNDIR%\m0" %TRAIN% >> "%LOG%" 2>&1
if errorlevel 1 goto :fail
:m0_done
call :evaluate m0 "%RUNDIR%\m0\model.onnx"
if errorlevel 1 goto :fail
if "%LAST_GEN%"=="0" goto :summary

rem ---------------------------------------------------------------------------------------------- gen1
rem Each later generation: the previous model plays (policy, no search, a few minutes), the teacher labels a sample of those positions, and the next
rem model is trained from scratch on ALL data so far (no warm start, no path dependence).
rem NOTE: pass genN_labeled.bin, never genN.bin as well. They are the same games and the trainer refuses both.
rem Rows with no label (80 percent of a relabeled log) are dropped by train.py: they train nothing.
if exist "%RUNDIR%\gen1_labeled.bin" goto :gen1_done
if exist "%RUNDIR%\gen1.bin" goto :gen1_rolled
call :stamp "STEP 3  gen1 rollouts: %G1_GAMES% games played by m0 policy (temp 1, topk 4, blocks %BLOCKED%), no search (a few minutes)"
"%CON%" selfplay --games %G1_GAMES% --seed 1200 --generation 1 --blocked %BLOCKED% --out "%RUNDIR%\gen1.tmp" --red "policy:model=%RUNDIR%\m0\model.onnx,temp=1,topk=4" --samples 0 --symmetry none >> "%LOG%" 2>&1
if errorlevel 1 goto :fail
move /y "%RUNDIR%\gen1.tmp" "%RUNDIR%\gen1.bin" >nul
:gen1_rolled
call :stamp "STEP 4  gen1 relabel: %TEACHER% labels %LABEL_FRACTION% of the positions (estimate 3.3-5.0 h at 15 percent)"
"%CON%" relabel --in "%RUNDIR%\gen1.bin" --out "%RUNDIR%\gen1_labeled.tmp" --teacher %TEACHER% --fraction %LABEL_FRACTION% --seed 2 >> "%LOG%" 2>&1
if errorlevel 1 goto :fail
"%CON%" validate-log --path "%RUNDIR%\gen1_labeled.tmp" >> "%LOG%" 2>&1
if errorlevel 1 goto :fail
move /y "%RUNDIR%\gen1_labeled.tmp" "%RUNDIR%\gen1_labeled.bin" >nul
:gen1_done
if exist "%RUNDIR%\m1\model.onnx" goto :m1_done
call :stamp "STEP 5  train m1 on gen0 + gen1_labeled (~13 min)"
python train.py --data "%RUNDIR%\gen0.bin" "%RUNDIR%\gen1_labeled.bin" --out "%RUNDIR%\m1" %TRAIN% >> "%LOG%" 2>&1
if errorlevel 1 goto :fail
:m1_done
call :evaluate m1 "%RUNDIR%\m1\model.onnx"
if errorlevel 1 goto :fail
if "%LAST_GEN%"=="1" goto :summary

rem ---------------------------------------------------------------------------------------------- gen2
if exist "%RUNDIR%\gen2_labeled.bin" goto :gen2_done
if exist "%RUNDIR%\gen2.bin" goto :gen2_rolled
call :stamp "STEP 6  gen2 rollouts: %G2_GAMES% games played by m1 policy (temp 1, topk 4, blocks %BLOCKED%)"
"%CON%" selfplay --games %G2_GAMES% --seed 1300 --generation 2 --blocked %BLOCKED% --out "%RUNDIR%\gen2.tmp" --red "policy:model=%RUNDIR%\m1\model.onnx,temp=1,topk=4" --samples 0 --symmetry none >> "%LOG%" 2>&1
if errorlevel 1 goto :fail
move /y "%RUNDIR%\gen2.tmp" "%RUNDIR%\gen2.bin" >nul
:gen2_rolled
call :stamp "STEP 7  gen2 relabel: %TEACHER% labels %LABEL_FRACTION% of the positions (estimate 3.3-5.0 h at 15 percent)"
"%CON%" relabel --in "%RUNDIR%\gen2.bin" --out "%RUNDIR%\gen2_labeled.tmp" --teacher %TEACHER% --fraction %LABEL_FRACTION% --seed 3 >> "%LOG%" 2>&1
if errorlevel 1 goto :fail
"%CON%" validate-log --path "%RUNDIR%\gen2_labeled.tmp" >> "%LOG%" 2>&1
if errorlevel 1 goto :fail
move /y "%RUNDIR%\gen2_labeled.tmp" "%RUNDIR%\gen2_labeled.bin" >nul
:gen2_done
if exist "%RUNDIR%\m2\model.onnx" goto :m2_done
call :stamp "STEP 8  train m2 on gen0 + gen1_labeled + gen2_labeled (~17 min)"
python train.py --data "%RUNDIR%\gen0.bin" "%RUNDIR%\gen1_labeled.bin" "%RUNDIR%\gen2_labeled.bin" --out "%RUNDIR%\m2" %TRAIN% >> "%LOG%" 2>&1
if errorlevel 1 goto :fail
:m2_done
call :evaluate m2 "%RUNDIR%\m2\model.onnx"
if errorlevel 1 goto :fail
if "%LAST_GEN%"=="2" goto :summary

rem ---------------------------------------------------------------------------------------------- gen3
if exist "%RUNDIR%\gen3_labeled.bin" goto :gen3_done
if exist "%RUNDIR%\gen3.bin" goto :gen3_rolled
call :stamp "STEP 9  gen3 rollouts: %G3_GAMES% games played by m2 policy (temp 1, topk 4, blocks %BLOCKED%)"
"%CON%" selfplay --games %G3_GAMES% --seed 1400 --generation 3 --blocked %BLOCKED% --out "%RUNDIR%\gen3.tmp" --red "policy:model=%RUNDIR%\m2\model.onnx,temp=1,topk=4" --samples 0 --symmetry none >> "%LOG%" 2>&1
if errorlevel 1 goto :fail
move /y "%RUNDIR%\gen3.tmp" "%RUNDIR%\gen3.bin" >nul
:gen3_rolled
call :stamp "STEP 10 gen3 relabel: %TEACHER% labels %LABEL_FRACTION% of the positions (estimate 3.3-5.0 h at 15 percent)"
"%CON%" relabel --in "%RUNDIR%\gen3.bin" --out "%RUNDIR%\gen3_labeled.tmp" --teacher %TEACHER% --fraction %LABEL_FRACTION% --seed 4 >> "%LOG%" 2>&1
if errorlevel 1 goto :fail
"%CON%" validate-log --path "%RUNDIR%\gen3_labeled.tmp" >> "%LOG%" 2>&1
if errorlevel 1 goto :fail
move /y "%RUNDIR%\gen3_labeled.tmp" "%RUNDIR%\gen3_labeled.bin" >nul
:gen3_done
if exist "%RUNDIR%\m3\model.onnx" goto :m3_done
call :stamp "STEP 11 train m3 on gen0 + gen1_labeled + gen2_labeled + gen3_labeled (~22 min)"
python train.py --data "%RUNDIR%\gen0.bin" "%RUNDIR%\gen1_labeled.bin" "%RUNDIR%\gen2_labeled.bin" "%RUNDIR%\gen3_labeled.bin" --out "%RUNDIR%\m3" %TRAIN% >> "%LOG%" 2>&1
if errorlevel 1 goto :fail
:m3_done
call :evaluate m3 "%RUNDIR%\m3\model.onnx"
if errorlevel 1 goto :fail

rem ---------------------------------------------------------------------------------------------- summary
:summary
call :stamp "SUMMARY"
echo RESULTS (score = share of points for the MODEL over paired games against the shipped engine, classic depth 3 + quiescence, blocked cells %BLOCKED%; 95%% CI is over opening pairs. Above 50%% = the model is stronger than depth 3) > "%RUNDIR%\SUMMARY.txt"
for %%f in ("%RUNDIR%\arena_*.txt") do (
  echo. >> "%RUNDIR%\SUMMARY.txt"
  echo %%~nf >> "%RUNDIR%\SUMMARY.txt"
  findstr /c:"P1 wins" /c:"verdict" /c:"P1 (" /c:"P2 (" "%%f" >> "%RUNDIR%\SUMMARY.txt"
)
type "%RUNDIR%\SUMMARY.txt"
call :stamp "pipeline finished OK (teacher %TEACHER%). Models: %RUNDIR%\m0 .. m%LAST_GEN% (model.onnx). Results: %RUNDIR%\SUMMARY.txt"
exit /b 0

rem ======================================================================================================
:evaluate
rem %1 = name, %2 = model. Contract check + latency, then ONE arena: the model against the shipped engine.
call :stamp "EVAL %~1: verify contract + latency, then arena vs shipped depth 3 (%PAIRS% pairs, blocks %BLOCKED%)"
if exist "%RUNDIR%\verify_%~1.txt" goto :eval_arena
"%CON%" verify-model --model "%~2" --threads 1 > "%RUNDIR%\verify_%~1.tmp" 2>&1
if errorlevel 1 ( type "%RUNDIR%\verify_%~1.tmp" >> "%LOG%" & exit /b 1 )
move /y "%RUNDIR%\verify_%~1.tmp" "%RUNDIR%\verify_%~1.txt" >nul
type "%RUNDIR%\verify_%~1.txt" >> "%LOG%"
:eval_arena
rem classic:depth=3 in the arena is the SHIPPED engine (normal mode, quiescence on). The model plays by policy alone (one forward pass per move, no search).
call :arena %~1_vs_shipped_d3 "policy:model=%~2" "classic:depth=3" %PAIRS%
exit /b %errorlevel%

:arena
rem %1 = label, %2 = P1 spec, %3 = P2 spec, %4 = pairs. Same --seed everywhere => same openings for every model.
if exist "%RUNDIR%\arena_%~1.txt" exit /b 0
call :stamp "arena %~1"
"%CON%" arena --p1 "%~2" --p2 "%~3" --pairs %~4 --blocked %BLOCKED% --openingPlies 4 --seed 7 > "%RUNDIR%\arena_%~1.tmp" 2>&1
if errorlevel 1 ( type "%RUNDIR%\arena_%~1.tmp" >> "%LOG%" & exit /b 1 )
move /y "%RUNDIR%\arena_%~1.tmp" "%RUNDIR%\arena_%~1.txt" >nul
type "%RUNDIR%\arena_%~1.txt" >> "%LOG%"
exit /b 0

:stamp
echo [%date% %time%] %~1
echo [%date% %time%] %~1 >> "%LOG%"
exit /b 0

:fail
echo.
echo [%date% %time%] PIPELINE FAILED. Last lines of %LOG%:
powershell -NoProfile -Command "Get-Content '%LOG%' -Tail 25"
echo [%date% %time%] PIPELINE FAILED >> "%LOG%"
exit /b 1
