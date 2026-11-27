# Implementation Checklist: Cross-Repo Shared Rules Extraction & Deduplication (AttaxSimulator)

Plan: `F:\Unity_projects\AttaxUnity\.kilo\plans\1795811266458-extract-shared-rules-and-dedup.md`

- [x] 1. Context
  - [x] 1.1 Memory recall (`attax-simulator`, `attax-engine`)
  - [x] 1.2 Docs consulted (`AGENTS.md`, `docs/TESTING.md`, `basePLANNING.md`, `stacks/dotnet.md`)
  - [x] 1.3 Impact analysis (`RunGame`, `RandomDecision`, `Select`, `Create`, `Standard`, `CreateStandardInitialBoard` callers verified)
- [x] 2. Implementation: `Attax.Play`, `Attax.Model`, `Attax.Console`
  - [x] 2.1 Modify `Attax.Play/StartBoard.cs`: delegate to `Attax.Core.Utils.StartPosition`
  - [x] 2.2 Modify `Attax.Play/LocalGameRunner.cs`: delegate `RandomDecision` to `RandomPlayer.ChooseRandom`
  - [x] 2.3 Modify `Attax.Play/RulesGameRunner.cs`: delegate turn planning to `AgentTurnPlanner.DecideDetailed` and scoring/winner to `AttaxRules.AdjudicateResult`/`GetScore`
  - [x] 2.4 Modify `Attax.Model/PolicyMoveSelector.cs`: delegate to `Attax.Core.PolicyLogitSelector`
  - [x] 2.5 Modify `Attax.Console/Economy/EconomyContext.cs`: use `RulesJsonLoader` and `AiAbilityConfig.Fingerprint().ToString("X16")`
  - [x] 2.6 Modify `Attax.Console/Program.Util.cs`, `Program.Tools.cs`, `Program.Compare.cs`: remove `CreateStandardInitialBoard`, call `StartPosition.Standard()`, reuse `PositionSampler` thresholds & `BoardRenderer.Render`
  - [x] 2.7 Existing `Attax.Core.Tests/*` verified compatible with `Attax.Core.PolicyChoice`
- [x] 3. Verification
  - [x] 3.1 Targeted tests via `small-model`
  - [x] 3.2 Full `dotnet build -c Release Attax.sln && dotnet test Attax.Core.Tests/Attax.Core.Tests.csproj -c Release --no-build` (139 passed, 0 failed) via `small-model`
- [x] 4. Close-out
  - [x] 4.1 Code graph refresh attempted (`attax_simulator`)
  - [x] 4.2 `IMPLEMENTED-ENGINE_RULES_API_SPEC.md` and Zengram `attax-simulator` updated
  - [x] 4.3 Final review done
