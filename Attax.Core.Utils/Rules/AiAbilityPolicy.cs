using System;
using System.Collections.Generic;

namespace Attax.Core.Utils {
    public sealed class AiAbilityPolicy {
        private readonly AiAbilityConfig _config;
        private readonly int _seed;
        private Random _rng;

        private int _lastDecidedTurn = -1;
        private PlayerColor _lastDecidedSide = PlayerColor.None;

        private bool _hasPendingChoice = false;
        private AiAbilityChoice _pendingChoice = default(AiAbilityChoice);
        private int _pendingPreActionCount = -1;
        private PlayerColor _pendingSide = PlayerColor.None;

        public bool Enabled { get; set; }
        public int UsesThisGame { get; private set; }

        /// <summary>
        /// Optional evaluator for scoring board leaves during target selection (e.g. from HeuristicEvaluator in Attax.Core).
        /// When null, target selection falls back to material piece difference (scored mode).
        /// </summary>
        public Func<BitboardState, PlayerColor, float> TargetEvaluator { get; set; }

        public AiAbilityPolicy(AiAbilityConfig config, int seed, bool enabled) {
            if (config == null) throw new ArgumentNullException(nameof(config));
            List<string> errors = config.Validate();
            if (errors.Count > 0) throw new ArgumentException("Invalid AiAbilityConfig: " + string.Join("; ", errors));

            _config = config.Clone();
            _seed = seed;
            _rng = new Random(seed);
            Enabled = enabled;
            UsesThisGame = 0;
        }

        public void ResetGame() {
            UsesThisGame = 0;
            _rng = new Random(_seed);
            _lastDecidedTurn = -1;
            _lastDecidedSide = PlayerColor.None;
            _hasPendingChoice = false;
            _pendingChoice = default(AiAbilityChoice);
            _pendingPreActionCount = -1;
            _pendingSide = PlayerColor.None;
        }

