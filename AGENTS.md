# Agent Instructions

## Project Scope

`AttaxSimulator` is the authoritative Ataxx engine and model-training workspace. It contains:

| Area | Location | Responsibility |
| --- | --- | --- |
| Shared rules engine | `Attax.Core.Utils/` | Dependency-free board state, move/action codecs, Zobrist hashing, authoritative rules validation, and rule execution. This code is shared with the server and copied into the Unity client. |
| AI engine | `Attax.Core/` | Negamax search, heuristic evaluation, engine parameters, and search helpers. |
| Model contract | `Attax.Model/` | Board encoding and policy/value interfaces used for training and evaluation. Do not copy this project into Unity. |
| Data and play tools | `Attax.Data/`, `Attax.Play/`, `Attax.Console/` | Training-log format, self-play, arenas, relabeling, validation, and command-line tooling. |
| ONNX evaluation | `Attax.Eval.OnnxRuntime/` | CPU-only ONNX Runtime model adapter for local tooling. |
| Training | `train.py`, `ataxx_*.py`, `tests_py/` | PyTorch training, ONNX export, data parsing, and contract-parity tests. |

The primary client repository is `F:\Unity_projects\AttaxUnity`. It consumes synchronized copies of `Attax.Core.Utils` (authoritative rules) and `Attax.Core` (heuristic search and engine parameters). It owns Unity UI, lifecycle, animation, Sentis inference, and client presentation. Do not add Unity APIs, Unity packages, UI behavior, or Editor concerns to this repository.

The backend is maintained as the Attax server project (`client_id: "attax-server"` in Zengram). It references `Attax.Core.Utils` for authoritative gameplay rules and must not depend on `Attax.Core`, model training, or the console AI tooling. Server/networking behavior is not implemented here.

## Architecture Boundaries

- Keep `Attax.Core.Utils` free of third-party dependencies, file I/O, ONNX, PyTorch, search, and AI allocations. Changes here affect both the Unity project and the server.
- Keep `Attax.Core` free of model, ONNX, log-format, and filesystem code so it remains portable to Unity.
- Keep training-only model code in `Attax.Model`, `Attax.Eval.OnnxRuntime`, and Python files. `Attax.Model` is never copied to Unity.
- `Attax.Core/engine-params.json` is the single source of engine parameter values. Do not introduce hidden code defaults or duplicate parameter files.
- Preserve the `attax-pv-1` model contract and v4 (`ATLG`) log-format compatibility unless an explicit coordinated migration updates all consumers and fixtures.
- Treat generated logs, checkpoints, ONNX artifacts, `data/`, `models/`, `runs/`, `bin/`, and `obj/` as generated outputs. Do not commit them unless the user explicitly requests it.

## Required Skills And Planning

For non-trivial work, read `C:\Users\alexx\.skills\codegraphcontext\SKILL.md` and `C:\Users\alexx\.skills\zengram-mcp\SKILL.md` before planning or implementation.

Plans and task hand-offs must begin with this block:

```markdown
> **Before implementing, read `AGENTS.md` (project root) and follow it.** It defines shared-engine boundaries plus required CodeGraphContext and Zengram workflows.
>
> **Required for this plan:**
> - **`AGENTS.md`**: sections that apply: <sections>
> - **`codegraphcontext`**: impact analysis: <symbols and callers/callees checked, or N/A with reason>
> - **`zengram-mcp`**: memories consulted and facts/statuses to store after verification
```

Before changing a C# symbol, locate it with CodeGraphContext and inspect its callers/callees. Before changing shared rules or a public contract, also assess the Unity and server impact. For Python contract/training changes, locate the corresponding C# contract or parity fixture before editing.

## CodeGraphContext

CodeGraphContext uses the isolated `attax_simulator` graph in local FalkorDB. Its `.cgcignore` deliberately excludes generated artifacts, documentation, parameters, and build outputs.

- Use `find_code` to locate code and `analyze_code_relationships` to inspect callers, callees, importers, and inheritance before non-trivial edits.
- If the repository is not indexed, index `F:\github\repo\AttaxSimulator`; if a graph query fails, report that state rather than guessing from graph data.
- Start `watch_directory` when edits span multiple files or sessions. Otherwise refresh the graph after verified source changes with the incremental CodeGraphContext update/index operation.
- Use file reads or grep for data formats, command-line strings, JSON, and text fixtures not represented as graph symbols.

## Shared Memory (Zengram)

Zengram is shared across all Attax repositories. Current source and this file override any memory entry; verify the files cited by a memory before relying on it.

| Scope (`client_id`) | Use for |
| --- | --- |
| `attax-engine` | Cross-repository rules-engine facts and decisions. Use for changes to `Attax.Core.Utils` or shared gameplay contracts. |
| `attax-server` | Backend integration, persistence, API, and server compatibility. Consult when shared rules affect the backend. |
| `attax-unity` | Unity client integration and rules-copy compatibility. Consult when shared rules affect the client. |
| `attax-simulator` | Simulator, console, training, ONNX, and tooling facts specific to this repository. |

- Before non-trivial work, search `attax-simulator` with `brain_search` using `format: "index"`; retrieve relevant results with `format: "full"`.
- For shared rules, model contracts, or cross-repository behavior, also search the applicable `attax-engine`, `attax-unity`, and/or `attax-server` scope.
- Store only verified outcomes. Use facts with keys such as `attax-simulator-<topic>`, statuses with subjects such as `attax-simulator-<topic>`, and decisions for confirmed choices and rationale.
- State the project, verified files, and source plan/session in every memory. Never store secrets, tokens, unverified assumptions, transient progress, or large code excerpts.
- At session close, follow the `zengram-mcp` session-end ritual: inspect artifacts, store an honest session event, and update durable facts/statuses that changed.

## Verification

Choose checks that cover the changed layer. Run the smallest relevant checks first, then the cross-language parity or full suite when contracts changed.

```powershell
dotnet build -c Release Attax.sln
dotnet test Attax.Core.Tests/Attax.Core.Tests.csproj -c Release
python -m unittest discover -s tests_py -t .
```

- Shared-rule, action-codec, board-encoding, symmetry, log-format, or model-contract changes require both C# tests and Python tests, including parity fixtures where applicable.
- Search/heuristic/parameter changes must preserve classic regression behavior unless an intentional, documented parameter or golden update is part of the request.
- Training or ONNX changes must preserve `attax-pv-1` input/output names, dtypes, shapes, action count, metadata validation, and C#/Python parity.
- State precisely which verification commands passed, were not run, or could not run.
