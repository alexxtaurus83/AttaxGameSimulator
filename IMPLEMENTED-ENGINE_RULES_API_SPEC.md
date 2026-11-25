# Engine Rules API Reference (`AttaxGame`)

**Status:** current-state reference, not an implementation request. Updated for v2 ability levels, allowance, turn-local restrictions, and board-scaled pricing (`schemaVersion = 2`, `EngineInfo.RulesRevision = 2`).
**Owner:** this repository (`AttaxSimulator`); canonical engine implementation lives in `..\AttaxShared\src\Attax.Core.Utils`. The source code is authoritative. If this document and the code disagree, the code wins and this document must be fixed.
**Supersedes:** the former implementation-request specification of the same name in the server repository (`merge-conflict-server`), which described the abilities as not yet implemented. That text remains in the server repository's Git history.

Related documents: [engineReadme.md](engineReadme.md) (engine overview and rule decisions), [AGENTS.md](AGENTS.md) (repository boundaries).

## 1. Purpose and consumers

`Attax.Core.Utils` holds the shared, dependency-free rules layer. One facade, `AttaxGame`, owns move legality, the nine abilities, the credit economy, footprints, turn flow, and terminal evaluation. All hosts call the same code, so they cannot disagree about legality or cost.

| Consumer | How it uses the layer |
|---|---|
| Authoritative server (`merge-conflict-server`) | References `Attax.Core.Utils` only (never `Attax.Core`). Creates games with `EngineStateFactory.NewGame` (`Attax.Server.Application/Games`, composed from `StartPosition.Create` and `GameState.CreateInitial`; called from `Attax.Server.Infrastructure/Games/InitialGameFactory.cs`) and applies player actions with `AttaxGame.TryApply` (`Attax.Server.Infrastructure/Gameplay/GameCommandService.cs`). Persisted games are rebuilt with `GameState.Restore` and checked with the fingerprints in section 8. |
| Unity client (`AttaxUnity`) | Receives a synchronized copy of `Attax.Core.Utils` and `Attax.Core`. Presentation, animation, and UI stay in Unity. This document does not re-audit the Unity code. |
| Bots and console tools | `UserSimulator/Attax.Agents/AgentTurnPlanner.cs` asks `AbilityDecider` first (search players only) and falls back to the engine search with the turn restriction masks. |

Out of scope for this layer: networking, persistence, UI, AI search, evaluation, ONNX, and training. The search never reads credits, footprints, or ability state, and it only proposes clone and jump moves.

## 2. Layout and constraints

Source: `Attax.Core.Utils/Rules/` (namespace `Attax.Core.Utils`). Public types: `AttaxGame`, `GameState`, `GameAction`, `ActionKind`, `ActionError`, `ActionOutcome`, `AbilityKind`, `AbilityLevelEffect`, `AbilityAllowance`, `BoardScaling`, `WinRule`, `StartPosition`, `EngineInfo`, `GameRulesConfig` and its group classes (`EconomyRules`, `AbilityCosts`, `TurnRules`), `AiAbilityConfig`, `AiAbilityOptions`. The AI decision (`AbilityDecider` and the tacticians) lives in `Attax.Core/Abilities/`. Internal per-ability rule dispatch lives in `Rules/Abilities/IAbilityRule.cs` and `AbilityRuleRegistry.cs` (`WallRule`, `BlockRule`, `UnblockRule`, `SpawnRule`, `ConvertRule`, `BombRule`, `SuperJumpRule`, `DoubleMoveRule`, `SuperCloneRule`). Namespace `Attax.Core.Utils.Validation` holds the dependency-free shared rules `DisplayNameValidator`, `DisplayNameCheck` and `RoomCodeValidator`. `NewGame` and `StartOptions` are server code (`Attax.Server.Application.Games`: `EngineStateFactory`, `StartOptions`).

Low-level board primitives live in `AttaxRules` (`Attax.Core.Utils/AttaxRules.cs`): `MakeMoveMasks`, `TryPlaceWall`, `TryUnblock`, `TryRemoveEnemyChip`, `TryPlaceChip`, `TryConvertEnemyChip`, `HasAnyLegalMove`, `AdjudicateResult`, `GetScore`. They maintain the Zobrist hash incrementally and never touch credits or footprints. (`AbilityTexts`, `AbilityToggles`, and `PlayerAbilitiesConfig` were removed in v2.)

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

