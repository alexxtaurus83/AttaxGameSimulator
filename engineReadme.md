This is not true. Bug fixed. need to test:

# Engine Documentation: Rules, Architecture, and Search

## 1. Shared Rules Engine (`AttaxGame`, Abilities, Economy)

`Attax.Core.Utils` provides the pure, authoritative rules engine shared between the console tools, the authoritative server, and the Unity client.

### 1.1 The Nine Abilities

Shipped `game-rules.json` (`schemaVersion: 2`, `winRule: "chipsPlusFootprints"`) defines seed base ability cost arrays (scaled per board playable cell count via `economy.boardScaling` with `referencePlayable = 48`, `alpha = 1.0`, `minFactor = 0.5`, `roundStep = 5`):
- `block`: `[40, 50]` (L1-L2)
- `wall`: `[50]` (L1)
- `unblock`: `[50, 80]` (L1-L2)
- `spawn`: `[60, 75, 90]` (L1-L3)
- `convert`: `[60, 75, 90]` (L1-L3)
- `superJump`: `[100]` (L1)
- `doubleMove`: `[120]` (L1)
- `superClone`: `[150]` (L1)
- `bomb`: `[170, 215, 255]` (L1-L3)

Credit cap is `300`, starting credits `0`, capture reward `10`, footprint reward `5`. Strictly one ability action is permitted per turn (`turn.maxAbilityActionsPerTurn = 1`). Per-player level caps can be constrained at runtime via optional `GameRulesConfig.abilityAllowance` (`AbilityAllowance`). `playerAbilities` and `abilityTexts` were removed in schema v2.

