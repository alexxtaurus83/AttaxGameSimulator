# AttaxSimulator — Ataxx engine, self-play log generation, and value-net training

This repository contains a **C# Ataxx (7×7) game engine + search AI**, a **.NET CLI tool** for generating/validating self-play training logs and running model arenas, and a **Python training script** that learns a value network and exports it to **ONNX** for inference via ONNX Runtime.

## Repository layout (what each piece is for)

| Component | What it is | Where |
| --- | --- | --- |
| Core engine | Bitboard state, move generation, search, hashing, evaluation interfaces, shared config validation | [`Attax.Core`](Attax.Core/AttaxConstants.cs:1) (see [`Attax.Core/AtaxxAIEngine.cs`](Attax.Core/AtaxxAIEngine.cs:1), [`Attax.Core/AttaxConfig.cs`](Attax.Core/AttaxConfig.cs:1)) |
| CLI tools | Generates self-play logs, runs arena matches, performs sanity/validation commands | [`Attax.Console`](Attax.Console/Program.cs:1) |
| ONNX inference adapter | Evaluator implementation backed by ONNX Runtime (CPU by default; optional CUDA provider) | [`Attax.Eval.OnnxRuntime`](Attax.Eval.OnnxRuntime/OnnxValueEvaluator.cs:1) |
| Python training | Streams `.bin` logs, trains a PyTorch value net, exports `ataxx_value.onnx` | [`train.py`](train.py:1) |
| Shared config (validated) | Declares board/model/log format constants used across tools | [`attax.config.json`](attax.config.json:1) |

## Quickstart

### Prerequisites

- **.NET 8 SDK** (the console app targets `net8.0`; see [`Attax.Console/Attax.Console.csproj`](Attax.Console/Attax.Console.csproj:13)).
- **Python 3.11+** recommended.
- Python packages used by the trainer:
  - Required: `torch`, `numpy`, `lz4`
  - Optional (only for `--check-onnx`): `onnx`, `onnxruntime`

### 1) Build the .NET solution

```bash
dotnet build -c Release Attax.sln
```

### 2) Generate self-play training logs (`.bin`)

Using `dotnet run`:

```bash
dotnet run -c Release --project Attax.Console -- selfplayloggen --games 200 --seed 0 --out logs.bin
```

Using a built executable (Windows example; path depends on configuration):

```bash
.\Attax.Console\bin\Release\net8.0\Attax.Console.exe selfplayloggen --games 200 --seed 0 --out logs.bin
```

Optional: validate the file structure after generation:

```bash
dotnet run -c Release --project Attax.Console -- validate-log --path logs.bin --strict true
```

### 3) Train and export an ONNX model

By default, the trainer scans the current directory for `*.bin` and exports `ataxx_value.onnx` (see [`MODEL_PATH`](train.py:24)).

```bash
py -3.11 -m pip install torch numpy lz4
py -3.11 -m train --device auto --data .
```

Optional: verify the exported ONNX with ONNX Runtime (requires additional packages):

```bash
py -3.11 -m pip install onnx onnxruntime
py -3.11 -m train --device cpu --data . --check-onnx
```

### 4) Run a model arena (heuristic vs ONNX)

```bash
dotnet run -c Release --project Attax.Console -- modelarena --model1 ataxx_value.onnx --model2 heuristic --games 100 --ort cpu
```

## CLI overview

The CLI entry point is [`Attax.Console/Program.cs`](Attax.Console/Program.cs:1). It supports these verbs:

- `selfplayloggen` — generate compressed binary self-play logs
- `modelarena` — run head-to-head matches between evaluators (heuristic or ONNX)
- `mirror-test` — deterministic “mirror sanity” evaluator check
- `validate-log` — validate the structure and checksums of a training log
- `validate-hash` — verify incremental Zobrist hashing vs recompute

Tip: You can always run `--help` to see the auto-generated help text from CommandLineParser.

---

## CLI reference

Notes:

- Option parsing is **case-insensitive** (see parser setup in [`Main()`](Attax.Console/Program.cs:15)).
- Enum values are case-insensitive (same parser setup).
- The legacy alias `--mirrortest` is normalized to `mirror-test` (see [`NormalizeArgs()`](Attax.Console/Program.cs:66)).

