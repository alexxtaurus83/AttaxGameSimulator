# Agent Instructions

Rules that bind every session. Read a linked file when its topic comes up, not up front.

## Shared base

- Base: `F:\github\repo\Agents\baseAgents.md` (read first), with `baseTesting.md`, `basePLANNING.md`, `baseDocs.md`, `baseSubagents.md` beside it.
- Stack: `F:\github\repo\Agents\stacks\dotnet.md`
- Personal: `F:\github\repo\Agents\personal\zengram.md`, `codegraph.md`

## Project values

- Memory scope: `client_id` `attax-simulator`; related: `attax-engine`, `attax-server`, `attax-unity`
- Code graph: `attax_simulator` (its `.cgcignore` excludes generated artifacts, docs and parameters)
- Required skills (plan header lines): `codegraphcontext`, `dotnet`, `zengram-mcp`
- Plans: `.kilo/plans/`. Ignore `docsbackup/` (archive, not instructions).

## Invariants

1. Shared rules and AI code (`Attax.Core.Utils`, `Attax.Core`, `Attax.Server.Contracts`, `Attax.Client.Protocol`, `Attax.Client.Sync`) live in `..\AttaxShared`, not here. Keep `Attax.Core.Utils` free of dependencies beyond `ZLinq`, file I/O, ONNX, PyTorch, search and AI allocations.
2. `Attax.Core` stays free of model, ONNX, log-format and filesystem code so it remains portable to Unity.
3. Training-only code stays in `Attax.Model`, `Attax.Eval.OnnxRuntime` and the Python files. `Attax.Model` is never exported to Unity. No Unity APIs, packages, UI or Editor concerns here.
4. `..\AttaxShared\src\Attax.Core\engine-params.json` is the single source of engine parameters; no hidden code defaults or duplicate files.
5. Preserve the `attax-pv-1` model contract and v4 (`ATLG`) log-format compatibility unless a coordinated migration updates every consumer and fixture.
6. Generated logs, checkpoints, ONNX artifacts, `data/`, `models/`, `runs/`, `bin/`, `obj/` are outputs; do not commit them unless asked.
7. Update the engine API reference in AttaxShared in the same change as any rules behavior change; the code wins over the document.
8. Every restore, build and test command runs through the `small-model` executor ([`docs/TESTING.md`](docs/TESTING.md)).

## Docs

| Doc | Read when you... |
|---|---|
| [`docs/ProjectSpecificDocs.md`](docs/ProjectSpecificDocs.md) | need the index of project and shared docs |
| [`docs/TESTING.md`](docs/TESTING.md) | select or run C# and Python tests, check parity |
| [`docs/productStructure.md`](docs/productStructure.md) | find a project, the shared libraries or a sibling repo |
