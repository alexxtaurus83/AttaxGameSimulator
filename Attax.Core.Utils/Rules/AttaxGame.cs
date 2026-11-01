using System;
using System.Collections.Generic;

namespace Attax.Core.Utils {
    /// <summary>
    /// Authoritative rules facade for Attax games, encapsulating action validation, application,
    /// nine cheat abilities, credit economy, turn flow, and state queries without external dependencies.
    /// </summary>
    public static class AttaxGame {
        /// <summary>
        /// Creates a new game instance with validated rules and opening configuration.
        /// </summary>
        public static GameState NewGame(GameRulesConfig rules, StartOptions start) {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            if (start == null) throw new ArgumentNullException(nameof(start));

            var errors = rules.Validate();
            if (errors.Count > 0) throw new ArgumentException("Invalid rules config: " + string.Join("; ", errors));

            if (start.FirstPlayer != PlayerColor.Red && start.FirstPlayer != PlayerColor.Blue)
                throw new ArgumentException("Invalid start.FirstPlayer: " + start.FirstPlayer);

            if (start.BlockedCells < 0 || start.BlockedCells > 12)
                throw new ArgumentException("BlockedCells must be between 0 and 12.");

            var ownedRules = rules.Clone();
            ulong fp = ownedRules.Fingerprint();
            var state = new GameState(ownedRules, fp) {
                Board = StartPosition.Create(start.BlockedCells, start.Seed, start.FirstPlayer),
                SideToMove = start.FirstPlayer
            };

            int startingCredits = ownedRules.economy != null && ownedRules.economy.enabled ? ownedRules.economy.startingCredits : 0;
            state.RedCredits = startingCredits;
            state.BlueCredits = startingCredits;
            state.MovesRemainingThisTurn = 1;
            state.AbilityActionsUsedThisTurn = 0;
            state.TurnNumber = 0;
            state.ActionCount = 0;
            state.Result = AttaxRules.GetTerminalResult(state.Board);

            return state;
        }

        /// <summary>
        /// Returns the ability represented by the action, or AbilityKind.None for normal clone/jump moves.
        /// </summary>
        public static AbilityKind AbilityOf(in GameAction action) {
            switch (action.Kind) {
                case ActionKind.Move:
                    int dx = Math.Abs(action.Move.ToX - action.Move.FromX);
                    int dy = Math.Abs(action.Move.ToY - action.Move.FromY);
                    return Math.Max(dx, dy) >= 3 ? AbilityKind.SuperJump : AbilityKind.None;
                case ActionKind.Convert: return AbilityKind.Convert;
                case ActionKind.Block: return AbilityKind.Block;
                case ActionKind.Wall: return AbilityKind.Wall;
                case ActionKind.DoubleMove: return AbilityKind.DoubleMove;
                case ActionKind.Spawn: return AbilityKind.Spawn;
                case ActionKind.SuperClone: return AbilityKind.SuperClone;
                case ActionKind.Unblock: return AbilityKind.Unblock;
                case ActionKind.Bomb: return AbilityKind.Bomb;
                default: return AbilityKind.None;
            }
        }

        /// <summary>
        /// Gets the configured credit cost of the ability under the game's effective rules. Returns 0 if disabled or free.
        /// </summary>
        public static int GetAbilityCost(GameState state, AbilityKind kind) {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return state.Rules.GetAbilityCost(kind);
        }

        /// <summary>
        /// Returns true if the player can afford the given ability and the ability is enabled in the rules.
        /// </summary>
        public static bool CanAfford(GameState state, PlayerColor side, AbilityKind kind) {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (kind == AbilityKind.None) return true;
            if (state.Rules.economy == null || !state.Rules.economy.enabled) return false;
            int cost = state.Rules.GetAbilityCost(kind);
            if (cost <= 0) return false;
            return state.GetCredits(side) >= cost;
        }