### `selfplayloggen` — generate self-play training logs

Definition: [`SelfPlayLogGenOptions`](Attax.Console/SelfPlayLogGenOptions.cs:4) → execution in [`RunSelfPlayLogGen()`](Attax.Console/Program.cs:89).

#### Synopsis

```text
Attax.Console selfplayloggen [options]
```

#### Options

| Flag | Type | Default | Meaning |
| --- | --- | --- | --- |
| `--games` | `int` | `100` | Number of self-play games to generate ([`Games`](Attax.Console/SelfPlayLogGenOptions.cs:6)). |
| `--seed` | `int` | `0` | Random seed for the overall run ([`Seed`](Attax.Console/SelfPlayLogGenOptions.cs:9)). |
| `--out` | `string` | `training_data.bin` | Output `.bin` path ([`Out`](Attax.Console/SelfPlayLogGenOptions.cs:12)). |
| `--nodeBudget` | `int` | `1000` | Search node budget per move ([`NodeBudget`](Attax.Console/SelfPlayLogGenOptions.cs:15)). |
| `--topK` | `int` | `1` | Top-K sampling for move selection ([`TopK`](Attax.Console/SelfPlayLogGenOptions.cs:18)). |
| `--temp` | `double` | `1.0` | Sampling temperature ([`Temp`](Attax.Console/SelfPlayLogGenOptions.cs:21)). |
| `--samples` | `int` | `20` | Target samples per game (0 logs only game headers/results) ([`Samples`](Attax.Console/SelfPlayLogGenOptions.cs:24)). |
| `--aiDepth` | `int` | `3` | Search depth for self-play ([`AiDepth`](Attax.Console/SelfPlayLogGenOptions.cs:30)). |
| `--useOrthogonalOnlyCapture` | `bool` | `false` | Use the orthogonal-only capture rule variant ([`UseOrthogonalOnlyCapture`](Attax.Console/SelfPlayLogGenOptions.cs:33)). |
| `--useMLRootOnly` | `bool` | `false` | Use the ML evaluator on the root only ([`UseMLRootOnly`](Attax.Console/SelfPlayLogGenOptions.cs:39)). |
| `--disableQuiescence` | `bool` | `true` | Disable quiescence search ([`DisableQuiescence`](Attax.Console/SelfPlayLogGenOptions.cs:42)). |
| `--logGenMode` | `bool` | `true` | Enable “log generation mode” on the engine config ([`LogGenMode`](Attax.Console/SelfPlayLogGenOptions.cs:45)). |
| `--epsilonStart` | `double` | `0.25` | Opening epsilon (random-move probability) ([`EpsilonStart`](Attax.Console/SelfPlayLogGenOptions.cs:48)). |
| `--epsilonMid` | `double` | `0.10` | Midgame epsilon ([`EpsilonMid`](Attax.Console/SelfPlayLogGenOptions.cs:51)). |
| `--epsilonLate` | `double` | `0.02` | Endgame epsilon ([`EpsilonLate`](Attax.Console/SelfPlayLogGenOptions.cs:54)). |
| `--epsilonPly1` | `int` | `10` | First epsilon schedule pivot ply ([`EpsilonPly1`](Attax.Console/SelfPlayLogGenOptions.cs:57)). |
| `--epsilonPly2` | `int` | `25` | Second epsilon schedule pivot ply (must be ≥ `--epsilonPly1`) ([`EpsilonPly2`](Attax.Console/SelfPlayLogGenOptions.cs:60)). |
| `--nodesMin` | `int?` | *(derived)* | Minimum node budget when `--profileMode Random`. If omitted, defaults to `--nodeBudget` (see [`nodesMin`](Attax.Console/Program.cs:117)). |
| `--nodesMax` | `int?` | *(derived)* | Maximum node budget when `--profileMode Random`. If omitted, defaults to `--nodeBudget` (see [`nodesMax`](Attax.Console/Program.cs:118)). |
| `--topKSet` | `string?` | *(derived)* | CSV of top-K values used when `--profileMode Random`. If omitted, uses `--topK` (see [`topKSetRaw`](Attax.Console/Program.cs:123)). |
| `--tempSet` | `string?` | *(derived)* | CSV of temperature values used when `--profileMode Random`. If omitted, uses `--temp` (see [`tempSetRaw`](Attax.Console/Program.cs:126)). |
| `--profileMode` | `Fixed\|Random` | `Fixed` | Whether to use fixed settings or randomize per-game from the sets ([`ProfileMode`](Attax.Console/SelfPlayLogGenOptions.cs:75)). |
| `--weakSideChance` | `double` | `0.30` | Chance to assign one side as “weak” (see [`weakSideChance`](Attax.Console/Program.cs:132)). |
| `--weakSideNodesScale` | `double` | `0.40` | Node scaling factor when the weak side is active (see [`weakSideNodesScale`](Attax.Console/Program.cs:133)). |
| `--symmetryMode` | `None\|Random\|All` | `None` | Symmetry logging mode: no transform, one random transform, or all 8 transforms ([`SymmetryMode`](Attax.Console/SelfPlayLogGenOptions.cs:84)). |
| `--samplesPerGame` | `int?` | *(hidden)* | Legacy alias for `--samples`; when provided, it overrides `--samples` (see [`SamplesPerGame`](Attax.Console/SelfPlayLogGenOptions.cs:27)). |
| `--debugInit` | `bool` | *(hidden)* | Emit a one-line “init position” snapshot during log generation ([`DebugInit`](Attax.Console/SelfPlayLogGenOptions.cs:36)). |

