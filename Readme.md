# AttaxSimulator — Ataxx engine, hybrid policy/value training, comparable model modes

A C# Ataxx (7×7) engine with a heuristic negamax AI, a .NET CLI for self-play data generation and arenas, and a PyTorch trainer for one **two-headed (policy + value) network** exported to ONNX.

Three ways to play, all driven by the same player spec and all comparable in one arena:

| Mode | What picks the move | Needs a model |
| --- | --- | --- |
| `classic` | heuristic negamax (the accepted Stage 1 engine) | no |
| `value` | negamax that scores leaves with the model's **value** head | yes |
| `policy` | one forward pass, argmax/sample over **legal** policy logits, no search | yes |
| `random` | uniform random legal move | no |

## Layout

| Project / file | Purpose |
| --- | --- |
| `Attax.Core` | **The game engine, nothing else** (this is what is copied into Unity): bitboards, rules, search, `HeuristicEvaluator`, `EngineParams` and its values in `engine-params.json`. No model, ONNX, JSON-file or logging-format code. |
| `Attax.Model` | Training/evaluation only, never copied into Unity: policy action space (`ActionCodec`), model input encoding (`BoardEncoder`), model contract (`IPolicyValueModel`), value/policy consumers |
| `Attax.Data` | log writer/reader/validator (format v4, class names keep "V3") |
| `Attax.Play` | player specs (`classic:depth=3,params=...`), players, engine-parameter file loading (`EngineParamsFile`), pair statistics and the sequential test |
| `Attax.Eval.OnnxRuntime` | `OnnxPolicyValueModel`: ONNX Runtime adapter that validates the contract at load |
| `Attax.Console` | CLI: `selfplay`, `relabel`, `arena`, `compare-moves`, `params-default`, `params-check`, `validate-log`, `verify-model`, `contract-fixtures`, `validate-hash` |
| `train.py`, `ataxx_common.py`, `ataxx_data.py`, `ataxx_model.py` | trainer, shared contract, log reader, network/losses/ONNX export |
| `Attax.Core.Tests`, `tests_py/` | xUnit and unittest suites |

## Build and test

```bash
dotnet build -c Release Attax.sln
dotnet test Attax.Core.Tests/Attax.Core.Tests.csproj -c Release
python -m unittest discover -s tests_py -t .
```

Python: 3.11+, `torch`, `numpy`, `lz4`, `onnx`, `onnxruntime`. Optional GPU telemetry: `nvidia-ml-py`.

### GPU logging in the trainer (optional, off by default)

`train.py --gpu-log <seconds>` prints GPU utilisation, memory, temperature and power through NVML (`pip install nvidia-ml-py`) every few seconds, plus a summary at the end (average and maximum utilisation, how often the GPU sat idle, peak memory over the baseline already in use, peak temperature and power). It also writes per-epoch GPU stats and PyTorch's own peak memory (`torch_memory`, from free counters) to `report.json`.

Cost, measured on the pilot machine: none visible (training 128x8 for 6 epochs: 15.34 s without vs 15.19 s with `--gpu-log 2`, 3 runs each, within noise). If NVML or the package is missing, the monitor disables itself and the run continues. `overnight-pipeline.cmd` enables it for training (`--gpu-log 10`).

NVML reports whole-GPU memory, which includes the desktop and other programs. Compare `torch_peak_reserved_mib` in `report.json` with the card's total to see how much headroom a larger batch or network has.

There is deliberately no GPU logging or GPU inference in the .NET tools: the .NET side runs ONNX models on the CPU only (see "Inference runs on the CPU" below).
## The pipeline (hybrid training)

```bash
# 1) bootstrap: classic depth-3 rollouts, labelled by a depth-3 teacher (labels are free when the mover IS the teacher)
Attax.Console selfplay --games 1500 --generation 0 --out data/gen0.bin --red classic:depth=3 --teacher classic:depth=3 --samples 40 --symmetry random

# 2) train generation 0
python train.py --data data/gen0.bin --out models/gen0 --epochs 12 --keep-best

# 3) fast rollouts with the model (no search), then review a fraction with the teacher offline
Attax.Console selfplay --games 1500 --generation 1 --out data/gen1.bin --red "policy:model=models/gen0/model.onnx,temp=1,topk=4" --samples 40
Attax.Console relabel --in data/gen1.bin --out data/gen1_labeled.bin --teacher classic:depth=3 --fraction 0.25

# 4) train generation 1 on everything so far (warm start optional)
python train.py --data data/gen0.bin data/gen1_labeled.bin --out models/gen1 --init-from models/gen0/checkpoint.pt --epochs 14 --keep-best

# 5) compare (paired openings, colours swapped)
Attax.Console arena --p1 "policy:model=models/gen1/model.onnx" --p2 "classic:depth=3" --pairs 100
```