enum AbilityLevelEffect { EndsTurn, FreeFrozenPlaced, FreeForbiddenLanding, Free, ConsumesMove, GrantsSecondMove }
```

`AbilityRuleRegistry` maps `AbilityKind` and `ActionKind` to each `IAbilityRule` (`SuperJump` uses `ActionKind.Move` with distance >= 3 and `Level = 1`).

`GameAction` carries `Kind`, a `Move` (only for `ActionKind.Move`), `X`, `Y` for targeted actions, and `Level` (`0` for standard `Move`, `1..MaxLevel` for abilities including `SuperJump`). Factories: `GameAction.MoveAction(Move)`, `GameAction.SuperJump(Move)`, `GameAction.Targeted(ActionKind, x, y, level)`, `GameAction.DoubleMoveAction()`.

`ActionError` values (stable):

| Value | Name | Meaning |
|---|---|---|
| 0 | `None` | Accepted |
| 1 | `GameOver` | Result is not `NotOver` |
| 2 | `NotYourTurn` | Actor is not Red/Blue or not the side to move |
| 3 | `OutOfBounds` | A coordinate is outside 0..6 |
| 4 | `NotYourPiece` | Move source is not an own chip |
| 5 | `DestinationNotEmpty` | Move, Wall, Spawn, or SuperClone target is occupied, blocked, or void |
| 6 | `SameSquare` | Move source equals destination |
| 7 | `IllegalDistance` | Reserved; `Validate` does not currently return it |
| 8 | `NotEnemyChip` | Convert, Block, or Bomb target is not an enemy chip |
| 9 | `NotBlockedCell` | Unblock target is not a playable blocked cell |
| 10 | `InsufficientCredits` | Balance is below the board-scaled price |
| 11 | `AbilityLimitReachedThisTurn` | Ability use already spent this turn |
| 12 | `DoubleMoveAlreadyActive` | `MovesRemainingThisTurn` is already above 1 |
| 13 | `UnknownAction` | `ActionKind` outside 0..8 |
| 14 | `AbilityDisabled` | Economy off or resolved price 0 |
| 15 | `CounterOverflow` | `ActionCount` or `TurnNumber` would overflow |
| 16 | `InvalidAbilityLevel` | Non-zero level on standard Move, or ability level outside `1..MaxLevel` |
| 17 | `AbilityLevelNotAllowed` | Requested level exceeds `rules.GetMaxLevel(actor, kind)` |
| 18 | `SourceFrozenThisTurn` | Move source is in `FrozenSourcesThisTurn` |
| 19 | `LandingForbiddenThisTurn` | Move destination is in `ForbiddenLandingsThisTurn` |

`ActionOutcome` reports `Kind`, `Ability`, `Level`, `Actor`, the masks `FlippedMask`, `RemovedMask`, `PlacedMask`, `BlockedAddedMask`, `BlockedClearedMask`, `FootprintPlacedMask`, `FootprintClearedMask`, `CreditsSpent`, `ActorCreditsGained` (footprint reward actually granted), `VictimCreditsGained` (capture reward actually granted), `TurnAdvanced`, `AutoPassed`, `FrozenMask`, `ForbiddenLandingMask`, and `Result`.

The server-side `StartOptions { BlockedCells (0..12), Seed, FirstPlayer = Red }` (`Attax.Server.Application.Games`) has a three-argument constructor `StartOptions(blockedCells, seed, firstPlayer = Red)`; it is no longer part of Core.Utils.

## 4. `AttaxGame` API

| Member | Behavior |
|---|---|
| `EngineStateFactory.NewGame(GameRulesConfig, StartOptions)` (server, not `AttaxGame`) | Validates rules (throws `ArgumentException` listing every problem), validates `FirstPlayer` and `BlockedCells`, builds the opening with `StartPosition.Create`, then calls `GameState.Restore`, which clones the rules, computes `PlayableCells` and cached board-scaled prices, sets both balances to `startingCredits` (0 when the economy is off), counters to 0, one move remaining, and evaluates `Result`. |
| `Validate(state, actor, in action)` | Pure, no mutation. Returns the first applicable `ActionError` (section 6). |
| `TryApply(state, actor, in action, out outcome)` | Validates first. On error the state is untouched and `outcome` is default. On success it applies the action (section 7). |
| `AbilityOf(in action)` (internal) | `None` for clone/jump, `SuperJump` for a `Move` with Chebyshev distance >= 3, otherwise the matching kind. |
| `GameRulesConfig.GetAbilityCost(kind, level)` | Base unscaled price from config (`0` when economy off or invalid level). |
| `GameState.GetAbilityCost(kind, level)` / `AttaxGame.GetAbilityCost(state, kind, level)` | Cached board-scaled price for `state.PlayableCells` (`0` when economy off). `AttaxGame.GetAbilityCost(state, kind)` uses `GetMaxLevel(state.SideToMove, kind)`. |
| `IsLegalStandardMove(state, side, in move)` / `GetStandardMoveTargets(state, side, x, y)` | Restriction-aware standard clone/jump destinations for `(x, y)` (excludes `FrozenSourcesThisTurn` and `ForbiddenLandingsThisTurn` when `side == state.SideToMove`). |
| `GetSuperJumpTargets(state, side, x, y)` | Valid Super Jump destinations for `(x, y)` enforcing `CanUseAbility` and turn-local restriction masks. |
| `GetAbilityTargets(state, kind)` / `(state, side, kind)` / `(state, side, kind, level)` | Valid target cells now; 0 when disabled, unaffordable, disallowed, limit reached, or game over. `DoubleMove` and `SuperJump` return 0 (2/3-arg overloads select `GetMaxLevel(side, kind)`). |
| `GetAbilityEffect(kind, level)` | Returns `AbilityLevelEffect` for `(kind, level)`; throws `ArgumentOutOfRangeException` if invalid. |
| `CanUseAbility(state, kind)` / `(state, side, kind)` / `(state, side, kind, level)` | Enabled, allowed, affordable, limit not reached, and (for `DoubleMove`) `MovesRemainingThisTurn == 1`, or (for `SuperJump`) at least one source has `GetSuperJumpTargets != 0`, or (otherwise) at least one target. |
| `GetAvailableAbilities(state)` and `(state, side)` (internal) | Bit mask with bit `1 << (int)kind` per usable ability (at max allowed level). |
| `AttaxRules.HasAnyLegalMove(board, side)` | Unrestricted clone or jump available; ignores credits and turn-local restrictions. |
| `AttaxRules.AdjudicateResult(state)` | Global terminal evaluation using `state.WinRule` (`Chips` or `ChipsPlusFootprints`); `AttaxRules.GetTerminalResult(board)` remains chip-only for `AtaxxAIEngine`. |
| `GetScore(state, side)` (internal) / `AttaxRules.GetScore(state, side)` | Score according to `state.WinRule` (`Chips` or `ChipsPlusFootprints`). |
| `WouldEndGame(state, actor, in action)` (internal) | False if invalid; otherwise simulates on a clone and reports whether the result is terminal. |

Side-aware and level-aware overloads (`GetAbilityTargets`, `CanUseAbility`, `GetAbilityCost`) exist in addition to the side-to-move overloads.

## 5. Rules model

### 5.1 The nine abilities

An ability is usable at `level` in `1..rules.GetMaxLevel(side, kind)` when the player's balance covers the board-scaled price `state.GetAbilityCost(kind, level)` and `AbilityActionsUsedThisTurn < 1`. There are no per-ability charges. A normal clone or jump (`Level = 0`) costs nothing and does not count; a Super Jump (`Level = 1`) counts.

| Ability | `ActionKind` | `MaxLevel` | Target | Level Effects (`GetEffect`) |
|---|---|---|---|---|
| Super Jump | `Move` (d >= 3, `Level = 1`) | 1 | Empty cell | L1 `ConsumesMove` (vacates source with footprint; flips adjacent enemies) |
| Chief (`Convert`) | `Convert` | 3 | Enemy chip | L1 `EndsTurn`, L2 `FreeFrozenPlaced`, L3 `Free` (converts enemy chip; no flips) |
| Block | `Block` | 2 | Enemy chip | L1 `EndsTurn`, L2 `Free` (removes enemy chip, places block) |
| Wall | `Wall` | 1 | Empty cell | L1 `Free` (places block; clears any footprint) |
| Double Move | `DoubleMove` | 1 | None | L1 `GrantsSecondMove` (`MovesRemainingThisTurn` becomes 2) |
| Spawn | `Spawn` | 3 | Empty cell | L1 `EndsTurn`, L2 `FreeFrozenPlaced`, L3 `Free` (places own chip; no flips) |
| Super Clone | `SuperClone` | 1 | Empty cell | L1 `ConsumesMove` (places own chip; flips adjacent enemies) |
| Unblock | `Unblock` | 2 | Blocked cell (`& ~Void`) | L1 `EndsTurn`, L2 `Free` (clears blocked cell) |
| Bomb | `Bomb` | 3 | Enemy chip | L1 `EndsTurn`, L2 `FreeForbiddenLanding`, L3 `Free` (removes enemy chip; no footprint) |

"Chief" is the game-facing name; the code identifier is `Convert`.

### 5.2 Credits, board scaling, and footprints

- Names in the engine and on the wire are neutral: credits and footprints. Unity display strings ("ByteCode", "Bug", "Review") never appear in engine code or JSON.
- Balances are `int` in `[0, economy.maxCredits]`. Rewards are clamped to remaining capacity (long intermediates). Credits never affect who wins.
- Board scaling (`economy.boardScaling`): `playable = 49 - PopCount(VoidSquares)` computed once at `CreateInitial` / `Restore`. When `economy.enabled` is true, `factor = min(1, max(minFactor, (playable / referencePlayable)^alpha))`, and each level price is `max(previous + roundStep, roundStep * Round(base[level] * factor / roundStep, MidpointRounding.AwayFromZero))`.
- Capture reward: the victim earns `captureReward` per flipped chip, from Move and Super Clone only. Chief, Block, and Bomb pay nothing.
- Footprints: a jump (distance 2) or Super Jump leaves the actor's footprint on the vacated source cell when the economy is enabled; clones leave none. A cell holds at most one footprint, only on empty playable cells.
- Landing (destination of Move, Spawn, or Super Clone): an enemy footprint is removed and the lander earns `footprintReward`; an own footprint is removed with no reward. Wall removes any footprint on its cell.
- Rewards stack: one action can pay the actor the footprint reward and the victim the capture reward.
- Affordability is checked against the balance before the action, so an ability cannot be paid with credits that the same action earns.
- With `economy.enabled = false`: balances stay 0, no footprints exist, all effective ability costs are 0, and every ability reports `AbilityDisabled`.

### 5.3 Rule decisions

- D1: abilities may strand a side; `AttaxRules.AdjudicateResult(state)` ignores turn-local restrictions (`FrozenSourcesThisTurn` / `ForbiddenLandingsThisTurn`) and ends the game immediately. Only when non-terminal and the turn did not already advance, if `!AttaxRules.HasAnyLegalMoveRestricted(...)`, the turn auto-passes (`AutoPassed = true`, `TurnAdvanced = true`, clearing restrictions). `WouldEndGame` lets a UI warn first.
- D2: credits never create moves; a side without a standard clone/jump ends the game immediately even with credits for Super Jump, Spawn, or Super Clone, and the winner is decided by `state.WinRule` (`Chips` or `ChipsPlusFootprints`; equal score is a draw).
- D3: terminal state is global and evaluated after every accepted action.
- D4: `EngineStateFactory.NewGame` (server) evaluates the initial result.
- D5: if Double Move leaves the actor without a standard move for the second move, the game ends; the facade does not skip the move.
- D6: one ability per turn (`maxAbilityActionsPerTurn == 1`); Double Move and all levels of Spawn consume that use.
- D7: opening blocks are ordinary blocks and can be cleared by Unblock (never `VoidSquares`).
- D8: footprint and reward rules as in 5.2.
- D9: credits, footprints, and turn-local restrictions are not in the Zobrist hash; desync detection uses fingerprints (section 8).

## 6. Validation order

`Validate` is deterministic; target errors win over resource errors.

1. `GameOver` if `state.Result != TerminalResult.NotOver`.
2. `NotYourTurn` if the actor is not Red/Blue or not `state.SideToMove`.
3. `UnknownAction` if `RuleOf(action) == null && action.Kind != ActionKind.Move`.
4. Target checks:
   - `Move` (standard or `SuperJump`): `OutOfBounds`, `SameSquare`, `NotYourPiece`, `DestinationNotEmpty`, `SourceFrozenThisTurn`, `LandingForbiddenThisTurn`.
   - `Convert`, `Block`, `Bomb`: `OutOfBounds`, `NotEnemyChip`.
   - `Wall`, `Spawn`, `SuperClone`: `OutOfBounds`, `DestinationNotEmpty`.
   - `Unblock`: `OutOfBounds`, `NotBlockedCell`.
   - `DoubleMove`: `DoubleMoveAlreadyActive` when `MovesRemainingThisTurn != 1`.
5. Level and resource gate:
   - Standard `Move`: `InvalidAbilityLevel` if `action.Level != 0`.
   - Any ability (`RuleOf(action) != null`): `InvalidAbilityLevel` (`level < 1 || level > MaxLevel`), `AbilityLevelNotAllowed` (`level > rules.GetMaxLevel(actor, kind)`), `AbilityDisabled` (`cost == 0`, including economy off), `InsufficientCredits`, `AbilityLimitReachedThisTurn`.
6. `CounterOverflow` if `ActionCount == int.MaxValue` or (`TurnNumber == int.MaxValue` and the action can advance the turn).

## 7. Application order and turn flow

`TryApply` on success, in order:

1. If ability: deduct `state.GetAbilityCost(ability, action.Level)`, increment `AbilityActionsUsedThisTurn`, record `CreditsSpent`.
2. Apply the board change through the rule (`ApplyMove` or `rule.Apply`); fill outcome masks (`Level`, `Actor`, `Kind`, `Ability`).
3. Landing resolution for Move, Spawn, and Super Clone (footprint removal and `ActorCreditsGained`).
4. Capture reward to the victim (`VictimCreditsGained`) for flipped chips.
5. `ActionCount++`.
6. Apply `AbilityLevelEffect`:
   - `ConsumesMove`: decrement `MovesRemainingThisTurn`; advances if `0`.
   - `GrantsSecondMove`: increment `MovesRemainingThisTurn`.
   - `EndsTurn`: marks `advances = true`.
   - `FreeFrozenPlaced`: sets `FrozenSourcesThisTurn = outcome.FrozenMask = outcome.PlacedMask`.
   - `FreeForbiddenLanding`: sets `ForbiddenLandingsThisTurn = outcome.ForbiddenLandingMask = outcome.RemovedMask`.
   - `Free`: leaves move slots and restrictions unchanged.
7. Global terminal evaluation (`AttaxRules.AdjudicateResult(state)`). If `!advances && Result == NotOver` and restrictions are non-zero and `!HasAnyLegalMoveRestricted(Board, actor, FrozenSourcesThisTurn, ForbiddenLandingsThisTurn)`, set `outcome.AutoPassed = advances = true`.
8. If `advances`: `TurnAdvanced = true`, flip `SideToMove`, `TurnNumber++`, reset `MovesRemainingThisTurn = 1`, `AbilityActionsUsedThisTurn = 0`, and clear `FrozenSourcesThisTurn = ForbiddenLandingsThisTurn = 0UL`.
9. Side-to-move hash key: `MakeMoveMasks` already flips it and cell abilities do not, so toggle when `advances != (action.Kind == ActionKind.Move)`.

## 8. State, restore, and fingerprints

`GameState` fields: `Board` (`BitboardState`, including `VoidSquares`), `RedFootprints`, `BlueFootprints`, `RedCredits`, `BlueCredits`, `SideToMove`, `TurnNumber`, `ActionCount`, `MovesRemainingThisTurn` (1 or 2), `AbilityActionsUsedThisTurn` (0 or 1), `FrozenSourcesThisTurn`, `ForbiddenLandingsThisTurn`, `PlayableCells`, `WinRule`, `Result`, `RulesFingerprint`. Methods: `ExportRules()` (deep copy), `Clone()`, `GetCredits`, `GetFootprints`, `GetAbilityCost(kind, level)`, `ComputeFingerprint()`. `GameState.CreateInitial(board, firstPlayer, rules)` constructs initial states, and `GameState.Restore(...)` validates invariants on restoration.

`GameState.Restore(red, blue, blocked, redFootprints, blueFootprints, sideToMove, redCredits, blueCredits, turnNumber, actionCount, movesRemainingThisTurn, abilityActionsUsedThisTurn, rules, voidSquares = 0UL, frozenSources = 0UL, forbiddenLandings = 0UL)` validates strictly and throws `ArgumentException` for: invalid rules, bits outside the 7x7 board, overlapping board masks (including `voidSquares`), footprints on occupied/blocked/void cells, overlapping footprints, invalid side to move, negative counters, `turnNumber > actionCount`, ability uses outside 0..1, both `frozenSources` and `forbiddenLandings` non-zero, any restriction non-zero without `abilityActionsUsedThisTurn == 1`, `frozenSources` outside the active side's pieces, `forbiddenLandings` outside playable empty cells, moves remaining outside 1..2, moves remaining 2 without an ability use, non-initial counters when `actionCount == 0`, credits outside `[0, maxCredits]`, and (for a disabled economy) any credits, footprints, ability use, or extra move. It recomputes the Zobrist hash, cached board-scaled prices, and `Result`.

Fingerprints use 64-bit FNV-1a (offset basis `14695981039346656037`, prime `1099511628211`), feeding each value as 8 little-endian bytes (`int`, `bool`, and `double` bit patterns widened/converted first).

- `GameRulesConfig.Fingerprint()` feed order: `schemaVersion`, `economy.enabled`, `startingCredits`, `maxCredits`, `captureReward`, `footprintReward`, the 9 cost arrays (`length` then elements in `AbilityKind` order 1..9), `boardScaling` (`referencePlayable`, `alpha` bits, `minFactor` bits, `roundStep`), `turn.maxAbilityActionsPerTurn`, optional `abilityAllowance` (`red` then `blue` array length + elements when `abilityAllowance != null`), and `winRule` (fed only when parsed != `WinRule.Chips`).
- `GameState.ComputeFingerprint()` feed order: `RedPieces`, `BluePieces`, `BlockedSquares`, `VoidSquares` (only when `!= 0`), `ZobristHash`, `SideToMove`, `RedFootprints`, `BlueFootprints`, `RedCredits`, `BlueCredits`, `TurnNumber`, `ActionCount`, `MovesRemainingThisTurn`, `AbilityActionsUsedThisTurn`, `FrozenSourcesThisTurn`, `ForbiddenLandingsThisTurn`, `RulesFingerprint`.

Hosts format masks, hashes, and fingerprints as 18-character hex `0x{value:X16}` on the wire. The shipped `game-rules.json` fingerprint changes whenever a cost, scaling parameter, reward, cap, allowance, or win rule changes; compute it with `GameRulesConfig.Fingerprint()` instead of copying an old value.

## 9. Rules configuration: `game-rules.json`

Source: `Attax.Core.Utils/Rules/game-rules.json`, shipped as a content item so it reaches consumer build and publish output. Classes: `GameRulesConfig` (`schemaVersion`, `winRule`, `economy`, `turn`, runtime-only `[NonSerialized] abilityAllowance`), `EconomyRules`, `AbilityCosts`, `BoardScaling`, `TurnRules`, `AbilityAllowance`.

Shipped values (check `Attax.Core.Utils/Rules/game-rules.json` for current numbers):

| Field | Shipped value |
|---|---|
| `schemaVersion` | 2 |
| `winRule` | `"chipsPlusFootprints"` |
| `economy.enabled` | true |
| `economy.startingCredits` | 0 |
| `economy.maxCredits` | 300 |
| `economy.captureReward` | 10 |
| `economy.footprintReward` | 5 |
| `economy.abilityCosts.*` | `superJump`: `[100]`, `convert`: `[60, 75, 90]`, `block`: `[40, 50]`, `wall`: `[50]`, `doubleMove`: `[120]`, `spawn`: `[60, 75, 90]`, `superClone`: `[150]`, `unblock`: `[50, 80]`, `bomb`: `[170, 215, 255]` |
| `economy.boardScaling` | `referencePlayable`: 48, `alpha`: 1.0, `minFactor`: 0.5, `roundStep`: 5 |
| `turn.maxAbilityActionsPerTurn` | 1 |
| `abilityAllowance` | `null` in file JSON (all levels up to `MaxLevel` allowed by default) |

Rules for the classes:

- No code defaults: a freshly constructed `GameRulesConfig` has null groups and fails `Validate()`.
- `Validate()` returns every problem and never throws. It checks `schemaVersion == 2`, `winRule` (`"chips"` or `"chipsPlusFootprints"`), all groups present (`economy`, `abilityCosts`, `boardScaling`, `turn`), `maxCredits > 0`, `0 <= startingCredits <= maxCredits`, non-negative rewards, exact per-ability level counts, positive and `>= roundStep`-increasing costs when economy is enabled, valid `boardScaling` (`referencePlayable` 1..49, finite non-negative `alpha`, `0 < minFactor <= 1`, `roundStep > 0`), `maxAbilityActionsPerTurn == 1`, and valid `abilityAllowance` bounds when non-null.
- `GameRulesConfig.GetAbilityCost(kind, level)` returns 0 for `None`, disabled economy, or out-of-range level; `GameState.GetAbilityCost(kind, level)` returns the board-scaled price.
- `GetMaxLevel(player, kind)` reads `abilityAllowance` if present, otherwise returns `GameRulesConfig.GetMaxLevel(kind)`.
- `EngineStateFactory.NewGame` (server) and `Restore` clone the rules, so later changes to the caller's object do not affect a running game. Server-side per-ruleset overlays are merged at the JSON level by the server before deserialization (rejecting `abilityAllowance` in file/overlay inputs).

## 10. Opening generator

`StartPosition.Standard(firstPlayer = Red)` places Red on squares 0 and 48 and Blue on 6 and 42. `StartPosition.Create(blockedCells, seed, firstPlayer = Red)` adds seeded blocked cells and recomputes the hash for `firstPlayer`. `StartPosition.GenerateBlockedCellPositions(count, board, seed)` (and a `Random` overload) is the single shared implementation; the AI engine and play tools delegate to it.

The algorithm is zoned: `zoneCount = ceil(sqrt(count))`, `zoneSize = 7 / zoneCount` (integer division, so some zones do not cover the whole board), zones shuffled with `OrderBy(rnd.Next())`, then up to 1000 attempts picking `rnd.Next(minX, maxX)` and `rnd.Next(minY, maxY)` per zone. This output is intentionally stable for a given seed.

`EngineInfo.OpeningAlgorithmId` is `"zoned-system-random-v1"`. `EngineInfo.VerifyOpeningCompatibility(out string error)` checks four golden vectors (seed/count pairs `(0,3)`, `(42,6)`, `(123,1)`, `(999,12)`) so a runtime change in `System.Random` would be noticed. `EngineInfo.RulesRevision` (public, like `OpeningAlgorithmId`; engine diagnostics only, not a wire value) is `2`; it is informational (logs and diagnostics), and client compatibility is enforced by the server's client-version policy.

## 11. AI ability decision

Files: `Attax.Core/Abilities/*` (`AbilityDecider`, `IAbilityTactician`, nine tacticians, `TacticalSignals`, `TacticalContext`), `Rules/AiAbilityConfig.cs`, `Rules/AiAbilityOptions.cs`, `Rules/ai-ability-config.json` (schema v2). The full description is `AI_ABILITY_TACTICS.md` in AttaxShared `docs/`. The v1 `AiAbilityPolicy`, `AiAbilityChoice` and `UseEngineEvaluator` were removed; the decision is deterministic and consumes no random stream.

`ai-ability-config.json` (shipped values):

| Field | Meaning | Shipped |
|---|---|---|
| `schemaVersion` | Must be 2 (explicit) | 2 |
| `aiAbilitiesEnabled` | Master switch from the file | true |
| `creditValueChips` | Chips one credit is worth (>= 0) | 0.1 |
| `minGainChips` | Minimum net gain in chips to cast (>= 0) | 1.0 |
| `maxCandidatesVerified` | Affordable candidates verified per decision (1..32) | 8 |
| `verifyDepthOffset` | Verification depth = `max(1, aiDepth - offset)` (0..4) | 1 |
| `savingEnabled` | Defer a modest cast for a clearly better one | true |

`AbilityDecider.Decide(state, engine, config, aiDepth, ct)`: baseline search of the plain move, tactician proposals, verification of the top affordable candidates by clone + `TryApply` + search of the actual next side, then `net = (score - plain) / EvalPerChip - cost * creditValueChips >= minGainChips`. `AbilityDecider.MeasureValues` returns per-level `AbilityValueSample`s for the calibration tool. `SearchOptions.ExcludedSources` / `ExcludedDestinations` are root-only search filters for frozen chips and forbidden landings; `AtaxxAIEngine.EvalPerChip(board)` converts score units to chips.

Host flow (`UserSimulator/Attax.Agents/AgentTurnPlanner.cs`): when the config is enabled and the player is a search player, call `Decide`; apply `Action` with `TryApply` if `UseAbility`. Otherwise choose a standard move honouring the restriction masks. Self-play, training and arena runs construct no config.
## 12. Source and test map

| Topic | Source | Tests (`Attax.Core.Tests/`) |
|---|---|---|
| Facade, validation, turn flow, parity | `Rules/AttaxGame.cs`, `Rules/Abilities/*.cs`, `AttaxRules.cs` | `AttaxGameTests.cs`, `AttaxGameAbilityTests.cs`, `AbilityLevelTests.cs` |
| Economy and caps | `Rules/AttaxGame.cs`, `Rules/BoardScaling.cs` | `AttaxGameEconomyTests.cs`, `AbilityLevelTests.cs` |
| Invariants, allocation | `Rules/GameState.cs` | `AttaxGameInvariantAndAllocationTests.cs` |
| Rules config and fingerprints | `Rules/GameRulesConfig.cs`, `Rules/game-rules.json` | `GameRulesConfigTests.cs` |
| Opening generator | `Rules/StartPosition.cs`, `Rules/EngineInfo.cs` | `StartPositionTests.cs`, `StartBoardTests.cs` |
| Compatibility and golden fixtures | n/a | `CompatibilityTests.cs`, `RefactorGoldenTests.cs` |
| AI ability decision | `Attax.Core/Abilities/`, `Rules/AiAbilityConfig.cs` | `AbilityDeciderTests.cs`, `TacticianTests.cs`, `HintSearchTests.cs` |
| Bot orchestration | `UserSimulator/Attax.Agents/AgentTurnPlanner.cs`, `UserSimulator/Attax.UserSimulator/BotSnapshot.cs` | `Attax.Core.Tests/AttaxAgentsTests.cs`, `UserSimulator/Attax.UserSimulator.Tests/BotCoreUnitTests.cs` |

This reference was written from source reading. No build or test run was performed for it; run the commands in [docs/TESTING.md](docs/TESTING.md) to verify the engine.

## 13. Maintenance

- Change this file in the same commit as any change to ability behavior, costs, validation order, enum values, fingerprint order, the opening algorithm, or the AI policy contract.
- A behavioral rules change bumps `EngineInfo.RulesRevision`, and a change to a persisted or wire value needs a coordinated server and Unity release.
- Adding an ability means: append an `AbilityKind` and `ActionKind` value, add a price field (bump `schemaVersion` and the fingerprint order), add validation and apply blocks, a `GetAbilityTargets` case, tests, and a `RulesRevision` bump.
- The server records engine discrepancies in its `ENGINE_SERVER_FOLLOWUPS.md`; engine fixes belong here.