#### Examples

Fixed parameters:

```bash
dotnet run -c Release --project Attax.Console -- selfplayloggen --games 1000 --seed 0 --out logs.bin --nodeBudget 1000 --aiDepth 3 --samples 20
```

Randomized per-game budgets/temperatures/topK:

```bash
dotnet run -c Release --project Attax.Console -- selfplayloggen --games 500 --seed 123 --out logs.bin --profileMode Random --nodesMin 400 --nodesMax 1600 --topKSet 1,2,4 --tempSet 0.8,1.0,1.2
```

---

### `modelarena` — evaluator vs evaluator matches

Definition: [`ModelArenaOptions`](Attax.Console/ModelArenaOptions.cs:4) → execution in [`RunModelArena()`](Attax.Console/Program.cs:704).

#### Synopsis

```text
Attax.Console modelarena [options]
```

#### Options

| Flag | Type | Default | Meaning |
| --- | --- | --- | --- |
| `--model1` | `string` | `heuristic` | Model 1 path or the literal `heuristic` ([`Model1`](Attax.Console/ModelArenaOptions.cs:6)). |
| `--model2` | `string` | `heuristic` | Model 2 path or the literal `heuristic` ([`Model2`](Attax.Console/ModelArenaOptions.cs:9)). |
| `--games` | `int` | `100` | Number of arena games (sides swap halfway through) ([`Games`](Attax.Console/ModelArenaOptions.cs:12), swap logic in [`RunModelArena()`](Attax.Console/Program.cs:737)). |
| `--ort` | `Cpu\|Cuda` | `Cpu` | ONNX Runtime provider used when a model path is provided ([`Ort`](Attax.Console/ModelArenaOptions.cs:15)). |
| `--aiDepth` | `int` | `3` | Search depth used for both players ([`AiDepth`](Attax.Console/ModelArenaOptions.cs:18)). |
| `--useOrthogonalOnlyCapture` | `bool` | `false` | Use orthogonal-only capture rule ([`UseOrthogonalOnlyCapture`](Attax.Console/ModelArenaOptions.cs:21)). |
| `--useMLRootOnly` | `bool` | `false` | Use ML evaluator on root only ([`UseMLRootOnly`](Attax.Console/ModelArenaOptions.cs:24)). |
| `--disableQuiescence` | `bool?` | `null` (auto) | If provided, forces quiescence on/off. If omitted (default `null`), resolves to `true` when either model is not `heuristic`, otherwise `false` (see [`disableQuiescence`](Attax.Console/Program.cs:713)). |

#### Examples

Heuristic vs ONNX (CPU):