Rollout games should use each generation's number in `--generation`; it is part of the game identity.

### Testing a model without training anything (both model modes)

`arena` only plays and prints; it writes no data. A model can play in two different ways, and both are supported and comparable in the same tool:

```bash
# MODE 1  full move prediction: the policy head picks the move directly (no search, ~1 ms/move)
Attax.Console arena --p1 "policy:model=models/gen1/model.onnx" --p2 "classic:depth=3" --pairs 100

# MODE 2  evaluation only: the model just scores positions (value head) and ordinary negamax search picks the move
Attax.Console arena --p1 "value:model=models/gen1/model.onnx,depth=2" --p2 "classic:depth=2" --pairs 30

# same model, both modes, head to head
Attax.Console arena --p1 "policy:model=models/gen1/model.onnx" --p2 "value:model=models/gen1/model.onnx,depth=2" --pairs 30
```

Mode 2 is about 10x slower per move (a model call per search node), so use fewer pairs and compare against classic at the same depth. Its quality is limited by the value head, which is currently trained on noisy game outcomes (see below); expect it to play worse than mode 1 until the value target improves. `evaluate-model.cmd` runs both modes.

## Player specs

`kind[:key=value,…]`, used by `--red`, `--blue`, `--teacher`, `--p1`, `--p2`.

```
classic:depth=3,nodes=3000            heuristic negamax
value:model=m.onnx,depth=2            negamax on the value head
policy:model=m.onnx,temp=1,topk=4     direct policy (no search)
random
```

| Key | Applies to | Meaning |
| --- | --- | --- |
| `model` | value, policy | ONNX path (required) |
| `depth` | classic, value | 1..12, default 3 |
| `nodes` | classic, value | per-move node budget, 0 = unlimited |
| `time` | classic, value | per-move time limit in ms (iterative deepening up to depth+4) |
| `bonus` | classic, value | root-bonus scale. classic default 1 (legacy heuristic bonuses), value default 0 |
| `temp`, `topk` | all but random | sampling. Units: classic = engine units, value = value units (1.0 = a full win/loss swing), policy = logit units |
| `train`, `id` | classic, value | engine training mode; iterative deepening in training mode |
| `quiescence` | classic, value | capture-only leaf extension. Default **on** in the arena (what the game ships), irrelevant in self-play (training mode never uses it) |
| `params` | classic, value | path of an engine-parameter JSON file (see "Engine parameters") |
| `p.<group>.<name>` | classic, value | one parameter override on top of the file, e.g. `p.root.riskPoints=1.2`, `p.eval.centerControlTable.24=4`. Names and syntax are checked when the spec is parsed |

Unknown, duplicated or misplaced keys are errors, never ignored. A teacher must be a search player (classic or value) and always labels greedily.

## Commands

| Command | Purpose |
| --- | --- |
| `selfplay` | write a v4 log (policy labels + search scores). `--red/--blue` any spec, `--teacher`, `--teacherFraction`, `--samples` (0 = all positions), `--symmetry none\|random\|all`, `--epsilon*` (random exploration), `--maxPlies` |
| `relabel` | copy a log adding teacher labels to a random `--fraction` of unlabeled positions (never in place) |
| `arena` | `--pairs N`: each opening is played twice with colours swapped. `--sprt 0,20` stops early with a sequential test, `--pairsCsv` writes one row per pair, a final `RESULT key=value ...` line is machine-readable. Reports score with a 95% CI computed over pairs, per-colour results, ms/move, nodes/move, completed depth, model latency |
| `compare-moves` | same logged positions, two players, no games: how often do they choose a different move (by phase)? 0.00% means a change is inert |
| `params-default` / `params-check` | write a complete copy of the base `engine-params.json` / validate a params file strictly on top of it and list what it changes |
| `validate-log` | full structural and semantic validation (legality of every action, teacher flags, CRCs) |
| `verify-model` | contract check, output sanity, batch-vs-single equality, latency at batch 1/8/32 |
| `contract-fixtures` | emits C#-computed planes / legal sets / symmetry maps / model outputs for `tests_py/test_parity.py` |
| `validate-hash` | Zobrist consistency |

For equal-wall-clock comparisons use `--parallel 1` with `time=` on both players; with parallel games CPU contention makes the reached depth noisy (the arena warns).