        /// <summary>
        /// Validates whether the given action can be legally applied by the actor in the current game state.
        /// Pure and allocation-free. Does not mutate the state.
        /// </summary>
        public static ActionError Validate(GameState state, PlayerColor actor, in GameAction action) {
            if (state == null) throw new ArgumentNullException(nameof(state));

            // 1. GameOver check
            if (state.Result != TerminalResult.NotOver) return ActionError.GameOver;

            // 2. Actor check
            if (actor != state.SideToMove || (actor != PlayerColor.Red && actor != PlayerColor.Blue))
                return ActionError.NotYourTurn;

            // 3. Action kind check
            if ((int)action.Kind < 0 || (int)action.Kind > 8)
                return ActionError.UnknownAction;

            // 4. Coordinates, geometry, and target checks
            if (action.Kind == ActionKind.DoubleMove) {
                if (state.MovesRemainingThisTurn > 1) return ActionError.DoubleMoveAlreadyActive;
            } else if (action.Kind == ActionKind.Move) {
                if (action.Move.FromX < 0 || action.Move.FromX >= 7 || action.Move.FromY < 0 || action.Move.FromY >= 7)
                    return ActionError.OutOfBounds;
                if (action.Move.ToX < 0 || action.Move.ToX >= 7 || action.Move.ToY < 0 || action.Move.ToY >= 7)
                    return ActionError.OutOfBounds;
                if (action.Move.FromX == action.Move.ToX && action.Move.FromY == action.Move.ToY)
                    return ActionError.SameSquare;

                int dx = Math.Abs(action.Move.ToX - action.Move.FromX);
                int dy = Math.Abs(action.Move.ToY - action.Move.FromY);
                int d = Math.Max(dx, dy);

                int fromIdx = action.Move.FromY * 7 + action.Move.FromX;
                ulong friendly = (actor == PlayerColor.Red) ? state.Board.RedPieces : state.Board.BluePieces;
                if ((friendly & (1UL << fromIdx)) == 0) return ActionError.NotYourPiece;

                int toIdx = action.Move.ToY * 7 + action.Move.ToX;
                if ((state.Board.EmptySquares() & (1UL << toIdx)) == 0) return ActionError.DestinationNotEmpty;
            } else if (action.Kind == ActionKind.Convert || action.Kind == ActionKind.Block || action.Kind == ActionKind.Bomb) {
                if (action.X < 0 || action.X >= 7 || action.Y < 0 || action.Y >= 7) return ActionError.OutOfBounds;
                int idx = action.Y * 7 + action.X;
                ulong enemy = (actor == PlayerColor.Red) ? state.Board.BluePieces : state.Board.RedPieces;
                if ((enemy & (1UL << idx)) == 0) return ActionError.NotEnemyChip;
            } else if (action.Kind == ActionKind.Wall || action.Kind == ActionKind.Spawn || action.Kind == ActionKind.SuperClone) {
                if (action.X < 0 || action.X >= 7 || action.Y < 0 || action.Y >= 7) return ActionError.OutOfBounds;
                int idx = action.Y * 7 + action.X;
                if ((state.Board.EmptySquares() & (1UL << idx)) == 0) return ActionError.DestinationNotEmpty;
            } else if (action.Kind == ActionKind.Unblock) {
                if (action.X < 0 || action.X >= 7 || action.Y < 0 || action.Y >= 7) return ActionError.OutOfBounds;
                int idx = action.Y * 7 + action.X;
                if ((state.Board.BlockedSquares & (1UL << idx)) == 0) return ActionError.NotBlockedCell;
            }

            // 5. Resource and ability checks
            AbilityKind ability = AbilityOf(action);
            if (ability != AbilityKind.None) {
                if (!state.Rules.IsAbilityEnabled(actor, ability))
                    return ActionError.AbilityDisabled;

                if (state.Rules.economy == null || !state.Rules.economy.enabled)
                    return ActionError.AbilityDisabled;

                int cost = state.Rules.GetAbilityCost(ability);
                if (cost <= 0) return ActionError.AbilityDisabled;

                int balance = state.GetCredits(actor);
                if (balance < cost) return ActionError.InsufficientCredits;

                int maxAbilities = state.Rules.turn != null ? state.Rules.turn.maxAbilityActionsPerTurn : 1;
                if (state.AbilityActionsUsedThisTurn >= maxAbilities)
                    return ActionError.AbilityLimitReachedThisTurn;
            }

            // 6. Counter overflow check
            if (state.ActionCount == int.MaxValue) return ActionError.CounterOverflow;
            bool isTurnEnding = action.Kind == ActionKind.Move || action.Kind == ActionKind.SuperClone;
            bool wouldAdvanceTurn = isTurnEnding && state.MovesRemainingThisTurn <= 1;
            if (wouldAdvanceTurn && state.TurnNumber == int.MaxValue) return ActionError.CounterOverflow;

            return ActionError.None;
        }