```bash
dotnet run -c Release --project Attax.Console -- modelarena --model1 ataxx_value.onnx --model2 heuristic --games 200 --ort cpu
```

ONNX vs ONNX (CUDA requested):

```bash
dotnet run -c Release --project Attax.Console -- modelarena --model1 a.onnx --model2 b.onnx --games 200 --ort cuda
```

---

### `mirror-test` — deterministic mirror sanity test

Definition: [`MirrorTestOptions`](Attax.Console/MirrorTestOptions.cs:4) → execution in [`RunMirrorTest()`](Attax.Console/Program.cs:836).

#### Options

| Flag | Type | Default | Meaning |
| --- | --- | --- | --- |
| `--model` | `string` | `heuristic` | Model path or the literal `heuristic` ([`Model`](Attax.Console/MirrorTestOptions.cs:6)). |
| `--ort` | `Cpu\|Cuda` | `Cpu` | ONNX Runtime provider when a model path is used ([`Ort`](Attax.Console/MirrorTestOptions.cs:9)). |
| `--side` | `Red\|Blue` | `Red` | Side-to-move used when evaluating the deterministic test board ([`Side`](Attax.Console/MirrorTestOptions.cs:12)). |

Example:

```bash
dotnet run -c Release --project Attax.Console -- mirror-test --model ataxx_value.onnx --ort cpu --side red
```

---

### `validate-log` — validate a training log (`.bin`)

Definition: [`ValidateLogOptions`](Attax.Console/ValidateLogOptions.cs:4) → execution in [`RunValidateLog()`](Attax.Console/Program.cs:892) using [`BinaryTrainingLogReader`](Attax.Console/BinaryTrainingLogReader.cs:6).

#### Options

| Flag | Type | Default | Meaning |
| --- | --- | --- | --- |
| `--path` | `string` | `training_data.bin` | Path to the log file to validate ([`Path`](Attax.Console/ValidateLogOptions.cs:6)). |
| `--strict` | `bool` | `true` | Enforces per-game completeness in the reader (see strict checks in [`ReadAndValidate()`](Attax.Console/BinaryTrainingLogReader.cs:34)). |

Example:

```bash
dotnet run -c Release --project Attax.Console -- validate-log --path logs.bin --strict true
```

---

### `validate-hash` — validate incremental Zobrist hashing

Definition: [`ValidateHashOptions`](Attax.Console/ValidateHashOptions.cs:4) → execution in [`RunValidateHash()`](Attax.Console/Program.cs:931).

#### Options

| Flag | Type | Default | Meaning |
| --- | --- | --- | --- |
| `--games` | `int` | `25` | Number of random games to simulate ([`Games`](Attax.Console/ValidateHashOptions.cs:6)). |
| `--seed` | `int` | `0` | Random seed ([`Seed`](Attax.Console/ValidateHashOptions.cs:9)). |
| `--moves` | `int` | `120` | Max plies per game ([`Moves`](Attax.Console/ValidateHashOptions.cs:12)). |
| `--strict` | `bool` | `true` | Throw on the first mismatch if `true`; otherwise log warnings (see [`strict`](Attax.Console/Program.cs:935)). |

Example:

```bash
dotnet run -c Release --project Attax.Console -- validate-hash --games 50 --seed 0 --moves 200 --strict true
```

---

## Python training (`train.py`)

The training script [`train.py`](train.py:1) reads `.bin` logs produced by `selfplayloggen`, trains a small CNN value network in PyTorch, and exports `ataxx_value.onnx` (see [`AtaxxValueNet`](train.py:457) and the ONNX export block around [`torch.onnx.export`](train.py:1010)).

### What it expects as input

- One or more training log files matching `*.bin`.
- By default, it scans the current directory (see [`DATA_DIR`](train.py:25)).
- If `--data` is provided:
  - a directory path is scanned for `*.bin`
  - a file path is treated as a single log input

### What it produces

- An ONNX value model written to `ataxx_value.onnx` (see [`MODEL_PATH`](train.py:24)).

### Trainer arguments

Parsed in [`parse_args()`](train.py:509) and passed into [`train()`](train.py:738).