## Evaluating a model (no training, no data written)

`arena` only plays games and prints results; it never trains and never writes a log. Use it any time to test a model file, before or after training, from any machine that has the `.onnx`:

```bash
# one command: contract check + latency, then the model against random, classic depth 1/2/3, the teacher, and optionally another model
evaluate-model.cmd models\gen1\model.onnx 100 models\gen0\model.onnx

# or by hand
Attax.Console verify-model --model models/gen1/model.onnx
Attax.Console arena --p1 "policy:model=models/gen1/model.onnx" --p2 "classic:depth=3" --pairs 100
Attax.Console arena --p1 "policy:model=models/gen1/model.onnx" --p2 "policy:model=models/gen0/model.onnx" --pairs 100
```

The two ways a model can play are tested separately, because they are different players:

| Model mode | Spec | Strength comes from |
| --- | --- | --- |
| direct policy | `policy:model=m.onnx` | the policy head alone, one forward pass per move |
| value search | `value:model=m.onnx,depth=2` | negamax using the value head at the leaves; slow (a model call per node) |

How to read an arena: `score` is P1's share of points (win 1, draw 0.5) over all games. The 95% interval is computed over opening pairs, and "NOT significant" means it contains 50%. `classic:depth=3` is the normal game engine; `classic:depth=3,train=true` is the engine that produced the training labels (the teacher). Fixed `--seed` means identical openings, so two models evaluated with the same command are directly comparable. For a fair equal-time comparison use `time=` on both players with `--parallel 1`.

## Weekend run (30 hours): a depth 4 teacher, one question

`overnight-pipeline.cmd` (the file name is kept) trains a policy/value model that imitates a **depth 4** teacher (shipped engine mode: null-move + quiescence) on boards **with blocked cells 0..6**. Output goes to `runs/weekend/` (git-ignored): `pipeline.log`, `SUMMARY.txt`, `arena_mN_vs_shipped_d3.txt`, models `m0` to `m3`. Every step is skipped if its output exists, so it can be stopped and re-run.

**The one question the arenas ask:** does the model beat the shipped engine (classic depth 3 + quiescence)? If it does, it is stronger than depth 3 and about 60x faster per move. Depth appears only in the teacher (`set TEACHER=...` to change it); **no arena plays depth 1, 2 or 4**, and each model is evaluated against depth 3 right after it is trained, so an interrupted run still leaves evaluated models.

| Step | What | Estimate at cost ratio 3.3x | at 4.9x |
| --- | --- | --- | --- |
| gen0 | 20,000 games by the cheap engine (depth 3, training mode); 16 positions per game labelled by the depth 4 teacher (policy label + search score) | 6.6 h | 9.3 h |
| gen1, gen2, gen3 | policy rollouts from the previous model (about 5 min), **15%** of positions labelled by the depth 4 teacher | 3.3 h each | 5.0 h each |
| train m0..m3 | 128x8, 60 epochs, value target = teacher score, from scratch on all data so far | 1.0 h in total | 1.0 h |
| arenas | 200 pairs per model vs the shipped depth 3, blocks 0..6 | 0.7 h in total | 0.7 h |
| **Total** | | **18.6 h** | **26.1 h** |

**Not measured end to end.** The two ratios are the cost of a depth 4 label relative to a depth 3 label, and they disagree: 3.3x from node counts of the shipped engine on open boards (1.27M against 383k nodes per move), 4.9x from a 12-game smoke run on blocked boards (1,072,643 against 220,836 nodes per label, only 192 labels, so noisy). The window is 30 hours, which leaves 11 h of buffer at 3.3x and 4 h at 4.9x. `LABEL_FRACTION` is 0.15 (not 0.20) because at 4.9x and 0.20 the total would be about 31 h. If it runs long, `set LAST_GEN=2` stops after m2 (saves 3.3-5.0 h), or Ctrl+C keeps all finished steps. The script was run end to end only at tiny scale (12-16 games, 1 epoch, 2 pairs) to prove the plumbing, with the depth 4 teacher.

What a good outcome looks like is not known in advance. The earlier depth 3 pilot (145k labels) reached 21% against the shipped engine; a depth 4 teacher is stronger than the engine the model is measured against, but imitation can only approach its teacher. Reading the result: above 50% (95% interval above 50%) means the model beats depth 3.

Earlier run (for comparison): `runs/overnight/`, depth 3 training-mode teacher, no blocks: 14% against the shipped engine on open boards and 4% with blocks.