        /// <summary>
        /// Validates and applies an action to the game state. On validation error, leaves state completely
        /// untouched and sets outcome to default. On success, mutates state and populates outcome.
        /// </summary>
        public static ActionError TryApply(GameState state, PlayerColor actor, in GameAction action, out ActionOutcome outcome) {
            outcome = default;
            if (state == null) throw new ArgumentNullException(nameof(state));

            var error = Validate(state, actor, action);
            if (error != ActionError.None) return error;

            AbilityKind ability = AbilityOf(action);
            outcome.Kind = action.Kind;
            outcome.Ability = ability;
            outcome.Actor = actor;

            // 1. Deduct ability price
            if (ability != AbilityKind.None) {
                int cost = state.Rules.GetAbilityCost(ability);
                if (actor == PlayerColor.Red) state.RedCredits -= cost;
                else state.BlueCredits -= cost;
                state.AbilityActionsUsedThisTurn++;
                outcome.CreditsSpent = cost;
            }

            // 2. Board change
            int targetIndex = action.Y * 7 + action.X;
            ulong targetMask = 1UL << targetIndex;

            switch (action.Kind) {
                case ActionKind.Move:
                    int fromIdx = action.Move.FromY * 7 + action.Move.FromX;
                    int toIdx = action.Move.ToY * 7 + action.Move.ToX;
                    ulong toMask = 1UL << toIdx;
                    AttaxRules.MakeMoveMasks(state.Board, action.Move, actor, out ulong flippedMask);
                    outcome.PlacedMask = toMask;
                    outcome.FlippedMask = flippedMask;

                    // Source footprint for jump or super jump
                    int dx = Math.Abs(action.Move.ToX - action.Move.FromX);
                    int dy = Math.Abs(action.Move.ToY - action.Move.FromY);
                    if (Math.Max(dx, dy) >= 2 && state.Rules.economy != null && state.Rules.economy.enabled) {
                        ulong fromMask = 1UL << fromIdx;
                        if (actor == PlayerColor.Red) state.RedFootprints |= fromMask;
                        else state.BlueFootprints |= fromMask;
                        outcome.FootprintPlacedMask = fromMask;
                    }
                    break;

                case ActionKind.Convert:
                    AttaxRules.TryConvertEnemyChip(state.Board, actor, action.X, action.Y, ChipConversion.ToPlayer);
                    outcome.PlacedMask = targetMask;
                    break;

                case ActionKind.Block:
                    AttaxRules.TryConvertEnemyChip(state.Board, actor, action.X, action.Y, ChipConversion.ToBlocked);
                    outcome.RemovedMask = targetMask;
                    outcome.BlockedAddedMask = targetMask;
                    break;

                case ActionKind.Wall:
                    AttaxRules.TryPlaceWall(state.Board, action.X, action.Y);
                    outcome.BlockedAddedMask = targetMask;
                    if (((state.RedFootprints | state.BlueFootprints) & targetMask) != 0) {
                        state.RedFootprints &= ~targetMask;
                        state.BlueFootprints &= ~targetMask;
                        outcome.FootprintClearedMask = targetMask;
                    }
                    break;

                case ActionKind.Unblock:
                    AttaxRules.TryUnblock(state.Board, action.X, action.Y);
                    outcome.BlockedClearedMask = targetMask;
                    break;

                case ActionKind.Bomb:
                    AttaxRules.TryRemoveEnemyChip(state.Board, actor, action.X, action.Y);
                    outcome.RemovedMask = targetMask;
                    break;

                case ActionKind.Spawn:
                    AttaxRules.TryPlaceChip(state.Board, actor, action.X, action.Y, capture: false, out _);
                    outcome.PlacedMask = targetMask;
                    break;

                case ActionKind.SuperClone:
                    AttaxRules.TryPlaceChip(state.Board, actor, action.X, action.Y, capture: true, out ulong scFlipped);
                    outcome.PlacedMask = targetMask;
                    outcome.FlippedMask = scFlipped;
                    break;

                case ActionKind.DoubleMove:
                    state.MovesRemainingThisTurn = 2;
                    break;
            }

            // 3. Destination landing resolution for Move, Spawn, SuperClone
            if (action.Kind == ActionKind.Move || action.Kind == ActionKind.Spawn || action.Kind == ActionKind.SuperClone) {
                int destIdx = action.Kind == ActionKind.Move ? (action.Move.ToY * 7 + action.Move.ToX) : targetIndex;
                ulong destMask = 1UL << destIdx;
                ulong friendlyFootprint = (actor == PlayerColor.Red) ? state.RedFootprints : state.BlueFootprints;
                ulong enemyFootprint = (actor == PlayerColor.Red) ? state.BlueFootprints : state.RedFootprints;

                if ((friendlyFootprint & destMask) != 0) {
                    if (actor == PlayerColor.Red) state.RedFootprints &= ~destMask;
                    else state.BlueFootprints &= ~destMask;
                    outcome.FootprintClearedMask |= destMask;
                }

                if ((enemyFootprint & destMask) != 0) {
                    if (actor == PlayerColor.Red) state.BlueFootprints &= ~destMask;
                    else state.RedFootprints &= ~destMask;
                    outcome.FootprintClearedMask |= destMask;

                    if (state.Rules.economy != null && state.Rules.economy.enabled) {
                        int currentBal = state.GetCredits(actor);
                        int cap = state.Rules.economy.maxCredits;
                        int reward = Math.Min(state.Rules.economy.footprintReward, Math.Max(0, cap - currentBal));
                        if (actor == PlayerColor.Red) state.RedCredits += reward;
                        else state.BlueCredits += reward;
                        outcome.ActorCreditsGained = reward;
                    }
                }
            }

            // 4. Flip capture rewards
            if (outcome.FlippedMask > 0 && state.Rules.economy != null && state.Rules.economy.enabled) {
                int flippedCount = BitboardOps.PopCount(outcome.FlippedMask);
                PlayerColor victim = AttaxRules.SwitchPlayer(actor);
                int victimBal = state.GetCredits(victim);
                int cap = state.Rules.economy.maxCredits;
                long rawReward = (long)flippedCount * state.Rules.economy.captureReward;
                int reward = (int)Math.Min(rawReward, (long)Math.Max(0, cap - victimBal));
                if (victim == PlayerColor.Red) state.RedCredits += reward;
                else state.BlueCredits += reward;
                outcome.VictimCreditsGained = reward;
            }

            // 5. Action counter
            state.ActionCount++;

            // 6. Turn handling and hash correction
            if (action.Kind == ActionKind.Move || action.Kind == ActionKind.SuperClone) {
                state.MovesRemainingThisTurn--;
                if (state.MovesRemainingThisTurn == 0) {
                    outcome.TurnAdvanced = true;
                    state.SideToMove = AttaxRules.SwitchPlayer(state.SideToMove);
                    state.TurnNumber++;
                    state.MovesRemainingThisTurn = 1;
                    state.AbilityActionsUsedThisTurn = 0;

                    if (action.Kind == ActionKind.SuperClone) {
                        state.Board.ZobristHash ^= ZobristHasher.GetSideToMoveKey();
                    }
                } else {
                    outcome.TurnAdvanced = false;
                    if (action.Kind == ActionKind.Move) {
                        state.Board.ZobristHash ^= ZobristHasher.GetSideToMoveKey();
                    }
                }
            } else {
                outcome.TurnAdvanced = false;
            }

            // 7. Terminal evaluation
            state.Result = AttaxRules.GetTerminalResult(state.Board);
            outcome.Result = state.Result;

            return ActionError.None;
        }

