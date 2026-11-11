# Engine Rules API Reference (`AttaxGame`)

**Status:** current-state reference, not an implementation request. Verified against `Attax.Core.Utils` at engine commit `043d567` (2026-11-03).
**Owner:** this repository (`AttaxSimulator`). The source code is authoritative. If this document and the code disagree, the code wins and this document must be fixed.
**Supersedes:** the former implementation-request specification of the same name in the server repository (`merge-conflict-server`), which described the abilities as not yet implemented. That text remains in the server repository's Git history.

Related documents: [engineReadme.md](engineReadme.md) (engine overview and rule decisions), [AGENTS.md](AGENTS.md) (repository boundaries).

## 1. Purpose and consumers

`Attax.Core.Utils` holds the shared, dependency-free rules layer. One facade, `AttaxGame`, owns move legality, the nine abilities, the credit economy, footprints, turn flow, and terminal evaluation. All hosts call the same code, so they cannot disagree about legality or cost.

| Consumer | How it uses the layer |
|---|---|
| Authoritative server (`merge-conflict-server`) | References `Attax.Core.Utils` only (never `Attax.Core`). Creates games with `AttaxGame.NewGame` (`Attax.Server.Infrastructure/Games/InitialGameFactory.cs`) and applies player actions with `AttaxGame.TryApply` (`Attax.Server.Infrastructure/Gameplay/GameCommandService.cs`). Persisted games are rebuilt with `GameState.Restore` and checked with the fingerprints in section 8. |
| Unity client (`AttaxUnity`) | Receives a synchronized copy of `Attax.Core.Utils` and `Attax.Core`. Presentation, animation, and UI stay in Unity. This document does not re-audit the Unity code. |
| Bots and console tools | `UserSimulator/Attax.Agents/AgentTurnPlanner.cs` asks `AiAbilityPolicy` first and falls back to the engine search. |

Out of scope for this layer: networking, persistence, UI, AI search, evaluation, ONNX, and training. The search never reads credits, footprints, or ability state, and it only proposes clone and jump moves.

## 2. Layout and constraints

Source: `Attax.Core.Utils/Rules/` (namespace `Attax.Core.Utils`). Public types: `AttaxGame`, `GameState`, `GameAction`, `ActionKind`, `ActionError`, `ActionOutcome`, `AbilityKind`, `StartOptions`, `StartPosition`, `EngineInfo`, `GameRulesConfig` and its group classes, `AiAbilityConfig`, `AiAbilityOptions`, `AiAbilityChoice`, `AiAbilityPolicy`.

Low-level board primitives live in `AttaxRules` (`Attax.Core.Utils/AttaxRules.cs`): `MakeMoveMasks`, `TryPlaceWall`, `TryUnblock`, `TryRemoveEnemyChip`, `TryPlaceChip`, `TryConvertEnemyChip`. They maintain the Zobrist hash incrementally and never touch credits or footprints.

Constraints that keep the folder copyable into Unity:

- No JSON library in `Attax.Core.Utils`. Configuration classes are plain `[Serializable]` data with public fields; each host parses the JSON itself and must read the complete file.
- ZLinq is the only package reference (used by `StartPosition`).
- No `global using`; C# 7.3-compatible syntax; no `Span<T>` in public signatures.
- Credits, footprints, and turn counters live in `GameState`, never in `BitboardState` and never in the Zobrist hash.
- Enum numeric values are persisted and sent over the wire. Append new members; never renumber.

## 3. Types

```csharp
enum AbilityKind { None = 0, SuperJump = 1, Convert = 2, Block = 3, Wall = 4, DoubleMove = 5,
                   Spawn = 6, SuperClone = 7, Unblock = 8, Bomb = 9 }

enum ActionKind  { Move = 0, Convert = 1, Block = 2, Wall = 3, DoubleMove = 4,
                   Spawn = 5, SuperClone = 6, Unblock = 7, Bomb = 8 }
```

`ActionKind` equals `AbilityKind - 1` for every targeted ability (the AI policy relies on this).

`GameAction` carries `Kind`, a `Move` (only for `ActionKind.Move`), and `X`, `Y` for every other targeted kind. Factories: `GameAction.MoveAction(Move)`, `GameAction.Targeted(ActionKind, x, y)`, `GameAction.DoubleMoveAction()`.

`ActionError` values (stable):