        public bool TryChoose(GameState state, PlayerColor side, out AiAbilityChoice choice) {
            choice = default(AiAbilityChoice);
            if (!Enabled) return false;
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (state.Result != TerminalResult.NotOver) return false;
            if (side != state.SideToMove) return false;
            if (state.Rules.economy == null || !state.Rules.economy.enabled) return false;
            if (_config.maxUsesPerGame > 0 && UsesThisGame >= _config.maxUsesPerGame) return false;

            // Maintain one decision opportunity per actual TurnNumber/side
            if (_lastDecidedTurn == state.TurnNumber && _lastDecidedSide == side) {
                return false;
            }
            _lastDecidedTurn = state.TurnNumber;
            _lastDecidedSide = side;

            // Chance roll
            if (_rng.Next(100) >= _config.useChancePercent) {
                return false;
            }

            // Build candidate list from usable & affordable abilities
            List<AbilityKind> candidates = new List<AbilityKind>();
            int currentCredits = state.GetCredits(side);
            for (int k = 1; k <= 9; k++) {
                AbilityKind kind = (AbilityKind)k;
                if (!AttaxGame.CanUseAbility(state, side, kind)) continue;

                int cost = state.Rules.GetAbilityCost(kind);
                if (currentCredits - cost < _config.creditReserve) continue;

                candidates.Add(kind);
            }

            if (candidates.Count == 0) return false;

            string mode = _config.targetSelection;
            bool isSmart = !string.IsNullOrEmpty(mode) && !string.Equals(mode, "random", StringComparison.OrdinalIgnoreCase);
            bool useEngine = string.Equals(mode, "engine", StringComparison.OrdinalIgnoreCase) && TargetEvaluator != null;

            // Up to 4 attempts: pick a candidate uniformly at random
            int attempts = 0;
            while (attempts < 4 && candidates.Count > 0) {
                attempts++;

                int pickIndex = _rng.Next(candidates.Count);
                AbilityKind chosenKind = candidates[pickIndex];

                if (isSmart) {
                    if (TryChooseSmartAction(state, side, chosenKind, useEngine, out GameAction bestAction, out bool endsTurn)) {
                        choice = new AiAbilityChoice {
                            Action = bestAction,
                            Ability = chosenKind,
                            EndsTurn = endsTurn
                        };

                        _hasPendingChoice = true;
                        _pendingChoice = choice;
                        _pendingPreActionCount = state.ActionCount;
                        _pendingSide = side;
                        return true;
                    }

                    candidates.RemoveAt(pickIndex);
                    continue;
                }

                GameAction candidateAction = default(GameAction);
                bool actionFormed = false;

                if (chosenKind == AbilityKind.DoubleMove) {
                    candidateAction = GameAction.DoubleMoveAction();
                    actionFormed = true;
                } else if (chosenKind == AbilityKind.SuperJump) {
                    ulong targets = AttaxGame.GetAbilityTargets(state, side, AbilityKind.SuperJump);
                    if (targets != 0) {
                        int targetCount = BitboardOps.PopCount(targets);
                        int pick = _rng.Next(targetCount);
                        ulong remTargets = targets;
                        int targetCell = -1;
                        for (int k = 0; k <= pick; k++) {
                            targetCell = BitboardOps.TrailingZeroCount(remTargets);
                            remTargets &= remTargets - 1;
                        }

                        int toX = targetCell % 7;
                        int toY = targetCell / 7;

                        // Find friendly chips from which distance is >= 3
                        ulong friendly = (side == PlayerColor.Red) ? state.Board.RedPieces : state.Board.BluePieces;
                        List<(int sx, int sy)> validSources = new List<(int sx, int sy)>();
                        ulong remFriendly = friendly;
                        while (remFriendly > 0) {
                            int sIdx = BitboardOps.TrailingZeroCount(remFriendly);
                            int sx = sIdx % 7;
                            int sy = sIdx / 7;
                            int d = Math.Max(Math.Abs(toX - sx), Math.Abs(toY - sy));
                            if (d >= 3) {
                                validSources.Add((sx, sy));
                            }
                            remFriendly &= remFriendly - 1;
                        }

                        if (validSources.Count > 0) {
                            var source = validSources[_rng.Next(validSources.Count)];
                            candidateAction = GameAction.MoveAction(new Move(source.sx, source.sy, toX, toY));
                            actionFormed = true;
                        }
                    }
                } else {
                    ulong targets = AttaxGame.GetAbilityTargets(state, side, chosenKind);
                    if (targets != 0) {
                        int targetCount = BitboardOps.PopCount(targets);
                        int pick = _rng.Next(targetCount);
                        ulong remTargets = targets;
                        int targetCell = -1;
                        for (int k = 0; k <= pick; k++) {
                            targetCell = BitboardOps.TrailingZeroCount(remTargets);
                            remTargets &= remTargets - 1;
                        }

                        int targetX = targetCell % 7;
                        int targetY = targetCell / 7;
                        ActionKind aKind = (ActionKind)((int)chosenKind - 1);
                        candidateAction = GameAction.Targeted(aKind, targetX, targetY);
                        actionFormed = true;
                    }
                }

                if (!actionFormed) {
                    candidates.RemoveAt(pickIndex);
                    continue;
                }

                if (AttaxGame.Validate(state, side, candidateAction) != ActionError.None) {
                    candidates.RemoveAt(pickIndex);
                    continue;
                }

                if (_config.safetyFilter) {
                    GameState clone = state.Clone();
                    ActionError applyErr = AttaxGame.TryApply(clone, side, candidateAction, out _);
                    TerminalResult lossResult = (side == PlayerColor.Red) ? TerminalResult.BlueWins : TerminalResult.RedWins;
                    if (applyErr != ActionError.None || clone.Result == lossResult) {
                        candidates.RemoveAt(pickIndex);
                        continue;
                    }
                }

                // Accepted candidate
                choice = new AiAbilityChoice {
                    Action = candidateAction,
                    Ability = chosenKind,
                    EndsTurn = (chosenKind == AbilityKind.SuperJump || chosenKind == AbilityKind.SuperClone)
                };

                _hasPendingChoice = true;
                _pendingChoice = choice;
                _pendingPreActionCount = state.ActionCount;
                _pendingSide = side;
                return true;
            }

            return false;
        }