        /// <summary>
        /// Pure geometric query returning empty reachable destination cells for a chip of SideToMove at (x,y).
        /// Returns 0 if (x,y) is not a piece of SideToMove. Does not check affordability.
        /// </summary>
        public static ulong GetMoveTargets(GameState state, int x, int y, bool includeSuperJump) {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (x < 0 || x >= 7 || y < 0 || y >= 7) return 0UL;

            int fromIdx = y * 7 + x;
            ulong friendly = (state.SideToMove == PlayerColor.Red) ? state.Board.RedPieces : state.Board.BluePieces;
            if ((friendly & (1UL << fromIdx)) == 0) return 0UL;

            ulong empty = state.Board.EmptySquares();
            ulong step1 = BoardLookup.SingleStepMoves[fromIdx] & empty;
            ulong step2 = BoardLookup.TwoStepMoves[fromIdx] & empty;
            ulong targets = step1 | step2;

            if (includeSuperJump) {
                ulong near = BoardLookup.SingleStepMoves[fromIdx] | BoardLookup.TwoStepMoves[fromIdx] | (1UL << fromIdx);
                ulong superTargets = empty & ~near;
                targets |= superTargets;
            }

            return targets;
        }

        /// <summary>
        /// Returns a bitmask of valid target cells for the given ability for SideToMove in the current state.
        /// Filtered by usability: returns 0 if disabled, unaffordable, turn limit reached, or game over.
        /// DoubleMove always returns 0 (use CanUseAbility).
        /// </summary>
        public static ulong GetAbilityTargets(GameState state, AbilityKind kind) {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return GetAbilityTargets(state, state.SideToMove, kind);
        }