| Ability | Kind | Levels | Target | Effect | Turn Handling (`AbilityLevelEffect`) |
|---|---|---|---|---|---|
| **SuperJump** | `Move` (d >= 3, `Level = 1`) | 1 | Empty cell | Jump of distance >= 3: vacates source (leaves footprint), captures adjacent enemies | L1 `ConsumesMove` (consumes standard move and ability use) |
| **Convert (Chief)** | `Convert` | 1-3 | Enemy chip | Converts enemy chip to actor's chip without adjacent captures | L1 `EndsTurn`, L2 `FreeFrozenPlaced` (converted chip frozen this turn), L3 `Free` |
| **Block** | `Block` | 1-2 | Enemy chip | Removes enemy chip and places a blocked square | L1 `EndsTurn`, L2 `Free` |
| **Wall** | `Wall` | 1 | Empty cell | Places a blocked square on empty cell; clears any footprint | L1 `Free` |
| **DoubleMove** | `DoubleMove` | 1 | None | Arms 2 standard moves for the current player this turn | L1 `GrantsSecondMove` (consumes sole ability use) |
| **Spawn** | `Spawn` | 1-3 | Empty cell | Places actor chip on empty cell; no captures | L1 `EndsTurn`, L2 `FreeFrozenPlaced` (placed chip frozen this turn), L3 `Free` |
| **SuperClone** | `SuperClone` | 1 | Empty cell | Places actor chip on empty cell and captures adjacent enemies | L1 `ConsumesMove` (consumes standard move and ability use) |
| **Unblock** | `Unblock` | 1-2 | Blocked cell (`& ~VoidSquares`) | Clears blocked cell, making it empty (works on opening blocks too) | L1 `EndsTurn`, L2 `Free` |
| **Bomb** | `Bomb` | 1-3 | Enemy chip | Removes enemy chip, leaving cell empty; no footprints or compensation | L1 `EndsTurn`, L2 `FreeForbiddenLanding` (crater forbidden for caster's moves this turn), L3 `Free` |

### 1.2 Rule Decisions (D1 - D12)

- **D1, Self-stranding & turn-local restrictions:** An ability may leave either side without a standard move, ending the game immediately under the no-pass rule (`AttaxRules.AdjudicateResult(state)` ignores turn-local `FrozenSourcesThisTurn` / `ForbiddenLandingsThisTurn`). Only when non-terminal and the turn did not already advance, if a turn-local restriction eliminates all legal standard moves for the active side (`!AttaxRules.HasAnyLegalMoveRestricted`), the engine auto-passes (`AutoPassed = true`, `TurnAdvanced = true`, clearing restrictions). `AttaxGame.WouldEndGame` allows UI warning beforehand.
- **D2, Credits never create moves:** A player lacking a standard clone/jump cannot bypass game termination even if they have enough credits for Spawn, SuperClone, or SuperJump. `AttaxRules.HasAnyLegalMove` strictly checks clone/jump availability; `AdjudicateResult` uses `state.WinRule` (`Chips` or `ChipsPlusFootprints`), while raw `AtaxxAIEngine` (`LocalGameRunner.NewReferee()`) evaluates strictly by chip count (`AttaxRules.GetTerminalResult`).
- **D3, Terminal state is global:** `AttaxRules.AdjudicateResult` evaluates both sides regardless of whose turn it is, and runs after every accepted action (including free actions).
- **D4, Initial result:** `EngineStateFactory.NewGame` (server) immediately evaluates terminal state.
- **D5, Double Move edge case:** After the first move of Double Move, global terminal check applies. If the actor has no moves left for the second move, the game terminates.
- **D6, Strictly one ability per turn:** `turn.maxAbilityActionsPerTurn = 1`. Every cast ability (including Double Move and all levels of Spawn) consumes that sole ability use.
- **D7, Opening blocks are normal blocks:** Unblock can clear blocks placed during the opening (never `VoidSquares`).
- **D8, Footprints:** Only jumps (d=2) and SuperJumps (d>=3) leave footprints on vacated source cells. Landing on an enemy footprint awards 5 credits to the lander and clears it; landing on an own footprint clears it with no reward. Wall clears footprints. Footprints only exist on empty playable cells and never on chips.
- **D9, Credits and restrictions are not in the Zobrist hash:** Credits, footprints, and turn-local restrictions (`FrozenSourcesThisTurn`, `ForbiddenLandingsThisTurn`) do not alter the Zobrist hash. Desync detection across network/persistence is handled by `GameState.ComputeFingerprint()`.
- **D10, Shipped credit cap 300:** Balances are capped at `maxCredits: 300`. Rewards are clamped to remaining capacity under 300. Spending occurs before rewards, creating capacity.
- **D11, Spawn levels and turn flow:** Spawn clears/rewards any footprint at the destination and increments `ActionCount` and `AbilityActionsUsedThisTurn`. Level 1 ends the turn (`EndsTurn`), level 2 is free while freezing the spawned chip for the rest of the turn (`FreeFrozenPlaced`, auto-passing if no other move exists), and level 3 is `Free` (leaving `MovesRemainingThisTurn`, `SideToMove`, `TurnNumber`, and the side hash key unchanged unless auto-passed).
- **D12, Opening verification:** `EngineInfo.OpeningAlgorithmId = "zoned-system-random-v1"`. `EngineInfo.VerifyOpeningCompatibility(out string error)` provides self-check verification against golden vectors. `EngineInfo.RulesRevision` is `2`.

### 1.3 AI Ability Decision

AI ability usage is driven by `AbilityDecider` in `Attax.Core` (`Abilities/`); details in `AI_ABILITY_TACTICS.md`.
- Config: `ai-ability-config.json`, explicit `schemaVersion: 2`: `aiAbilitiesEnabled`, `creditValueChips`, `minGainChips`, `maxCandidatesVerified`, `verifyDepthOffset`, `savingEnabled`.
- Hybrid decision: per-ability tacticians propose, the engine search verifies on a cloned state, and a value gate (`net = gain in chips - price x creditValueChips >= minGainChips`) accepts. No random chance or random ability pick; the same input gives the same decision.
- Search root filters `ExcludedSources` / `ExcludedDestinations` keep frozen chips and forbidden landings out of the root; `GetBestMove` and training logs are unchanged.
- `MeasureValues` supplies calibration samples without mutating live state.
- Bots (`AgentTurnPlanner`) use the bot's own search engine; non-search players never cast abilities.