        private bool TryChooseSmartAction(GameState state, PlayerColor side, AbilityKind chosenKind, bool useEngine, out GameAction bestAction, out bool endsTurn) {
            bestAction = default;
            endsTurn = (chosenKind == AbilityKind.SuperJump || chosenKind == AbilityKind.SuperClone);

            List<GameAction> actions = new List<GameAction>();

            if (chosenKind == AbilityKind.DoubleMove) {
                actions.Add(GameAction.DoubleMoveAction());
            } else if (chosenKind == AbilityKind.SuperJump) {
                ulong targets = AttaxGame.GetAbilityTargets(state, side, AbilityKind.SuperJump);
                if (targets == 0) return false;

                ulong friendly = (side == PlayerColor.Red) ? state.Board.RedPieces : state.Board.BluePieces;
                if (friendly == 0) return false;

                ulong remTargets = targets;
                while (remTargets > 0) {
                    int targetCell = BitboardOps.TrailingZeroCount(remTargets);
                    int toX = targetCell % 7;
                    int toY = targetCell / 7;

                    ulong remFriendly = friendly;
                    while (remFriendly > 0) {
                        int sIdx = BitboardOps.TrailingZeroCount(remFriendly);
                        int sx = sIdx % 7;
                        int sy = sIdx / 7;
                        int d = Math.Max(Math.Abs(toX - sx), Math.Abs(toY - sy));
                        if (d >= 3) {
                            actions.Add(GameAction.MoveAction(new Move(sx, sy, toX, toY)));
                        }
                        remFriendly &= remFriendly - 1;
                    }

                    remTargets &= remTargets - 1;
                }
            } else {
                ulong targets = AttaxGame.GetAbilityTargets(state, side, chosenKind);
                if (targets == 0) return false;

                ActionKind aKind = (ActionKind)((int)chosenKind - 1);
                ulong remTargets = targets;
                while (remTargets > 0) {
                    int targetCell = BitboardOps.TrailingZeroCount(remTargets);
                    actions.Add(GameAction.Targeted(aKind, targetCell % 7, targetCell / 7));
                    remTargets &= remTargets - 1;
                }
            }

            if (actions.Count == 0) return false;

            List<(GameAction action, float score, int tie)> scoredCandidates = new List<(GameAction, float, int)>();

            for (int i = 0; i < actions.Count; i++) {
                GameAction action = actions[i];
                if (AttaxGame.Validate(state, side, action) != ActionError.None) continue;

                float score = EvaluateCandidateAction(state, side, action, endsTurn, useEngine);
                if (float.IsNegativeInfinity(score)) continue;

                scoredCandidates.Add((action, score, _rng.Next()));
            }

            if (scoredCandidates.Count == 0) return false;

            scoredCandidates.Sort((a, b) => {
                int cmp = b.score.CompareTo(a.score);
                if (cmp != 0) return cmp;
                return a.tie.CompareTo(b.tie);
            });

            bestAction = scoredCandidates[0].action;
            return true;
        }

