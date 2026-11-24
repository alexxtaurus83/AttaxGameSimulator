# Product structure

## Product

Attax (Merge Conflict). `AttaxSimulator` is the model-training workspace and the home of the `Attax.UserSimulator` bots. The authoritative rules, AI engine, protocol and canonical JSONs live in `AttaxShared`.

## Related repositories

| Repository | Path | Responsibility | Direction |
|---|---|---|---|
| AttaxShared | `F:\github\repo\AttaxShared` | Rules, AI search, DTOs, protocol, sync, canonical JSONs. Its engine API reference (`docs/IMPLEMENTED-ENGINE_RULES_API_SPEC.md`) and overview (`docs/engineReadme.md`) are maintained there | referenced here via `ProjectReference` (`AttaxSharedRepoPath`, default `..\AttaxShared`) |
| AttaxUnity | `F:\Unity_projects\AttaxUnity` | Unity client | consumes compiled `netstandard2.1` DLLs and JSONs exported from AttaxShared |
| merge-conflict-server | `F:\github\repo\merge-conflict-server` | Online backend | consumes AttaxShared; its tests run these bots |

## Areas

| Area | Location | Responsibility |
|---|---|---|
| Model contract | `Attax.Model/` | board encoding and policy/value interfaces for training and evaluation. Never copied into Unity |
| Data and play tools | `Attax.Data/`, `Attax.Play/`, `Attax.Console/` | training-log format, self-play, arenas, relabeling, validation, CLI |
| ONNX evaluation | `Attax.Eval.OnnxRuntime/` | CPU-only ONNX Runtime adapter |
| Training | `train.py`, `ataxx_*.py`, `tests_py/` | PyTorch training, ONNX export, data parsing, parity tests |
| Bots | `UserSimulator/` | `Attax.Agents`, `Attax.UserSimulator` and its tests |
| Tests | `Attax.Core.Tests/`, `tests_py/` | engine and contract tests |

Root docs `Readme.md`, `engineReadme.md`, `HEURISTIC_TUNING_PLAN.md` and `IMPLEMENTED-ENGINE_RULES_API_SPEC.md` stay where they are and are only indexed in [`ProjectSpecificDocs.md`](ProjectSpecificDocs.md).