## Model contract `attax-pv-1`

- Input `board`: float32 `[N,4,7,7]`; channels friendly (side to move), enemy, blocked, constant 1. Always the side-to-move perspective.
- Output `policy_logits`: float32 `[N,833]`, **unmasked**. Output `value`: float32 `[N,1]` in [-1,1], a value for the side to move (trained on the teacher search score through tanh, see "Training"; in -1..1).
- Action index = `kind*49 + to_square` (`square = y*7+x`). Kind 0 is a clone to `to_square` (all clone sources give the same board, so they are one action). Kinds 1..16 are jumps whose source is `to_square + offset` for the 16 distance-2 offsets in row-major order; a source off the board is never legal. There are no pass actions: a position where the side to move has no move is terminal and never a sample.
- The ONNX file carries a metadata stamp. `OnnxPolicyValueModel` refuses, at load time, anything that does not match exactly (names, dtypes, shapes, contract id, action count), including old value-only models. Any inference problem throws; a broken model stops the run instead of producing data.
- Illegal logits are masked before any selection (`PolicyMoveSelector`, and inside the training loss).

`ModelValueEvaluator` exposes only the value head to the search, and `PolicyMoveSelector` is the only consumer of the policy head, so the two outputs cannot be swapped.

## Log format v4 (`ATLG` + `ushort 4`)

Records: `1` GameHeader `[gameUid:8][seed:8][board:1][generation:2][redMode:1][blueMode:1]`; `2` SampleBlock `[count][rawSize][compSize][crc32]` + LZ4; `3` GameResult `[gameUid:8][resultRedPov:1][plies:2][termination:1]`; `4` TeacherConfig `[id][kind][depth][nodes][rootBonusScale][flags][modelHash:8]`.

Sample (46 bytes): `gameUid, ply, red, blue, blocked, side, playedAction, teacherAction, flags, symmetry, teacherId, teacherScore`. The position is the state **before** the move. **Version 4 added `teacherScore`; v3 logs are rejected** (regenerate them: the data is cheap compared with training).

- `playedAction` is what the rollout did (possibly an exploration move, flagged `PlayedRandom`). It is never a policy target.
- `teacherAction` is an independent label, trusted only with the `TeacherValid` flag, and is tied to a declared teacher configuration (depth, budget, bonus scale, **engine mode**, parameter or model hash). The teacher record's `flags` say whether the teacher ran in normal mode (bit 0) and with quiescence (bit 1); 0 = training mode.
- `teacherScore` (engine units: heuristic points x 10,000; flag `TeacherScoreValid`, requires `TeacherValid`) is the teacher search's **value of the position for the side to move**, without the root bonus. A test checks it equals the exact brute-force minimax value of the evaluator at the searched depth. Ordinary positions are within about +/-1e7 (median 1.4e6, 99th percentile 6.6e6, measured); decisive terminal results are about +/-1e9. It is a property of the position, so every symmetry copy carries the same number.
- The game **outcome** belongs to the **played trajectory** only. An unplayed teacher continuation never receives it.
- `termination`: `Terminal` (real result) or `PlyCap`. Ply-cap outcomes are a piece-count fallback and are excluded from outcome-based value training unless `--include-plycap`.
- `gameUid` is the stable identity across files, generations, relabeled copies and symmetry copies. Train/validation is split by a CRC of the uid, so all copies of a game land on the same side. Colliding uids with different seed/generation are rejected.

## Training (`train.py`)

One shared conv trunk (no BatchNorm), a spatial policy head (17×7×7) and a value head.