        /// <summary>
        /// Returns a bitmask of valid target cells for the given ability for the specified side in the current state.
        /// Filtered by usability: returns 0 if disabled, unaffordable, turn limit reached, or game over.
        /// DoubleMove always returns 0 (use CanUseAbility).
        /// </summary>
        public static ulong GetAbilityTargets(GameState state, PlayerColor side, AbilityKind kind) {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (state.Result != TerminalResult.NotOver) return 0UL;
            if (state.ActionCount == int.MaxValue) return 0UL;
            if (!state.Rules.IsAbilityEnabled(side, kind)) return 0UL;
            if (!CanAfford(state, side, kind)) return 0UL;

            int maxAbilities = state.Rules.turn != null ? state.Rules.turn.maxAbilityActionsPerTurn : 1;
            if (state.AbilityActionsUsedThisTurn >= maxAbilities) return 0UL;

            switch (kind) {
                case AbilityKind.DoubleMove:
                    return 0UL;

                case AbilityKind.SuperJump:
                    ulong friendly = (side == PlayerColor.Red) ? state.Board.RedPieces : state.Board.BluePieces;
                    if (friendly == 0) return 0UL;
                    ulong empty = state.Board.EmptySquares();
                    ulong superJumpTargets = 0UL;
                    ulong remFriendly = friendly;
                    while (remFriendly > 0) {
                        int fIdx = BitboardOps.TrailingZeroCount(remFriendly);
                        ulong near = BoardLookup.SingleStepMoves[fIdx] | BoardLookup.TwoStepMoves[fIdx] | (1UL << fIdx);
                        superJumpTargets |= (empty & ~near);
                        remFriendly &= remFriendly - 1;
                    }
                    return superJumpTargets;

                case AbilityKind.Convert:
                case AbilityKind.Block:
                case AbilityKind.Bomb:
                    return (side == PlayerColor.Red) ? state.Board.BluePieces : state.Board.RedPieces;

                case AbilityKind.Wall:
                case AbilityKind.Spawn:
                case AbilityKind.SuperClone:
                    return state.Board.EmptySquares();

                case AbilityKind.Unblock:
                    return state.Board.BlockedSquares;

                default:
                    return 0UL;
            }
        }

