# Testing

Generic selection, evidence and writing rules are in `F:\github\repo\Agents\baseTesting.md`; the executor contract is in `F:\github\repo\Agents\baseSubagents.md`; .NET specifics in `F:\github\repo\Agents\stacks\dotnet.md`. This file holds the Simulator's commands and gates.

## Commands

Each row is one separate `COMMAND` for the `small-model` executor. Run the smallest relevant check first, then the cross-language parity or full suite when contracts changed.

| Scope | Working directory | Command |
|---|---|---|
| Build everything | repository root | `dotnet build -c Release Attax.sln` |
| Engine rules, runner, and model-play tests | repository root | `dotnet test Attax.Core.Tests/Attax.Core.Tests.csproj -c Release` |
| Targeted engine tests | repository root | `dotnet test Attax.Core.Tests/Attax.Core.Tests.csproj -c Release --filter "FullyQualifiedName~<TestClass>"` |
| Python training, data and parity tests | repository root | `python -m unittest discover -s tests_py -t .` |

## Rules

- `Attax.sln` references the shared projects (`Attax.Core.Utils`, `Attax.Core`, `Attax.Engine.Host`) from the sibling `..\AttaxShared` checkout (override with `-p:AttaxSharedRepoPath=<path>`). Run `dotnet test ..\AttaxShared\AttaxShared.sln -c Release` when shared code changes. Bot tests (`Attax.UserSimulator.Tests`) live in `..\merge-conflict-server\tools\UserSimulator\UserSimulator.sln`.
- Test projects use xUnit v3 in VSTest mode (`xunit.v3.mtp-off` with `Microsoft.NET.Test.Sdk`), so `dotnet test --filter` works. Do not switch to the Microsoft Testing Platform runner without updating these commands.
- **Parity:** shared-rule, action-codec, board-encoding, symmetry, log-format or model-contract changes need both C# tests and Python tests, including parity fixtures where applicable.
- Search, heuristic or parameter changes must preserve classic regression behavior unless an intentional, documented parameter or golden update is part of the request.
- Training or ONNX changes must preserve the `attax-pv-1` input and output names, dtypes, shapes, action count, metadata validation and C#/Python parity.
- Documentation-only edits need link, reference and diff checks, not a claim that runtime behavior was retested.

## Affected tests

```
node F:\github\repo\Agents\tools\affected-tests.mjs --json
```

Config `.agents/affected-tests.json` (`dotnet-csproj` plus `map` edges: Python sources and `tests_py/**` select `python -m unittest`). Exit `1` (a changed source file reached no test) means add a test or a `map` edge. The executor runs the printed `run` command; for Python selections run the table command above.

## Evidence

Evidence and acceptance rules: `baseTesting.md` section 3 and `baseSubagents.md` "Accepting a report". Record per command the exact command, working directory, filter, exit code, executed/passed/failed/skipped counts, failed test names and artifact paths, copied from the runner report. State which commands passed, were not run or could not run.
