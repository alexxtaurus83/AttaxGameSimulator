# Economy calibration guide

How to measure and set the ByteCode ability prices, board scaling and AI gate value with `Attax.Console`. The economy itself is described in `F:\Unity_projects\AttaxUnity\docs\ECONOMY.md`. These verbs are additive: `selfplay`, `relabel`, `arena`, `train.py` and the v4 (`ATLG`) log format are untouched, and economy games write a separate JSONL format (`EconomyLog`, `schemaVersion: 1`).

## What the simulator uses without calibration

`game-rules.json` and `ai-ability-config.json` in `AttaxShared\src\Attax.Core.Utils\Rules` hold seed prices that work out of the box. Calibration replaces them with measured values.

## Commands

| Verb | Purpose |
|---|---|
| `economy-sim` | Play rules-engine games and write economy JSONL. `--opening-plies 0..4` adds seeded legal standard moves before the AI takes over (default `0`). Both games in a color-swapped pair get the same opening. |
| `economy-analyze` | Fit board scaling, value and price the ability levels from logs, and verify cast frequencies. `--verify-games` sets games per tuning iteration; `--holdout-games` checks the selected table once with a separate seed. The report shows both results. |
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

Disable sleep and run `economy-overnight.cmd` with clean, committed AttaxShared and AttaxSimulator checkouts. It publishes a console inside `runs\economy-overnight-diverse-v2\console`, plays six sessions over all 20 boards with four seeded opening moves, then runs draft analysis with 100 tuning games per iteration and a 100-game holdout. It never applies prices. The six session counts use the previous batch's timing; re-pilot with the new openings if runtime matters. Re-run in the same directory only with the same build, configs and options. Results: `SUMMARY.txt` and `analysis\economy-report.md` in that directory.

Keep `runs\economy-overnight\` and `runs\economy-overnight-own-turn-v1\` separate. Both are diagnostic only in the new analyzer: they lack the new telemetry. Two `.jsonl` runs must not be mixed just because their file names match.

Smoke test in `cmd.exe`: `set "RUNDIR=runs\economy-smoke-v2" & set "S01_GAMES=2" & set "S02_GAMES=2" & set "S03_GAMES=2" & set "S04_GAMES=2" & set "S05_GAMES=2" & set "S06_GAMES=2" & set "VERIFY_GAMES=0" & set "HOLDOUT_GAMES=0" & set "NO_LOGWINDOW=1" & economy-overnight.cmd` (diagnostic only).

The report shows distinct opening board positions, capped and uncapped income, Wall I affordability from actual no-spend turn-start balances, and positive ability values by depth and board coverage. Board scaling uses uncapped rewards; spending rules still use the real cap. The mean balance at the start of the target own turn seeds the prices; the final price's observed affordability is reported separately. Value sampling still uses each side's own turns 4 through its last turn minus 4. Seeded legal openings reduce repetition, but are not human-play data; a second seed checks robustness to openings, not generalization to humans.

## 3. Apply

```
runs\economy-overnight-diverse-v2\console\Attax.Console.exe economy-analyze --in runs\economy-overnight-diverse-v2 --out runs\economy-overnight-diverse-v2\applied --verify-games 100 --holdout-games 100 --seed 20262133 --apply
```

Use the same tuning arguments and seed as the accepted draft (`20262133` with the default batch seed). Before applying, check all six logs, provenance and sampling, actual Wall affordability, and whether the per-depth values support the proposed price changes. Missing telemetry or reference balances, bad provenance, insufficient samples, out-of-cap prices, or failed tuning or holdout block writes. `--apply` requires both verification batches to cover the selected boards. On success it updates `game-rules.json`, `ai-ability-config.json` and the marked block of `docs\ECONOMY.md` transactionally.

If Pass 2 does not converge, inspect the missed targets before changing the anchors or tolerance. If the holdout fails, do not tune against that holdout. If evidence is too sparse for a specific level or cohort, collect targeted samples rather than accepting a price solely because the aggregate cast rate fits.

## 4. Propagate after applying

1. `AttaxShared`: `dotnet test AttaxShared.sln -c Release` (update pinned shipped-config tests to the new prices).
2. `AttaxSimulator`: `dotnet test Attax.sln -c Release` and `python -m unittest discover -s tests_py -t .`.
3. `merge-conflict-server`: `dotnet test ... Attax.Server.sln` (regenerate fingerprint fixtures).
4. `AttaxUnity`: do not export from here; plan 01e stages the Unity callers and runs the single `Export-Unity.ps1`.

Repeat the whole procedure whenever the rules, the board catalog or the AI tacticians change.