        /// <summary>
        /// Returns true if the specified ability is currently usable by SideToMove
        /// (enabled, affordable, turn limit not reached, and has at least one valid target).
        /// </summary>
        public static bool CanUseAbility(GameState state, AbilityKind kind) {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return CanUseAbility(state, state.SideToMove, kind);
        }

        /// <summary>
        /// Returns true if the specified ability is currently usable by the specified side
        /// (enabled, affordable, turn limit not reached, and has at least one valid target).
        /// </summary>
        public static bool CanUseAbility(GameState state, PlayerColor side, AbilityKind kind) {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (state.Result != TerminalResult.NotOver) return false;
            if (state.ActionCount == int.MaxValue) return false;
            if (!state.Rules.IsAbilityEnabled(side, kind)) return false;
            if (!CanAfford(state, side, kind)) return false;

            int maxAbilities = state.Rules.turn != null ? state.Rules.turn.maxAbilityActionsPerTurn : 1;
            if (state.AbilityActionsUsedThisTurn >= maxAbilities) return false;

            if (kind == AbilityKind.DoubleMove) {
                return state.MovesRemainingThisTurn == 1;
            }

            return GetAbilityTargets(state, side, kind) != 0UL;
        }

        /// <summary>
        /// Returns a bitmask representing available abilities for SideToMove, with bit (1 &lt;&lt; (int)kind) set if usable.
        /// </summary>
        public static int GetAvailableAbilities(GameState state) {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return GetAvailableAbilities(state, state.SideToMove);
        }

        /// <summary>
        /// Returns a bitmask representing available abilities for the specified side, with bit (1 &lt;&lt; (int)kind) set if usable.
        /// </summary>
        public static int GetAvailableAbilities(GameState state, PlayerColor side) {
            if (state == null) throw new ArgumentNullException(nameof(state));
            int mask = 0;
            for (int k = 1; k <= 9; k++) {
                if (CanUseAbility(state, side, (AbilityKind)k)) {
                    mask |= (1 << k);
                }
            }
            return mask;
        }

        /// <summary>
        /// Returns true if the player has at least one standard clone or jump move available.
        /// Ignores credits (drives the global no-pass rule).
        /// </summary>
        public static bool HasStandardMove(GameState state, PlayerColor side) {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return AttaxRules.HasAnyLegalMove(state.Board, side);
        }

        /// <summary>
        /// Returns the chip count of the given player in the current state.
        /// </summary>
        public static int GetScore(GameState state, PlayerColor side) {
            if (state == null) throw new ArgumentNullException(nameof(state));
            ulong pieces = (side == PlayerColor.Red) ? state.Board.RedPieces :
                           (side == PlayerColor.Blue) ? state.Board.BluePieces : 0UL;
            return BitboardOps.PopCount(pieces);
        }

        /// <summary>
        /// Simulates the action on a cloned state to determine whether it would result in the game ending.
        /// </summary>
        public static bool WouldEndGame(GameState state, PlayerColor actor, in GameAction action) {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (Validate(state, actor, action) != ActionError.None) return false;
            var clone = state.Clone();
            var err = TryApply(clone, actor, action, out _);
            return err == ActionError.None && clone.Result != TerminalResult.NotOver;
        }
    }
}