| Flag | Type | Default | Meaning |
| --- | --- | --- | --- |
| `--data` | `str` | *(none)* | Path to a `.bin` file or directory. If omitted, scan the current directory ([`--data`](train.py:511)). |
| `--epochs` | `int` | `10` | Training epochs ([`EPOCHS`](train.py:21)). |
| `--batch-size` | `int` | `1024` | Batch size ([`BATCH_SIZE`](train.py:19)). |
| `--device` | `auto\|cpu\|cuda` | `auto` | Device selection; `cuda` errors if CUDA is unavailable (see [`resolve_device()`](train.py:617)). |
| `--check-onnx` | `bool` | `false` | Validate the exported ONNX with ONNX checker + ONNX Runtime (see [`validate_onnx_export()`](train.py:703)). |
| `--num-workers` | `int` | `0` | DataLoader worker count ([`--num-workers`](train.py:541)). |
| `--pin-memory` | `bool` | `false` | Enable pinned host memory (useful with CUDA) ([`--pin-memory`](train.py:546)). |
| `--prefetch-factor` | `int` | `4` | DataLoader prefetch factor (only used when `--num-workers > 0`) ([`--prefetch-factor`](train.py:552)). |
| `--shuffle-buffer` | `int` | `0` | Approximate sample count used for stream shuffling (0 disables) ([`shuffle_buffer`](train.py:559)). |
| `--amp` / `--no-amp` | `bool` | *(auto)* | Mixed precision control. Default is “enabled on CUDA, disabled otherwise” (see [`amp_enabled`](train.py:775)). |
| `--val-fraction` | `float` | `0.05` | Validation fraction in `[0,1]`, split deterministically by game id hash (see [`game_in_validation_split()`](train.py:60)). |
| `--overfit-n` | `int` | `0` | Overfit sanity mode (train on the first N samples only) (see [`materialize_first_n()`](train.py:418)). |
| `--strict-metadata` | `0\|1` | `1` | Strict metadata checks when reading logs ([`strict_metadata`](train.py:592)). |
| `--skip-bad-games` | `0\|1` | `0` | If non-strict, skip mismatched samples/games instead of failing ([`skip_bad_games`](train.py:599)). |
| `--expected-orth-capture` | `0\|1` | `None` (falls back to `0`) | Expected capture-rule bit; if omitted (default `None`), falls back to `0` (see argument definition in [`parse_args()`](train.py:607) and checks in [`_iter_file_samples()`](train.py:104)). |

### Example commands

Train on CPU using all `*.bin` in the current directory:

```bash
py -3.11 -m train --device cpu
```

Train on CUDA (will error if CUDA is unavailable):

```bash
py -3.11 -m train --device cuda --pin-memory --num-workers 2
```

Quick sanity check: overfit a small number of samples:

```bash
py -3.11 -m train --device auto --overfit-n 5000 --val-fraction 0
```

---

## File formats

### Training logs (`.bin`)

The log writer is [`BinaryTrainingLogSink`](Attax.Console/BinaryTrainingLogSink.cs:9). The validator is [`BinaryTrainingLogReader`](Attax.Console/BinaryTrainingLogReader.cs:6). The Python reader mirrors the same format in [`AtaxxStreamingDataset._iter_file_samples()`](train.py:104).

#### File header (v1)

v1 logs start with a **6-byte** header:

- Magic: 4 bytes, ASCII `ATLG` (writer constant: [`FileMagic`](Attax.Console/BinaryTrainingLogSink.cs:10))
- Version: `ushort` (writer constant: [`FormatVersion`](Attax.Console/BinaryTrainingLogSink.cs:11))

#### Record stream

After the file header, the stream is a sequence of records. Each record starts with a **1-byte type**:

| Type byte | Record | Payload |
| --- | --- | --- |
| `1` | GameHeader | `uint gameId`, `ulong seed`, `byte ruleFlags`, `byte boardSize` (see [`WriteGameHeader()`](Attax.Console/BinaryTrainingLogSink.cs:160)) |
| `2` | CompressedSampleBlock | `int count`, `int uncompressedSize`, `int compressedSize`, `uint crc32`, followed by LZ4 payload (see [`FlushBuffer()`](Attax.Console/BinaryTrainingLogSink.cs:175)) |
| `3` | GameResult | `uint gameId`, `sbyte resultFromRedPov`, `ushort totalPlies` (see [`WriteGameResult()`](Attax.Console/BinaryTrainingLogSink.cs:168)) |