| Value | Name | Meaning |
|---|---|---|
| 0 | `None` | Accepted |
| 1 | `GameOver` | Result is not `NotOver` |
| 2 | `NotYourTurn` | Actor is not Red/Blue or not the side to move |
| 3 | `OutOfBounds` | A coordinate is outside 0..6 |
| 4 | `NotYourPiece` | Move source is not an own chip |
| 5 | `DestinationNotEmpty` | Move, Wall, Spawn, or SuperClone target is occupied or blocked |
| 6 | `SameSquare` | Move source equals destination |
| 7 | `IllegalDistance` | Reserved; `Validate` does not currently return it |
| 8 | `NotEnemyChip` | Convert, Block, or Bomb target is not an enemy chip |
| 9 | `NotBlockedCell` | Unblock target is not blocked |
| 10 | `InsufficientCredits` | Balance is below the price |
| 11 | `AbilityLimitReachedThisTurn` | Ability use already spent this turn |
| 12 | `DoubleMoveAlreadyActive` | `MovesRemainingThisTurn` is already above 1 |
| 13 | `UnknownAction` | `ActionKind` outside 0..8 |
| 14 | `AbilityDisabled` | Disabled for this player, economy off, or price 0 |
| 15 | `CounterOverflow` | `ActionCount` or `TurnNumber` would overflow |

`ActionOutcome` reports `Kind`, `Ability`, `Actor`, the masks `FlippedMask`, `RemovedMask`, `PlacedMask`, `BlockedAddedMask`, `BlockedClearedMask`, `FootprintPlacedMask`, `FootprintClearedMask`, and `CreditsSpent`, `ActorCreditsGained` (footprint reward actually granted), `VictimCreditsGained` (capture reward actually granted), `TurnAdvanced`, `Result`.

`StartOptions { BlockedCells (0..12), Seed, FirstPlayer = Red }` has a three-argument constructor `StartOptions(blockedCells, seed, firstPlayer = Red)`.

## 4. `AttaxGame` API

| Member | Behavior |
|---|---|
| `NewGame(GameRulesConfig, StartOptions)` | Validates rules (throws `ArgumentException` listing every problem), validates `FirstPlayer` and `BlockedCells`, clones the rules into the state, builds the opening with `StartPosition.Create`, sets both balances to `startingCredits` (0 when the economy is off), counters to 0, one move remaining, and evaluates `Result`. |
| `Validate(state, actor, in action)` | Pure, no mutation. Returns the first applicable `ActionError` (section 6). |
| `TryApply(state, actor, in action, out outcome)` | Validates first. On error the state is untouched and `outcome` is default. On success it applies the action (section 7). |
| `AbilityOf(in action)` | `None` for clone/jump, `SuperJump` for a `Move` with Chebyshev distance >= 3, otherwise the matching kind. |
| `GetAbilityCost(state, kind)` | Configured price; 0 means disabled. |
| `CanAfford(state, side, kind)` | Economy on, price above 0, and balance at least the price. |
| `GetMoveTargets(state, x, y, includeSuperJump)` | Empty destinations for the side-to-move chip at `(x, y)`; 0 otherwise. Does not check price. |
| `GetAbilityTargets(state, kind)` and `(state, side, kind)` | Valid target cells now; 0 when disabled, unaffordable, limit reached, or game over. `DoubleMove` always returns 0. |
| `CanUseAbility(state, kind)` and `(state, side, kind)` | Enabled, affordable, limit not reached, and (for `DoubleMove`) exactly one move remaining or (otherwise) at least one target. |
| `GetAvailableAbilities(state)` and `(state, side)` | Bit mask with bit `1 << (int)kind` per usable ability. |
| `HasStandardMove(state, side)` | Clone or jump available; ignores credits. |
| `GetScore(state, side)` | Chip count. |
| `WouldEndGame(state, actor, in action)` | False if invalid; otherwise simulates on a clone and reports whether the result is terminal. |

Side-aware overloads (`GetAbilityTargets`, `CanUseAbility`, `GetAvailableAbilities` with an explicit `side`) exist in addition to the side-to-move overloads.

## 5. Rules model

### 5.1 The nine abilities

An ability is usable when the player's balance covers its price. There are no per-ability charges. Strictly one ability use per turn (`turn.maxAbilityActionsPerTurn` must be 1). A normal clone or jump costs nothing and does not count; a Super Jump counts.

