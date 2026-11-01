This is not true. Bug fixed. need to test:

# Engine Documentation: Rules, Architecture, and Search

## 1. Shared Rules Engine (`AttaxGame`, Abilities, Economy)

`Attax.Core.Utils` provides the pure, authoritative rules engine shared between the console tools, the authoritative server, and the Unity client.

### 1.1 The Nine Abilities

All nine abilities cost 100 credits by default in `game-rules.json`. Strictly one ability action is permitted per turn (`turn.maxAbilityActionsPerTurn = 1`). Each player's allowed abilities can be individually toggled for the match via `playerAbilities.red` and `playerAbilities.blue` in `game-rules.json`.

| Ability | Kind | Target | Effect | Turn Handling |
|---|---|---|---|---|
| **SuperJump** | `Move` (d >= 3) | Empty cell | Jump of distance >= 3: vacates source (leaves footprint), captures adjacent enemies | Consumes standard move and ability use |
| **Convert (Chief)** | `Convert` | Enemy chip | Converts enemy chip to actor's chip without adjacent captures | Free action (does not consume move) |
| **Block** | `Block` | Enemy chip | Removes enemy chip and places a blocked square | Free action (does not consume move) |
| **Wall** | `Wall` | Empty cell | Places a blocked square on empty cell; clears any footprint | Free action (does not consume move) |
| **DoubleMove** | `DoubleMove` | None | Arms 2 standard moves for the current player this turn | Free action (consumes sole ability use) |
| **Spawn** | `Spawn` | Empty cell | Places actor chip on empty cell; no captures; pays 100 credits | Free action (consumes sole ability use; does not consume move) |
| **SuperClone** | `SuperClone` | Empty cell | Places actor chip on empty cell and captures adjacent enemies | Consumes standard move and ability use |
| **Unblock** | `Unblock` | Blocked cell | Clears blocked cell, making it empty (works on opening blocks too) | Free action (does not consume move) |
| **Bomb** | `Bomb` | Enemy chip | Removes enemy chip, leaving cell empty; no footprints or compensation | Free action (does not consume move) |

### 1.2 Rule Decisions (D1 - D12)

- **D1, Self-stranding free actions:** A Wall, Block, Convert, or Bomb may leave the acting player (or opponent) without a standard move, ending the game immediately under the no-pass rule. This is allowed and evaluated immediately. `AttaxGame.WouldEndGame` allows UI warning beforehand.
- **D2, Credits never create moves:** A player lacking a standard clone/jump cannot bypass game termination even if they have enough credits for Spawn, SuperClone, or SuperJump. `HasStandardMove` strictly checks clone/jump availability.
- **D3, Terminal state is global:** `AttaxRules.GetTerminalResult` evaluates both sides regardless of whose turn it is, and runs after every accepted action (including free actions).
- **D4, Initial result:** `AttaxGame.NewGame` immediately evaluates terminal state.
- **D5, Double Move edge case:** After the first move of Double Move, global terminal check applies. If the actor has no moves left for the second move, the game terminates (loss by no-pass rule).
- **D6, Strictly one ability per turn:** `turn.maxAbilityActionsPerTurn = 1`. Double Move uses the turn's sole ability use. Spawn also consumes the sole ability use.
- **D7, Opening blocks are normal blocks:** Unblock can clear blocks placed during the opening.
- **D8, Footprints:** Only jumps (d=2) and SuperJumps (d>=3) leave footprints on vacated source cells. Landing on an enemy footprint awards 5 credits to the lander and clears it; landing on an own footprint clears it with no reward. Wall clears footprints. Footprints only exist on empty cells and never on chips.
- **D9, Credits are not in the Zobrist hash:** Credits and footprints do not alter standard move generation. Desync detection across network/persistence is handled by `GameState.ComputeFingerprint()`.
- **D10, Shipped credit cap 3000:** Balances are capped at `maxCredits: 3000`. Rewards are clamped to remaining capacity under 3000. Spending occurs before rewards, creating capacity.
- **D11, Spawn is paid and non-move-consuming:** Spawn costs 100 credits. It clears/rewards any footprint at the destination, increments `ActionCount` and `AbilityActionsUsedThisTurn`, but leaves `MovesRemainingThisTurn`, `SideToMove`, `TurnNumber`, and the side hash key unchanged (`TurnAdvanced = false`). If nonterminal, the actor continues with an ordinary clone/jump.
- **D12, Opening verification:** `EngineInfo.OpeningAlgorithmId = "zoned-system-random-v1"`. `EngineInfo.VerifyOpeningCompatibility(out string error)` provides self-check verification against golden vectors.

### 1.3 AI Ability Policy

AI ability usage is driven by `AiAbilityPolicy` in `Attax.Core.Utils`.
- Driven by `ai-ability-config.json` with master flag `aiAbilitiesEnabled: false` by default.
- Pure choice policy with its own seeded `Random` instance; does not alter AI engine RNG or search behavior.
- Evaluates at most once per turn. Candidate abilities are filtered by affordability, credit reserve, and the match's per-player ability toggles (`playerAbilities`).
- Selects uniformly at random among available, affordable candidate abilities without arbitrary weight tables.
- Optional safety filter simulates choices with `TryApply` and rejects moves that cause an immediate loss.
- Successful application must be reported by the host using `RecordAppliedAbility(state, outcome)`.