Each decompressed sample in a CompressedSampleBlock is a fixed-size 32-byte struct (mirrored by [`SAMPLE_DTYPE`](train.py:28)):

- `uint gid`
- `ushort ply`
- `ulong red`, `ulong blue`, `ulong blocked`
- `byte side` (`0` = red, `1` = blue)
- `byte rules` (currently uses bit 0 for the orthogonal-only capture flag)

### ONNX model (`.onnx`)

The trainer exports an ONNX model to `ataxx_value.onnx` (see [`MODEL_PATH`](train.py:24)). The ONNX evaluator is [`OnnxValueEvaluator`](Attax.Eval.OnnxRuntime/OnnxValueEvaluator.cs:15).

Current expectations:

- Input tensor shape: `[N, 4, 7, 7]` (see [`bitboards_to_numpy_batch()`](train.py:45) and tensor creation in [`CreateInputTensor()`](Attax.Eval.OnnxRuntime/OnnxValueEvaluator.cs:80)).
- Output tensor shape: `[N]` or `[N, 1]` depending on the exported graph; the .NET evaluator reads one float per sample from the first output tensor ([`EvaluateBatch()`](Attax.Eval.OnnxRuntime/OnnxValueEvaluator.cs:53)).

### Shared config (`attax.config.json`)

The config is validated by .NET via [`AttaxConfigLoader.LoadOrDefault()`](Attax.Core/AttaxConfig.cs:59). It records:

- board size (must match compiled constant `7`; see [`AttaxConstants.BaseConst.BoardSize`](Attax.Core/AttaxConstants.cs:11) and validation in [`Validate()`](Attax.Core/AttaxConfig.cs:70))
- ONNX IO naming conventions (currently informational; the ONNX evaluator reads model metadata at runtime)
- log format magic/version and side encoding

---

## Gotchas / troubleshooting

### .NET 8 is required for the CLI

The console app targets `net8.0` (see [`Attax.Console/Attax.Console.csproj`](Attax.Console/Attax.Console.csproj:15)). Use a .NET 8 SDK to build and run.

### ONNX Runtime CPU vs CUDA

- CPU inference works out of the box using `Microsoft.ML.OnnxRuntime`.
- CUDA inference requires building/running with the GPU provider package.
  - By default, the evaluator project sets `UseOnnxGpu=false` (see [`Attax.Eval.OnnxRuntime/Attax.Eval.OnnxRuntime.csproj`](Attax.Eval.OnnxRuntime/Attax.Eval.OnnxRuntime.csproj:7)).
  - If you pass `--ort cuda` without a CUDA-capable build/environment, the evaluator will throw with a descriptive error (see the exception around [`AppendExecutionProvider_CUDA()`](Attax.Eval.OnnxRuntime/OnnxValueEvaluator.cs:26)).

### Determinism and reproducibility

- `selfplayloggen` is seeded (`--seed`) and disables parallel root search in its engine configs (see config setup in [`RunSelfPlayLogGen()`](Attax.Console/Program.cs:210)), which helps reproducibility.
- The Python trainer does **not** set global RNG seeds; model initialization and some data ordering choices can make training runs non-deterministic (see the absence of explicit seeding in [`train()`](train.py:738)).

### Log writing and merging

- Do **not** run multiple writers to the same `--out` path: [`BinaryTrainingLogSink`](Attax.Console/BinaryTrainingLogSink.cs:9) opens the file with `FileShare.Read` and assumes a single writer.
- When merging v1 `.bin` logs, do not concatenate multiple full files including multiple v1 headers. Keep one file’s 6-byte header and append the other file(s) starting after their headers (header handling is in [`InitializeOrValidateFileHeader()`](Attax.Console/BinaryTrainingLogSink.cs:132) and format detection in [`DetectFormat()`](Attax.Console/BinaryTrainingLogReader.cs:147)).
- After merging, run `validate-log` to confirm structure and checksums.