- Loss = `policyWeight · CE(masked logits, teacher action)` on rows with a valid teacher label + `valueWeight · MSE(value, target)` on rows with a valid value target. Each term uses only its own valid rows, and **rows with neither are dropped before training** (a relabeled log is mostly such rows; `--keep-unlabeled` keeps them).
- **Value target** (`--value-target`): `score` (default) = `tanh(teacherScore / --score-scale)`, scale 4e6, so ordinary positions stay in the steep part of tanh and decisive wins/losses saturate to +/-1; `outcome` = the played game's result; `mix` = `--value-mix · score + (1 - --value-mix) · outcome`. A row without the needed field gets no value target; nothing falls back silently to another target. The report has `value_r2` (share of target variance explained; 1 = perfect, 0 = no better than the mean) and `value_sign_acc_ordinary`.
- **Measured on a pilot** (145k blocked-board positions labelled by the shipped engine, same data, 64x5, 25 epochs): value R2 **0.87** and sign accuracy **0.94** with the score target, against R2 **0.14** and sign accuracy **0.64** with the outcome target and R2 0.37 with mix. As an evaluator inside negamax at depth 1 against classic depth 1 the score-trained head scored **72%** (40 pairs, 64-81%), the outcome-trained head **48%**. The same 128x8 net at 40 epochs: policy top-1 0.535, value R2 0.88; at 80 epochs 0.552 and 0.89 (still improving).
- Random 8-fold symmetry augmentation on device (boards and actions transformed consistently); `--no-augment` disables it.
- `--lr-schedule cosine` decays the learning rate over the run (measured slightly better and more stable than constant); `--value-weight` scales the value loss (the value target is the noisy game outcome, and a lower weight kept policy accuracy higher in the pilot).
- Resumable checkpoints (`--resume`), warm start (`--init-from`), `--keep-best`, refusing a non-empty `--out` unless told otherwise, non-finite loss aborts.
- Exports `model.onnx` with the contract stamp, validates it against torch (names, shapes, finiteness, value range, numerical parity, batch independence) and writes `report.json` (data stats, per-epoch metrics, timings, artifact sha256).

## Game result rule

A player with no legal move (including a colour with no pieces) loses immediately, regardless of piece counts. If neither player can move, the side with more pieces wins and equal counts draw. There are no passes. A game that reaches the ply cap has no terminal result and falls back to piece counts (recorded as `PlyCap`). Search, self-play, the arena and the trainer all use `AtaxxAIEngine.GetTerminalResult`.

## Node budget

`nodes=` is a hard per-move limit shared by all iterations. If it runs out before any iteration finishes, the engine plays a cheap unlimited depth-1 pass (`UsedFallback`, reported per player). In training mode without `id=true` the engine jumps straight to the target depth, so a budget below the cost of that depth means **most moves fall back to depth 1** (measured: `classic:depth=3,nodes=3000` fell back on 75% of moves). Use `id=true`, or no budget, if you want real depth-3 play.

## Engine parameters (`engine-params.json`) and Unity

**All tunable numbers live in one file, `Attax.Core/engine-params.json`. There are no default values in code.** The file is deserialised into `Attax.Core.EngineParams` (three groups) and handed to the engine:

| Group | Contents | Affects |
| --- | --- | --- |
| `eval` | material, mobility, potential mobility, centre control, corner, edge, stability weights, phase thresholds, `centerControlTable` (49 values) | every search node |
| `root` | clone / flip / risk / positional / non-capturing-jump root bonuses, phase limits, the comeback aggression table, `positionalTable` (49 values) | the choice among root moves only |
| `search` | null-move and quiescence gates (they used to be hard-coded in `AlphaBeta`) | how much of the tree is searched (nodes and time) |

`AttaxConstants` keeps only the board size and killer-table depth. The constants nothing ever read were deleted.

### Using it from the console: three layers, applied in this order

1. **Base file** `engine-params.json`: complete, every value. The console build copies it next to the exe from `Attax.Core/` (set the `ATTAX_ENGINE_PARAMS` environment variable to use another file).
2. **`params=file.json`** in a player spec: a file that lists **only the values it changes**. A table (array) that appears in it replaces the whole table.
3. **`p.<group>.<name>=value`** overrides in the same spec, e.g. `p.root.riskPoints=1.2` or `p.eval.centerControlTable.24=4`.

```bash
Attax.Console params-default --out params/mine.json            # complete copy of the base file to edit (delete what you do not change)
Attax.Console params-check --file params/mine.json             # strict check on top of the base file + the list of values it changes
Attax.Console arena --p1 "classic:depth=3,params=params/mine.json" --p2 "classic:depth=3" --pairs 200 --blocked 0,6
Attax.Console arena --p1 "classic:depth=3,p.eval.centerControlWeight=3" --p2 "classic:depth=3" --sprt 0,20 --pairs 600 --blocked 0,6
Attax.Console compare-moves --playerA "classic:depth=3,p.eval.cornerWeight=8" --playerB "classic:depth=3" --positions data/gen0.bin --count 1000
```

Loading is strict: an unknown name, a duplicate key, a wrong type or an out-of-range value is an error naming the file, the property and the line, and it fails before any game is played. The arena prints each player's effective engine mode, quiescence setting and a parameter fingerprint, then the values that differ between the two. Every engine, including the rules-only referee, is built from parameters, so even `random` vs `random` needs the base file (the policy and random *players* themselves have no parameters of their own).

### Using it from Unity (breaking change: Unity must now pass the parameters)

