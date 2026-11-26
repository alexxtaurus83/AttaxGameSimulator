# Product structure

## Product

Attax (Merge Conflict). `AttaxSimulator` is the offline self-play, economy calibration, and model-training workspace. The authoritative rules, AI engine, non-ML engine host (`Attax.Engine.Host`), protocol, and canonical JSONs live in `AttaxShared`; online `Attax.UserSimulator` bots live in `merge-conflict-server/tools/UserSimulator`.

## Related repositories

| Repository | Path | Responsibility | Direction |
|---|---|---|---|
| AttaxShared | `F:\github\repo\AttaxShared` | Rules, AI search, `Attax.Engine.Host` (`EngineParamsFile`, `PlayerSpec`, `PlayerFactory`, `AgentTurnPlanner`, `BoardRenderer`), DTOs, protocol, sync, canonical JSONs. Its engine API reference (`docs/IMPLEMENTED-ENGINE_RULES_API_SPEC.md`) and overview (`docs/engineReadme.md`) are maintained there | referenced here via `ProjectReference` (`AttaxSharedRepoPath`, default `..\AttaxShared`) |
| AttaxUnity | `F:\Unity_projects\AttaxUnity` | Unity client | consumes compiled `netstandard2.1` DLLs and JSONs exported from AttaxShared |
| merge-conflict-server | `F:\github\repo\merge-conflict-server` | Online backend and `tools/UserSimulator` bots | consumes AttaxShared (`Attax.Engine.Host`, protocol, sync) |

## Areas

| Area | Location | Responsibility |
|---|---|---|
| Model contract | `Attax.Model/` | board encoding and policy/value interfaces for training and evaluation. Never copied into Unity |
| Data and play tools | `Attax.Data/`, `Attax.Play/`, `Attax.Console/` | training-log format, `ModelPlayerFactory` / `PolicyPlayer`, `LocalGameRunner`, `RulesGameRunner`, self-play, arenas, relabeling, economy calibration, validation, CLI (consumes `Attax.Engine.Host` from `AttaxShared`) |
| ONNX evaluation | `Attax.Eval.OnnxRuntime/` | CPU-only ONNX Runtime adapter |
| Training | `train.py`, `ataxx_*.py`, `tests_py/` | PyTorch training, ONNX export, data parsing, parity tests |
| Tests | `Attax.Core.Tests/`, `tests_py/` | engine, runner, model-play, economy, and contract tests |

Root docs `Readme.md`, `engineReadme.md`, `HEURISTIC_TUNING_PLAN.md` and `IMPLEMENTED-ENGINE_RULES_API_SPEC.md` stay where they are and are only indexed in [`ProjectSpecificDocs.md`](ProjectSpecificDocs.md).