| Ability | `ActionKind` | Target | Effect | Turn effect |
|---|---|---|---|---|
| Super Jump | `Move`, distance >= 3 | Empty cell | Jump; source vacated and gets a footprint; adjacent enemies flipped | Ends the move and uses the ability |
| Chief (`Convert`) | `Convert` | Enemy chip | Chip becomes the actor's; no neighbors flipped | Free action |
| Block | `Block` | Enemy chip | Chip removed, cell becomes blocked | Free action |
| Wall | `Wall` | Empty cell | Cell becomes blocked; a footprint there is removed | Free action |
| Double Move | `DoubleMove` | None | `MovesRemainingThisTurn` becomes 2 | Free action; uses the ability |
| Spawn | `Spawn` | Empty cell | Own chip placed; no flips | Does not end the move; uses the ability; turn does not pass |
| Super Clone | `SuperClone` | Empty cell | Own chip placed; adjacent enemies flipped | Ends the move and uses the ability |
| Unblock | `Unblock` | Blocked cell | Cell becomes empty (also opening blocks) | Free action |
| Bomb | `Bomb` | Enemy chip | Chip removed; cell empty; no footprint | Free action |

"Chief" is the game-facing name; the code identifier is `Convert`.

### 5.2 Credits and footprints

- Names in the engine and on the wire are neutral: credits and footprints. Unity display strings ("ByteCode", "Bug", "Review") never appear in engine code or JSON.
- Balances are `int` in `[0, economy.maxCredits]`. Rewards are clamped to remaining capacity (long intermediates). Credits never affect who wins.
- Capture reward: the victim earns `captureReward` per flipped chip, from Move and Super Clone only. Chief, Block, and Bomb pay nothing.
- Footprints: a jump (distance 2) or Super Jump leaves the actor's footprint on the vacated source cell when the economy is enabled; clones leave none. A cell holds at most one footprint, only on empty cells.
- Landing (destination of Move, Spawn, or Super Clone): an enemy footprint is removed and the lander earns `footprintReward`; an own footprint is removed with no reward. Wall removes any footprint on its cell.
- Rewards stack: one action can pay the actor the footprint reward and the victim the capture reward.
- Affordability is checked against the balance before the action, so an ability cannot be paid with credits that the same action earns.
- With `economy.enabled = false`: balances stay 0, no footprints exist, and every ability reports `AbilityDisabled`.

### 5.3 Rule decisions

- D1: free actions may strand a side; the game ends immediately and `WouldEndGame` lets a UI warn first.
- D2: credits never create moves; a side without a standard clone/jump ends the game immediately even with credits for Super Jump, Spawn, or Super Clone, and the winner is decided strictly by chip count (equal chips is a draw).
- D3: terminal state is global and evaluated after every accepted action.
- D4: `NewGame` evaluates the initial result.
- D5: if Double Move leaves the actor without a standard move for the second move, the game ends; the facade does not skip the move.
- D6: one ability per turn; Double Move and Spawn consume that use.
- D7: opening blocks are ordinary blocks and can be cleared by Unblock.
- D8: footprint and reward rules as in 5.2.
- D9: credits and footprints are not in the Zobrist hash; desync detection uses fingerprints (section 8).

## 6. Validation order

`Validate` is deterministic; target errors win over resource errors.

1. `GameOver` if the game has ended.
2. `NotYourTurn` if the actor is not Red/Blue or not the side to move.
3. `UnknownAction` if `Kind` is outside 0..8.
4. Per kind:
   - `Move`: `OutOfBounds`, `SameSquare`, `NotYourPiece`, `DestinationNotEmpty`. Distance 1 or 2 needs no resource gate.
   - `Convert`, `Block`, `Bomb`: `OutOfBounds`, `NotEnemyChip`.
   - `Wall`, `Spawn`, `SuperClone`: `OutOfBounds`, `DestinationNotEmpty`.
   - `Unblock`: `OutOfBounds`, `NotBlockedCell`.
   - `DoubleMove`: `DoubleMoveAlreadyActive` when `MovesRemainingThisTurn > 1`.
5. Resource gate when `AbilityOf` is not `None`, in this order: `AbilityDisabled` (disabled for the player, economy off, or price 0), `InsufficientCredits`, `AbilityLimitReachedThisTurn`.
6. `CounterOverflow` if `ActionCount` is `int.MaxValue`, or a turn-passing action would overflow `TurnNumber`.