Core does no file handling. Deserialise the **complete** file and pass it to the engine constructor. **The parameters are mandatory: there is no engine without them.**

```csharp
// Newtonsoft.Json is what was tested (UnityEngine.JsonUtility was not).
EngineParams p = JsonConvert.DeserializeObject<EngineParams>(jsonText);      // jsonText = engine-params.json from a TextAsset / Resources / StreamingAssets
var errors = p.Validate();                                                   // empty list = OK; a partial file or a typo'd value is reported here (the constructor checks too)

// Heuristic engine: no evaluator argument, the engine builds it from the same parameters.
var engine = new AtaxxAIEngine(p, new AtaxxAIEngine.AIEngineConfig { AiDepth = 3 }, null, logger, coordinator);

// Model (Sentis) evaluator at every leaf: pass it as the third argument. It replaces the heuristic; the root and search groups still apply.
var modelEngine = new AtaxxAIEngine(p, new AtaxxAIEngine.AIEngineConfig { AiDepth = 2 }, sentisEvaluator, logger, coordinator);
```

New constructor: `AtaxxAIEngine(EngineParams engineParams, AIEngineConfig config = default, IValueEvaluator evaluator = null, ILogSink logger = null, AILogCoordinator coordinator = null)`.

- `engineParams` is validated and copied at construction (`ArgumentNullException` for null, `ArgumentException` listing every problem otherwise), so there are no checks left inside the search, and editing the object afterwards changes nothing. Two engines with different parameters in one process do not interfere. A blank `new EngineParams()` is all zeros and is rejected, so a forgotten field can never silently become 0.
- `evaluator = null` means the heuristic evaluator, built by the engine from `engineParams`. Passing a `HeuristicEvaluator` object is rejected (it could disagree with the parameters).
- A referee that only needs legal moves, hashing and results is built the same way: `new AtaxxAIEngine(p)`.
- **`AIEngineConfig.Params` and `AIEngineConfig.UseMLRootOnly` were removed.** `UseMLRootOnly` ordered root moves with the model and searched with the heuristic: measured, it was slower than plain search and chose the same move 95-98% of the time, and it was never stronger. `UseMLEvaluation` is still a config field that Unity sets; the engine ignores it (the evaluator is whatever is passed).
- Every Unity site that creates an engine changes (about 15, none updated), as does the one that reads `attax.config.json` (replace with the constant `"value"`).
- Different parameter sets (for example for different play styles) are just different `EngineParams` objects.

A relabel/self-play teacher records the hash of its parameters in the log's teacher record (`modelHash`), so a log says which parameter set produced its labels. See `HEURISTIC_TUNING_PLAN.md` for how to tune.

### Tie-break at the root (engine default, not a parameter)

When several root moves share the best search score, the engine plays **one of them at random** instead of always the first generated one. All tied moves are equally good for the search, so this only changes **variety**, not strength, and it makes the game less predictable (the same start no longer always gives the same game).

- **It is the engine's constructor default, not an entry in `engine-params.json`.** `AIEngineConfig.DisableRandomRootTies` is `false` by default (random ties on); set it to `true` for a deterministic engine (tests, A/B runs).
- **Reproducible:** the draw uses its own random stream, seeded from `AIEngineConfig.Seed` (same seed, same game) and **separate from the main stream**, so it never shifts blocked-cell placement or temperature sampling. With ties disabled the engine does no tie counting and draws nothing.
- **Exact ties only:** moves tied on the final score (search value plus root bonus) are the candidates; a clear best move is always played, and a move is never chosen over a better one. Tests check all of this.
- **Scores are unchanged:** the setting changes which of the tied moves is *played*, not any score, so the teacher's value labels are the same either way.
- **Console / specs:** the player spec key `ties=false` (default `true`) turns it off for that player, e.g. `classic:depth=3,ties=false`. It is part of the spec's search key and the arena header prints `root ties=random|first`.

