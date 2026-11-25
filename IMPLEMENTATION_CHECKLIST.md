# Implementation Checklist: 01d Economy Calibration (Phase A)

Plan: `F:/Unity_projects/AttaxUnity/.kilo/plans/01d-economy-calibration.md` (Large tier).

- [x] Context: both AGENTS.md, 01c prerequisites present (`AbilityDecider`, `MeasureValues`, `ISearchEnginePlayer.ChooseRestricted`, AiAbilityConfig v2).
- [x] Impact: `LocalGameRunner.RunGame`, `LogV3Writer`, `selfplay`/`arena` untouched; additions only (`RulesGameRunner`, `Attax.Console/Economy/*`, 3 verbs).
- [x] `RulesGameRunner`, `EconomyLog` (JSONL schema v1), `EconomyAnchors`, `PriceSolver`, `Calibrator`, `EconomyReport`, `EconomySim`, `EconomyContext`, `Program.Economy.cs`, Options/Program dispatch.
- [x] Tests: `EconomyPriceSolverTests`, `RulesGameRunnerTests`, `EconomyLogAndBackwardCompatTests` 31/31; full `Attax.sln` Release 172/172 (147 core + 25 user simulator).
- [x] Pilot (1 game, `--parallel 1`, default board, 10 value samples), wallMs / searchMs R+B / abilityDecideMs R+B / valueSampleMs / plies / casts:
  - d2 q0: 4446 / 605+591 / 1333+1168 / 739 / 96 / 1
  - d2 q1: 4157 / 548+533 / 1179+1152 / 737 / 96 / 1
  - d3 q0: 11401 / 1161+1236 / 3642+3789 / 1562 / 102 / 14
  - d3 q1: 17929 / 4358+4567 / 3659+3539 / 1797 / 95 / 5
  - Probe: 12 games d3q1 all boards `--parallel 0` (12 cores): 33.9 s total, 2.83 s/game (K ~ 6.3).
- [x] Per-session all-board probes (12 cores): s01 d2q0 abilities off 0.64 s/game; s02 pairs 0.33 s/game; s03 pairs 3.2 s/pair; s04 0.97 s; s05 0.92 s; s06 estimated from d3q1 2.8 s. Defaults `S01..S06 = 2500/2400/1000/2500/2600/1700` (about 4.5 h + analysis).
- [x] `economy-overnight.cmd` smoke (all S=2, VERIFY_GAMES=1): exit 0, six logs, `analysis\economy-report.{md,json}`, `SUMMARY.txt`. Smoke analysis correctly reports NOT CONVERGED (too few samples).
- [x] Docs: `AttaxUnity/docs/ECONOMY.md` skeleton with markers, `ECONOMY_CALIBRATION.md`, indexes, `GAME_RULES_BYTECODE.md` link, Simulator `Readme.md`.
- [ ] Python `unittest` parity: not run in this session.
- [ ] Zengram `attax-engine-economy-calibration`: stored after Phase B.

## Phase B (after the user's overnight run)

Resume at plan section 5 steps 5-8: `economy-analyze --in runs\economy-overnight --out runs\economy-overnight\applied --apply`, then Shared/Simulator/server tests, docs diff, Zengram.