## 7. Application order and turn flow

`TryApply` on success, in order:

1. Deduct the ability price, increment `AbilityActionsUsedThisTurn`, record `CreditsSpent`.
2. Apply the board change through the `AttaxRules` primitive; fill the outcome masks.
3. For a jump or Super Jump, place the actor's footprint on the source cell.
4. Landing resolution for Move, Spawn, and Super Clone (footprint removal and reward).
5. Capture reward to the victim for flipped chips.
6. `ActionCount++`.
7. Turn handling, then terminal evaluation (`state.Result`, `outcome.Result`).

Turn handling:

- `Move` and `SuperClone` decrement `MovesRemainingThisTurn`. At 0 the turn passes: `SideToMove` flips, `TurnNumber++`, one move remaining, ability use reset, `TurnAdvanced = true`. Above 0 (Double Move active) the same player continues and `TurnAdvanced = false`.
- All other actions (including Spawn) never change `SideToMove`, `TurnNumber`, or `MovesRemainingThisTurn`, and `TurnAdvanced = false`.
- Side-to-move hash key: `AttaxRules.MakeMoveMasks` always toggles it, and `TryPlaceChip` and the free-action primitives never do. The facade corrects this so the key is toggled exactly once per turn change: after a `Move` that does not pass the turn it toggles again; after a `SuperClone` that passes the turn it toggles once.

## 8. State, restore, and fingerprints

`GameState` fields: `Board` (`BitboardState`), `RedFootprints`, `BlueFootprints`, `RedCredits`, `BlueCredits`, `SideToMove`, `TurnNumber`, `ActionCount`, `MovesRemainingThisTurn` (1, or 2 after Double Move), `AbilityActionsUsedThisTurn` (0 or 1), `Result`, `RulesFingerprint`. Methods: `ExportRules()` (deep copy), `Clone()`, `GetCredits`, `IsHashConsistent()`, `AreInvariantsValid()`, `ComputeFingerprint()`.

`GameState.Restore(red, blue, blocked, redFootprints, blueFootprints, sideToMove, redCredits, blueCredits, turnNumber, actionCount, movesRemainingThisTurn, abilityActionsUsedThisTurn, rules)` validates strictly and throws `ArgumentException` for: invalid rules, bits outside the 7x7 board, overlapping chip masks, footprints on occupied or blocked cells, overlapping footprints, invalid side to move, negative counters, `turnNumber > actionCount`, ability uses outside 0..1, moves remaining outside 1..2, moves remaining 2 without an ability use, a non-initial counter set when `actionCount` is 0, credits outside `[0, maxCredits]`, and (for a disabled economy) any credits, footprints, ability use, or extra move. It recomputes the Zobrist hash and `Result`.

Fingerprints use 64-bit FNV-1a (offset basis `14695981039346656037`, prime `1099511628211`), feeding each value as 8 little-endian bytes (`int` and `bool` widened first).

- `GameRulesConfig.Fingerprint()` feed order: `schemaVersion`, `economy.enabled`, `startingCredits`, `maxCredits`, `captureReward`, `footprintReward`, the nine costs in the order `superJump, convert, block, wall, doubleMove, spawn, superClone, unblock, bomb`, `turn.maxAbilityActionsPerTurn`, the nine Red toggles, the nine Blue toggles. `abilityTexts` is not hashed.
- `GameState.ComputeFingerprint()` feed order: Red mask, Blue mask, blocked mask, `ZobristHash`, `SideToMove`, `RedFootprints`, `BlueFootprints`, `RedCredits`, `BlueCredits`, `TurnNumber`, `ActionCount`, `MovesRemainingThisTurn`, `AbilityActionsUsedThisTurn`, `RulesFingerprint`.

Hosts format masks, hashes, and fingerprints as 18-character hex `0x{value:X16}` on the wire. The shipped `game-rules.json` fingerprint changes whenever a cost, reward, cap, or toggle changes; compute it with `GameRulesConfig.Fingerprint()` instead of copying an old value.

## 9. Rules configuration: `game-rules.json`

