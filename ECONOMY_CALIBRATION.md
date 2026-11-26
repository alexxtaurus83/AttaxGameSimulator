# Economy calibration guide

How to measure and set the ByteCode ability prices, board scaling and AI gate value with `Attax.Console`. The economy itself is described in `F:\Unity_projects\AttaxUnity\docs\ECONOMY.md`. These verbs are additive: `selfplay`, `relabel`, `arena`, `train.py` and the v4 (`ATLG`) log format are untouched, and economy games write a separate JSONL format (`EconomyLog`, `schemaVersion: 1`).

## What the simulator uses without calibration

`game-rules.json` and `ai-ability-config.json` in `AttaxShared\src\Attax.Core.Utils\Rules` hold seed prices that work out of the box. Calibration replaces them with measured values.

## Commands

| Verb | Purpose |
|---|---|
| `economy-sim` | Play rules-engine games (credits, footprints, abilities) and write one economy `.jsonl` log. `--boards all|id,...`, `--games`, `--red`, `--blue`, `--swap-colors`, `--abilities`, `--sample-values`, `--seed`, `--parallel`, `--tag`, `--out`. Prints a `SUMMARY` line. |
| `economy-analyze` | Fit board scaling, value and price the ability levels from logs, verify cast frequencies (Pass 2), write `economy-report.{md,json}`; `--apply` writes the configs and `docs/ECONOMY.md` only when converged. |
| `economy` | The same calibration in memory in one run (abilities-off and abilities-on games, then analysis). For quick runs. |

Use explicit `true` or `false` (case-insensitive) for `economy-sim --abilities`, `economy-sim --swap-colors`, and `--quiescence` on `economy-analyze` or `economy`. If omitted, abilities and quiescence default to `true`, and color swapping defaults to `false`. `--apply` is a switch with no value.

Without `--rules` / `--ai-config` the tool uses the `AttaxShared` source files (found as a sibling checkout or via `ATTAX_SHARED_REPO`), so log hashes match the source configs. `--apply` refuses to write anything that is not an explicitly named file or the source file, so it never edits `bin\...\publish` copies. `docs\ECONOMY.md` is found via `ATTAX_UNITY_REPO` or the workspace layout, or pass `--economy-doc`.

## 1. Timing pilot (after changing the tool or the engine)

Run one game per configuration, single-threaded, and read `wallMs`, `searchMsRed/Blue`, `abilityDecideMsRed/Blue`, `valueSampleMs`, `plies` and the casts from the logs:

```
Attax.Console economy-sim --boards default --games 1 --red classic:depth=2,quiescence=false --blue classic:depth=2,quiescence=false --abilities true --sample-values 10 --parallel 1 --out runs\economy-pilot\pilot_d2_q0.jsonl
```

Repeat for `d2 q1`, `d3 q0`, `d3 q1`. Probe multi-core speed-up with a 12-game `--parallel 0` run. Then set `S01_GAMES`..`S06_GAMES` in `economy-overnight.cmd` so the total lands in 3 to 6 hours. Last measured numbers are in `IMPLEMENTATION_CHECKLIST.md`.

## 2. Overnight log suite

Disable sleep and run `economy-overnight.cmd` from this folder with clean, committed AttaxShared and AttaxSimulator checkouts. It publishes the console, plays six sessions over all 20 boards (about 4.3 hours on the reference machine), then runs draft analysis without `--apply`. Value samples come from each side's own turns 4 through its last turn minus 4, inclusive. Re-running with the same inputs skips completed `.jsonl` sessions and restarts an interrupted session. Results: `runs\economy-overnight-own-turn-v1\SUMMARY.txt` and `analysis\economy-report.md` in that directory.

If `runs\economy-overnight\` exists elsewhere, treat it as legacy evidence only: the old parser enabled abilities in the income baseline and doubled game counts in sessions that requested no swap. Do not reuse it or mix those logs with the new run.

Smoke test: `set RUNDIR=runs\economy-smoke & set S01_GAMES=2 & set S02_GAMES=2 & set S03_GAMES=2 & set S04_GAMES=2 & set S05_GAMES=2 & set S06_GAMES=2 & set VERIFY_GAMES=1 & set NO_LOGWINDOW=1 & economy-overnight.cmd` (diagnostic only: too few samples to calibrate).

## 3. Apply

```
Attax.Console\bin\Release\net9.0\publish\Attax.Console.exe economy-analyze --in runs\economy-overnight-own-turn-v1 --out runs\economy-overnight-own-turn-v1\applied --apply
```

Before applying, check that all six logs completed, no `.tmp` file remains, and the rules, AI config and engine parameter hashes, source revisions, build ID and sampling method match the analysis build and inputs. The tool rejects (exit non-zero, nothing modified) when inputs disagree, when a level has no positive value samples, when Wall I or the fit lacks data, when Pass 2 does not converge, when the frequency targets miss in opposite directions or when a price exceeds the cap. On success it prints a `CONVERGED` line and rewrites `game-rules.json`, `ai-ability-config.json` and the marked block of `docs\ECONOMY.md` transactionally (all previous contents are restored if any write fails).

If Pass 2 does not converge, iterate on the existing logs without re-running the overnight suite: adjust `--verify-games`, `--iterations`, `--tolerance` or `--anchors anchors.json` (camelCase keys of the anchors in `ECONOMY.md` section 5).

## 4. Propagate after applying

1. `AttaxShared`: `dotnet test AttaxShared.sln -c Release` (update pinned shipped-config tests to the new prices).
2. `AttaxSimulator`: `dotnet test Attax.sln -c Release` and `python -m unittest discover -s tests_py -t .`.
3. `merge-conflict-server`: `dotnet test ... Attax.Server.sln` (regenerate fingerprint fixtures).
4. `AttaxUnity`: do not export from here; plan 01e stages the Unity callers and runs the single `Export-Unity.ps1`.

Repeat the whole procedure whenever the rules, the board catalog or the AI tacticians change.