**Training data.** The console option `--teacherRandomTies` (`selfplay` and `relabel`, default `false`) controls the **teacher** only:
- **Default (false): the teacher takes the first of the tied best moves** (it adds `ties=false` to the teacher spec unless the spec already says `ties=...`). This is a consistent rule for the policy head to learn (random picks give the model conflicting labels for the same kind of position), and a fresh search reproduces the label. The teacher record in the log has a flag bit for it (bit 2 of the teacher flags: 1 = random ties), so a log tells the two kinds of label apart.
- **The mover** (the engine that plays the games in self-play) keeps the engine default (random ties), which makes the games more varied.
- A mover's own move is reused as a free teacher label only if mover and teacher have the same search settings including the tie rule. With the default teacher (first tied move) and a mover on random ties the specs differ, so labels are searched fresh (a little slower for same-depth teachers); `ties=false` on the mover restores the reuse.
- **Arena:** both players use the engine default (random ties), i.e. the engine as the game plays it. Same seed gives the same games. Use `ties=false` for a deterministic opponent.
- **Expected effect on model quality:** none on the value head; for the policy it is neutral to slightly positive for a fixed teacher rule against random tie labels. This is reasoning from how the labels are made, not a measurement.

For the C# tests, goldens and "same position, same move" tests set `DisableRandomRootTies = true` on their engine config (helper `Prm.Det`); the tie-break tests cover the random behaviour.

### Blocked cells

`selfplay` and `arena` accept `--blocked N` or `--blocked min,max` (the game uses 0 to 6, so `0,6` matches it). Blocks are placed exactly as the game places them (`GenerateRandomBlockedCellPositions`), both games of an arena pair get the same blocks, and `selfplay` stores the blocked squares in the log, so the trainer sees them (the board encoding already has a blocked plane). The default is `0,0` (none), which leaves every existing seed producing the same games as before. The `overnight-pipeline.cmd` run did **not** use blocks: its models never saw a blocked cell.

### Arena engine settings (changed)

`classic:` players in the **arena** now run what the game ships by default: normal engine mode (null-move pruning on) with the capture-only quiescence extension on at depth 3 and above. Earlier arena runs used the same normal mode but with quiescence forced off. Use `quiescence=false` to reproduce the old arena behaviour. Self-play uses training mode as before, where quiescence is never used, so generated data is unchanged. Search results at depth 3 and below with quiescence off are bit-identical to before.

## Classic behaviour is pinned

`ClassicRegressionTests` (Stage 1) and `RefactorGoldenTests` (recorded on the engine before the `EngineParams` refactor) hold digests of fixed-seed games: moves, node counts and every root move's strategic/bonus/final score, in training and normal mode, with and without quiescence, at depths 2 to 4, plus raw evaluator output on 8,000 positions. Default `EngineParams` must reproduce all of them bit for bit; `EngineParamsTests` additionally checks the precomputed weight tables against the original formulas for every piece count, and that every single parameter has an observable effect.
## Gotchas

- The Console, ONNX and test projects target `net9.0`.
- `Attax.Core` must stay free of model, ONNX and file/format code: it is copied into Unity. `Attax.Model` holds everything that only training needs; a test-time grep for `Attax.Model|ActionCodec|OnnxRuntime` inside `Attax.Core` should find nothing.
- `data/` and `models/` (generated logs, checkpoints, ONNX) and all `bin/` / `obj/` folders are git-ignored.
- Every `selfplay` run needs its own `--seed` (or `--generation`): the same pair recreates the same games, and the trainer refuses to load two files that contain the same game.
- Old value-only `.onnx` files and v1/v2 logs are rejected by design.
- Do not run multiple writers to the same `--out`; `LogV3Writer` assumes a single writer (it is thread-safe within one process).
- Run-to-run determinism: self-play is seeded per game (`--seed`, index); ONNX CPU inference is deterministic for a fixed model, but the Python trainer's GPU kernels are not bit-reproducible.

## Inference runs on the CPU

The .NET tools (`selfplay`, `relabel`, `arena`, `verify-model`) run ONNX models on the **CPU only**. There is no `--ort` option, no CUDA provider and no GPU package (`Microsoft.ML.OnnxRuntime`, not `.Gpu`). This was decided from measurements on an RTX 2080 Ti + 12-core CPU, not assumed. Training (`train.py`) does use the GPU, because there the batches are large.

### What was measured

Both columns run the same model on the same positions. CPU inference is pinned to one thread per call (each game thread already owns a core).

**One position per call (what self-play and the arena actually do):**

| Model | CPU | GPU (CUDA) | |
| --- | --- | --- | --- |
| 64x5 (the `gen1` model), `verify-model` | 0.35 ms | 1.44 ms | GPU 4x slower |
| 128x8 (overnight model), `verify-model` | 1.47 ms | 2.11 ms | GPU 1.4x slower |

**Real workloads, 12 games in parallel:**