Source: `Attax.Core.Utils/Rules/game-rules.json`, shipped as a content item so it reaches consumer build and publish output. Classes: `GameRulesConfig` (`schemaVersion`, `economy`, `turn`, `playerAbilities`, `abilityTexts`), `EconomyRules`, `AbilityCosts`, `TurnRules`, `AbilityToggles`, `PlayerAbilitiesConfig`, `AbilityTexts`/`AbilityText`.

Shipped values (check the file for the current numbers before relying on them):

| Field | Shipped value |
|---|---|
| `schemaVersion` | 1 |
| `economy.enabled` | true |
| `economy.startingCredits` | 0 |
| `economy.maxCredits` | 3000 |
| `economy.captureReward` | 10 |
| `economy.footprintReward` | 5 |
| `economy.abilityCosts.*` | 10 for all nine abilities |
| `turn.maxAbilityActionsPerTurn` | 1 |
| `playerAbilities.red` / `.blue` | all nine abilities enabled |

`abilityTexts` holds a display `name`, `description`, and `instruction` per ability (`instruction` is optional only for `doubleMove`). These are presentation strings supplied with the rules; they do not affect legality.

Rules for the classes:

- No code defaults: a freshly constructed `GameRulesConfig` has null groups and fails `Validate()`.
- `Validate()` returns every problem and never throws. It checks the schema version, all groups present, `maxCredits > 0`, `0 <= startingCredits <= maxCredits`, non-negative rewards and costs, `maxAbilityActionsPerTurn == 1`, and complete `abilityTexts`.
- `GetAbilityCost(kind)` returns 0 for `None`, a disabled economy, or a cost of 0 (a 0 cost disables that ability).
- `IsAbilityEnabled(player, kind)` reads the per-player toggles.
- `NewGame` and `Restore` clone the rules, so later changes to the caller's object do not affect a running game. Server-side per-ruleset overlays are merged at the JSON level by the server before deserialization.

## 10. Opening generator

`StartPosition.Standard(firstPlayer = Red)` places Red on squares 0 and 48 and Blue on 6 and 42. `StartPosition.Create(blockedCells, seed, firstPlayer = Red)` adds seeded blocked cells and recomputes the hash for `firstPlayer`. `StartPosition.GenerateBlockedCellPositions(count, board, seed)` (and a `Random` overload) is the single shared implementation; the AI engine and play tools delegate to it.

The algorithm is zoned: `zoneCount = ceil(sqrt(count))`, `zoneSize = 7 / zoneCount` (integer division, so some zones do not cover the whole board), zones shuffled with `OrderBy(rnd.Next())`, then up to 1000 attempts picking `rnd.Next(minX, maxX)` and `rnd.Next(minY, maxY)` per zone. This output is intentionally stable for a given seed.

`EngineInfo.OpeningAlgorithmId` is `"zoned-system-random-v1"`. `EngineInfo.VerifyOpeningCompatibility(out string error)` checks four golden vectors (seed/count pairs `(0,3)`, `(42,6)`, `(123,1)`, `(999,12)`) so a runtime change in `System.Random` would be noticed. `EngineInfo.RulesRevision` is `1`; it is informational (logs and diagnostics), and client compatibility is enforced by the server's client-version policy.

## 11. AI ability policy

Files: `AiAbilityConfig.cs`, `AiAbilityOptions.cs`, `AiAbilityChoice.cs`, `AiAbilityPolicy.cs`, `ai-ability-config.json`. The policy is separate from the search; it runs before the normal search on the AI's turn and owns its own seeded `System.Random`, so it never consumes the engine's random streams.

`ai-ability-config.json` (shipped values):

| Field | Meaning | Shipped |
|---|---|---|
| `schemaVersion` | Must be 1 | 1 |
| `aiAbilitiesEnabled` | Master switch from the file | false |
| `useChancePercent` | 0..100 chance per AI turn to consider an ability | 35 |
| `creditReserve` | Balance that must remain after paying | 0 |
| `maxUsesPerGame` | Cap per game; 0 = unlimited | 0 |
| `safetyFilter` | Reject a candidate that ends the game as an AI loss | true |
| `targetSelection` | `random`, `scored`, or `engine`; empty or null behaves like `random` | `engine` |

`AiAbilityOptions.Resolve(file, hostOverride)` returns `hostOverride ?? file.aiAbilitiesEnabled`. `AiAbilityPolicy(config, seed, enabled)` validates the config (throws on errors) and exposes `Enabled`, `UsesThisGame`, `ResetGame()`, `TryChoose(state, side, out choice)`, `RecordAppliedAbility(state, in outcome)`, and a `TargetEvaluator` property.