        private float EvaluateCandidateAction(GameState state, PlayerColor side, GameAction action, bool endsTurn, bool useEngine) {
            GameState clone = state.Clone();
            ActionError applyErr = AttaxGame.TryApply(clone, side, action, out _);
            if (applyErr != ActionError.None) return float.NegativeInfinity;

            if (_config.safetyFilter) {
                TerminalResult lossResult = (side == PlayerColor.Red) ? TerminalResult.BlueWins : TerminalResult.RedWins;
                if (clone.Result == lossResult) return float.NegativeInfinity;
            }

            float baseScore = ScoreBoard(clone.Board, side, useEngine);
            if (endsTurn || clone.Result != TerminalResult.NotOver || clone.SideToMove != side) {
                return baseScore;
            }

            ulong friendly = (side == PlayerColor.Red) ? clone.Board.RedPieces : clone.Board.BluePieces;
            if (friendly == 0) return baseScore;

            ulong empty = clone.Board.EmptySquares();
            if (empty == 0) return baseScore;

            bool hasAnyMove = false;
            float bestFollowUp = float.NegativeInfinity;

            // 1. Clones to reachable adjacent empty squares (canonical by destination)
            ulong cloneTargets = 0UL;
            ulong remFriendly = friendly;
            while (remFriendly > 0) {
                int fIdx = BitboardOps.TrailingZeroCount(remFriendly);
                cloneTargets |= BoardLookup.SingleStepMoves[fIdx];
                remFriendly &= remFriendly - 1;
            }
            cloneTargets &= empty;

            while (cloneTargets > 0) {
                int toIndex = BitboardOps.TrailingZeroCount(cloneTargets);
                ulong sources = BoardLookup.SingleStepMoves[toIndex] & friendly;
                int fromIndex = BitboardOps.TrailingZeroCount(sources);
                Move move = new Move(fromIndex % 7, fromIndex / 7, toIndex % 7, toIndex / 7);

                BitboardState testBoard = clone.Board.Clone();
                AttaxRules.MakeMoveMasks(testBoard, move, side, out _);
                float s = ScoreBoard(testBoard, side, useEngine);
                if (s > bestFollowUp) bestFollowUp = s;
                hasAnyMove = true;

                cloneTargets &= cloneTargets - 1;
            }

            // 2. Jumps to reachable 2-step empty squares
            remFriendly = friendly;
            while (remFriendly > 0) {
                int fromIndex = BitboardOps.TrailingZeroCount(remFriendly);
                ulong jumpTargets = BoardLookup.TwoStepMoves[fromIndex] & empty;
                while (jumpTargets > 0) {
                    int toIndex = BitboardOps.TrailingZeroCount(jumpTargets);
                    Move move = new Move(fromIndex % 7, fromIndex / 7, toIndex % 7, toIndex / 7);

                    BitboardState testBoard = clone.Board.Clone();
                    AttaxRules.MakeMoveMasks(testBoard, move, side, out _);
                    float s = ScoreBoard(testBoard, side, useEngine);
                    if (s > bestFollowUp) bestFollowUp = s;
                    hasAnyMove = true;

                    jumpTargets &= jumpTargets - 1;
                }
                remFriendly &= remFriendly - 1;
            }

            return hasAnyMove ? bestFollowUp : baseScore;
        }

        private float ScoreBoard(BitboardState board, PlayerColor side, bool useEngine) {
            if (useEngine && TargetEvaluator != null) {
                return TargetEvaluator(board, side);
            }
            int red = BitboardOps.PopCount(board.RedPieces);
            int blue = BitboardOps.PopCount(board.BluePieces);
            int myPieces = (side == PlayerColor.Red) ? red : blue;
            int enemyPieces = (side == PlayerColor.Red) ? blue : red;
            if (enemyPieces == 0 && myPieces > 0) return 10000f + myPieces;
            return myPieces - enemyPieces;
        }

        public bool RecordAppliedAbility(GameState state, in ActionOutcome outcome) {
            if (!_hasPendingChoice || state == null) return false;
            if (outcome.Actor != _pendingSide) return false;
            if (outcome.Ability != _pendingChoice.Ability) return false;
            if (outcome.Kind != _pendingChoice.Action.Kind) return false;
            if (state.ActionCount != _pendingPreActionCount + 1) return false;

            _hasPendingChoice = false;
            _pendingChoice = default(AiAbilityChoice);
            _pendingPreActionCount = -1;
            _pendingSide = PlayerColor.None;
            UsesThisGame++;
            return true;
        }
    }
}