| Workload | CPU | GPU (CUDA) | |
| --- | --- | --- | --- |
| 1,500 policy self-play games, 64x5 (69,734 model calls) | 3.6 s | 62.3 s | GPU 17x slower |
| 1,500 policy self-play games, 128x8 (81,833 model calls) | 20.5 s | 137.5 s | GPU 6.7x slower |
| Arena, policy vs the same policy, 200 games | 0.8 s | 10.9 s | GPU 14x slower |
| Arena, policy vs `classic:depth=3`, 200 games | 33.8 s | 41.6 s | GPU 1.2x slower |

Time per model call under that load, 64x5: 0.54 ms on CPU vs 10.5 ms on GPU (the same call costs 1.44 ms when it is the only one running). The last row is nearly even only because the classic opponent's search (about 60 ms/move) dominates and the policy player's model calls are a tiny share of the total.

### Why the GPU loses here

1. **A GPU call has a large fixed cost that does not shrink with the input.** Time per call on the GPU is almost flat from 1 to 32 positions (64x5: 1.44 ms at batch 1, 1.40 ms at batch 8, 1.76 ms at batch 32), so about 1.4 ms is overhead (copying the input over, launching the kernels, waiting, copying the result back), not useful work. This board is tiny (7x7 with 4 channels) and the networks are small (about 7 million multiply-adds for 64x5), so a single CPU core finishes the actual computation in 0.35 ms, less than the GPU's overhead alone.
2. **Every call carries exactly one position.** In self-play and the arena each game thread asks for one move at a time, so nothing is batched. The GPU's strength is doing many positions in one launch, and it is never given that chance.
3. **Twelve game threads share one GPU.** Their single-position calls queue behind each other, which fits the measurements: 10.5 ms per call under load against 1.44 ms alone, and 730 s of summed thread time spent in model calls for a 62 s wall-clock run (threads mostly waiting). The CPU has no such bottleneck, since every game thread brings its own core. I did not profile where the queueing happens (single CUDA stream, session lock, or driver), so this is the explanation that fits the numbers, not a proven cause.
4. **The CPU scales with the work, the GPU does not.** CPU time per call grows roughly linearly with batch size (64x5: 0.35 / 1.85 / 6.6 ms at batch 1 / 8 / 32), the GPU's stays flat. That is why the GPU only wins at larger batches.

### When the GPU would win

Per-call time by batch size (`verify-model`, one thread on CPU):

| Batch | 64x5 CPU | 64x5 GPU | 128x8 CPU | 128x8 GPU |
| --- | --- | --- | --- | --- |
| 1 | 0.35 ms | 1.44 ms | 1.47 ms | 2.11 ms |
| 8 | 1.85 ms | 1.40 ms | 10.87 ms | 2.03 ms |
| 32 | 6.59 ms | 1.76 ms | 40.98 ms | 2.21 ms |

Per position at batch 32 the GPU is about 3.7x cheaper for 64x5 and about 18x cheaper for 128x8. The CPU is faster at batch 1 and the GPU is faster at batch 8 for both nets, so the crossover lies somewhere between 1 and 8 (only batches 1, 8 and 32 were measured, so I cannot say where exactly). Two situations create batches that large:

- **A much bigger network**, where one CPU core becomes the bottleneck.
- **Batching positions across many games**, a scheduler that gathers one pending position from each of many concurrent games and runs a single forward pass. That works naturally for policy mode (one call per move). It does not work for value-mode search, which needs a score at every leaf of a recursive search and would have to be restructured. To fill a batch of 32 you also need well over 12 concurrent games, since there are only 12 game threads.

Neither exists today, and neither is needed: policy self-play already takes seconds for 1,500 games and the expensive steps (the teacher relabel, classic search) are CPU search that a GPU cannot speed up. If the CPU ever becomes the bottleneck, the order of work is: build the cross-game batching scheduler in `Attax.Play`, then re-add `Microsoft.ML.OnnxRuntime.Gpu` and a provider option. Switching the provider alone would reproduce the slowdowns above.

### Caveats

- The GPU runs used default ONNX Runtime CUDA settings. Things that can reduce per-call overhead (IO binding, CUDA graphs, the TensorRT provider, one session per thread) were not tried, so a tuned GPU setup might beat the numbers above for single positions. It would still not fix point 2 (no batching) and I have no evidence it would beat a 0.35 ms CPU call.
- The GPU numbers were measured before the GPU option was removed from the CLI, so they cannot be reproduced with the current `Attax.Console`. CPU latency at batch 1, 8 and 32 is still printed by `verify-model`.
- Measured on one machine (RTX 2080 Ti, 12 cores, driver 617). A different GPU/CPU ratio, a laptop, or a much larger model shifts the crossover.