`TryChoose` behavior:

1. Returns false when disabled, the game is over, `side` is not the side to move, the economy is off, or `maxUsesPerGame` is reached.
2. Allows one decision per `TurnNumber` and side. Repeated calls in the same turn (including a Double Move continuation) return false without a new roll.
3. Rolls `useChancePercent`.
4. Builds candidates that pass `AttaxGame.CanUseAbility` and keep the balance at or above `creditReserve`; picks one uniformly at random, up to 4 attempts, removing rejected abilities.
5. Target choice depends on `targetSelection`:
   - `random`: a random valid target (for Super Jump, a random own chip at distance >= 3).
   - `scored` or `engine`: every valid action is simulated on a clone, scored by board evaluation, and the best is taken with a seeded random tie-break. `scored` uses a material difference; `engine` uses `TargetEvaluator` when one is set and otherwise falls back to the material difference. For free actions the best follow-up clone or jump is also scored.
6. With `safetyFilter`, a candidate that results in an immediate AI loss is rejected.
7. `EndsTurn` is true for Super Jump and Super Clone only.

The ability is chosen uniformly among usable abilities; target selection is where the policy can use evaluation. The policy never returns an action that `Validate` rejects.

`RecordAppliedAbility` must be called by the host right after `TryApply` succeeds for the chosen action; it matches the pending choice and increments `UsesThisGame` once.

To use the engine's heuristic evaluator, `Attax.Core/AiAbilityPolicyExtensions.cs` provides `UseEngineEvaluator(EngineParams)` and `UseEngineEvaluator(IValueEvaluator)`. This extension lives in `Attax.Core` because `Attax.Core.Utils` must not reference it.

Host flow (see `UserSimulator/Attax.Agents/AgentTurnPlanner.cs`): if the policy is enabled and `TryChoose` succeeds, apply `choice.Action` with `TryApply` and call `RecordAppliedAbility`; for a free ability the host then runs the normal search on `state.Board`, and for an `EndsTurn` ability it skips the search. If no ability is chosen, run the search and apply `GameAction.MoveAction(move)`. Self-play, training, and arena runs construct no policy.

## 12. Source and test map

| Topic | Source | Tests (`Attax.Core.Tests/`) |
|---|---|---|
| Facade, validation, turn flow, parity | `Rules/AttaxGame.cs`, `AttaxRules.cs` | `AttaxGameTests.cs`, `AttaxGameAbilityTests.cs` |
| Economy and caps | `Rules/AttaxGame.cs` | `AttaxGameEconomyTests.cs` |
| Invariants, allocation | `Rules/GameState.cs` | `AttaxGameInvariantAndAllocationTests.cs` |
| Rules config and fingerprints | `Rules/GameRulesConfig.cs`, `Rules/game-rules.json` | `GameRulesConfigTests.cs` |
| Opening generator | `Rules/StartPosition.cs`, `Rules/EngineInfo.cs` | `StartPositionTests.cs`, `StartBoardTests.cs` |
| Compatibility and golden fixtures | n/a | `CompatibilityTests.cs`, `RefactorGoldenTests.cs` |
| AI ability policy | `Rules/AiAbilityPolicy.cs`, `Attax.Core/AiAbilityPolicyExtensions.cs` | `AiAbilityPolicyTests.cs` |
| Bot orchestration | `UserSimulator/Attax.Agents/AgentTurnPlanner.cs` | `UserSimulator/Attax.UserSimulator.Tests/` |

This reference was written from source reading. No build or test run was performed for it; run the commands in [AGENTS.md](AGENTS.md#verification) to verify the engine.

## 13. Maintenance

- Change this file in the same commit as any change to ability behavior, costs, validation order, enum values, fingerprint order, the opening algorithm, or the AI policy contract.
- A behavioral rules change bumps `EngineInfo.RulesRevision`, and a change to a persisted or wire value needs a coordinated server and Unity release.
- Adding an ability means: append an `AbilityKind` and `ActionKind` value, add a price field (bump `schemaVersion` and the fingerprint order), add validation and apply blocks, a `GetAbilityTargets` case, tests, and a `RulesRevision` bump.
- The server records engine discrepancies in its `ENGINE_SERVER_FOLLOWUPS.md`; engine fixes belong here.
