using System;
using System.Collections.Generic;
using System.IO;
using Attax.Core.Utils;
using Xunit;

namespace Attax.Core.Tests {
    public class AttaxGameInvariantAndAllocationTests {
        private static GameRulesConfig CreateRichRules() {
            return new GameRulesConfig {
                schemaVersion = 1,
                economy = new EconomyRules {
                    enabled = true,
                    startingCredits = 1500,
                    maxCredits = 3000,
                    captureReward = 10,
                    footprintReward = 5,
                    abilityCosts = new AbilityCosts {
                        superJump = 20, convert = 20, block = 20, wall = 20, doubleMove = 20,
                        spawn = 20, superClone = 20, unblock = 20, bomb = 20
                    }
                },
                turn = new TurnRules { maxAbilityActionsPerTurn = 1 },
                playerAbilities = new PlayerAbilitiesConfig {
                    red = new AbilityToggles {
                        superJump = true, convert = true, block = true, wall = true,
                        doubleMove = true, spawn = true, superClone = true, unblock = true, bomb = true
                    },
                    blue = new AbilityToggles {
                        superJump = true, convert = true, block = true, wall = true,
                        doubleMove = true, spawn = true, superClone = true, unblock = true, bomb = true
                    }
                }
            };
        }

        [Fact]
        public void Invariants_300SeededPlayouts_HoldAfterEveryAction() {
            var rules = CreateRichRules();

            for (int seed = 1; seed <= 300; seed++) {
                var rng = new Random(seed);
                var state = AttaxGame.NewGame(rules, new StartOptions(seed % 5, seed, PlayerColor.Red));

                int maxPlies = 60;
                int plies = 0;

                while (plies < maxPlies && state.Result == TerminalResult.NotOver) {
                    var actor = state.SideToMove;
                    int preRedCredits = state.RedCredits;
                    int preBlueCredits = state.BlueCredits;

                    // Randomly try an ability if affordable and not yet used this turn
                    if (state.AbilityActionsUsedThisTurn < 1 && rng.Next(100) < 40) {
                        int avail = AttaxGame.GetAvailableAbilities(state);
                        var usableKinds = new List<AbilityKind>();
                        for (int k = 1; k <= 9; k++) {
                            if ((avail & (1 << k)) != 0) usableKinds.Add((AbilityKind)k);
                        }

                        if (usableKinds.Count > 0) {
                            var pickedKind = usableKinds[rng.Next(usableKinds.Count)];
                            GameAction abilityAction;
                            if (pickedKind == AbilityKind.DoubleMove) {
                                abilityAction = GameAction.DoubleMoveAction();
                            } else {
                                ulong targets = AttaxGame.GetAbilityTargets(state, pickedKind);
                                if (targets != 0) {
                                    int count = BitboardOps.PopCount(targets);
                                    int pick = rng.Next(count);
                                    ulong rem = targets;
                                    int cell = -1;
                                    for (int i = 0; i <= pick; i++) {
                                        cell = BitboardOps.TrailingZeroCount(rem);
                                        rem &= rem - 1;
                                    }

                                    if (pickedKind == AbilityKind.SuperJump) {
                                        ulong friendly = (actor == PlayerColor.Red) ? state.Board.RedPieces : state.Board.BluePieces;
                                        var sources = new List<(int, int)>();
                                        ulong remF = friendly;
                                        while (remF > 0) {
                                            int sIdx = BitboardOps.TrailingZeroCount(remF);
                                            int sx = sIdx % 7, sy = sIdx / 7;
                                            int tx = cell % 7, ty = cell / 7;
                                            if (Math.Max(Math.Abs(tx - sx), Math.Abs(ty - sy)) >= 3) {
                                                sources.Add((sx, sy));
                                            }
                                            remF &= remF - 1;
                                        }

                                        if (sources.Count > 0) {
                                            var (fromX, fromY) = sources[rng.Next(sources.Count)];
                                            abilityAction = GameAction.MoveAction(new Move(fromX, fromY, cell % 7, cell / 7));
                                        } else {
                                            abilityAction = default;
                                        }
                                    } else {
                                        ActionKind aKind = (ActionKind)((int)pickedKind - 1);
                                        abilityAction = GameAction.Targeted(aKind, cell % 7, cell / 7);
                                    }

                                    if (abilityAction.Kind != ActionKind.Move || abilityAction.Move.FromX != abilityAction.Move.ToX || abilityAction.Move.FromY != abilityAction.Move.ToY) {
                                        if (AttaxGame.Validate(state, actor, abilityAction) == ActionError.None) {
                                            var err = AttaxGame.TryApply(state, actor, abilityAction, out var outcome);
                                            Assert.Equal(ActionError.None, err);
                                            Assert.True(state.AreInvariantsValid());

                                            // Exact credit accounting
                                            int actorDelta = state.GetCredits(actor) - (actor == PlayerColor.Red ? preRedCredits : preBlueCredits);
                                            int victimDelta = state.GetCredits(AttaxRules.SwitchPlayer(actor)) - (actor == PlayerColor.Red ? preBlueCredits : preRedCredits);
                                            Assert.Equal(-outcome.CreditsSpent + outcome.ActorCreditsGained, actorDelta);
                                            Assert.Equal(outcome.VictimCreditsGained, victimDelta);

                                            if (outcome.TurnAdvanced) {
                                                plies++;
                                                continue;
                                            }
                                            if (state.Result != TerminalResult.NotOver) break;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Standard clone/jump move
                    var legalMoves = new List<Move>();
                    for (int fy = 0; fy < 7; fy++) {
                        for (int fx = 0; fx < 7; fx++) {
                            ulong targets = AttaxGame.GetMoveTargets(state, fx, fy, false);
                            while (targets > 0) {
                                int tIdx = BitboardOps.TrailingZeroCount(targets);
                                legalMoves.Add(new Move(fx, fy, tIdx % 7, tIdx / 7));
                                targets &= targets - 1;
                            }
                        }
                    }

                    if (legalMoves.Count == 0) {
                        break;
                    }

                    var move = legalMoves[rng.Next(legalMoves.Count)];
                    preRedCredits = state.RedCredits;
                    preBlueCredits = state.BlueCredits;

                    var moveErr = AttaxGame.TryApply(state, actor, GameAction.MoveAction(move), out var moveOutcome);
                    Assert.Equal(ActionError.None, moveErr);
                    Assert.True(state.AreInvariantsValid());

                    // Re-test GameState.Restore matches fingerprint
                    var restored = GameState.Restore(
                        state.Board.RedPieces, state.Board.BluePieces, state.Board.BlockedSquares,
                        state.RedFootprints, state.BlueFootprints, state.SideToMove,
                        state.RedCredits, state.BlueCredits, state.TurnNumber,
                        state.ActionCount, state.MovesRemainingThisTurn, state.AbilityActionsUsedThisTurn,
                        state.ExportRules()
                    );
                    Assert.Equal(state.ComputeFingerprint(), restored.ComputeFingerprint());

                    plies++;
                }
            }
        }

        [Fact]
        public void ZeroAllocations_OnHotPaths_AfterWarmup() {
            var rules = CreateRichRules();
            // Setup rich position where all 9 abilities are valid:
            // Red at (0,0), Blue at (1,1), Blocked at (2,2), empty cells available
            ulong redPieces = 1UL << BoardLookup.GetBitIndex(0, 0);
            ulong bluePieces = (1UL << BoardLookup.GetBitIndex(1, 1)) | (1UL << BoardLookup.GetBitIndex(6, 6));
            ulong blocked = 1UL << BoardLookup.GetBitIndex(2, 2);
            var state = GameState.Restore(redPieces, bluePieces, blocked, 0UL, 0UL, PlayerColor.Red, 2000, 2000, 0, 0, 1, 0, rules);

            // Warm up JIT and static tables for all operations
            for (int i = 0; i < 50; i++) {
                var s = state.Clone();
                AttaxGame.Validate(s, PlayerColor.Red, GameAction.MoveAction(new Move(0, 0, 0, 1)));
                AttaxGame.TryApply(s.Clone(), PlayerColor.Red, GameAction.MoveAction(new Move(0, 0, 0, 1)), out _);
                AttaxGame.TryApply(s.Clone(), PlayerColor.Red, GameAction.MoveAction(new Move(0, 0, 3, 3)), out _);
                AttaxGame.TryApply(s.Clone(), PlayerColor.Red, GameAction.Targeted(ActionKind.Convert, 1, 1), out _);
                AttaxGame.TryApply(s.Clone(), PlayerColor.Red, GameAction.Targeted(ActionKind.Block, 1, 1), out _);
                AttaxGame.TryApply(s.Clone(), PlayerColor.Red, GameAction.Targeted(ActionKind.Wall, 3, 3), out _);
                AttaxGame.TryApply(s.Clone(), PlayerColor.Red, GameAction.DoubleMoveAction(), out _);
                AttaxGame.TryApply(s.Clone(), PlayerColor.Red, GameAction.Targeted(ActionKind.Spawn, 3, 3), out _);
                AttaxGame.TryApply(s.Clone(), PlayerColor.Red, GameAction.Targeted(ActionKind.SuperClone, 3, 3), out _);
                AttaxGame.TryApply(s.Clone(), PlayerColor.Red, GameAction.Targeted(ActionKind.Unblock, 2, 2), out _);
                AttaxGame.TryApply(s.Clone(), PlayerColor.Red, GameAction.Targeted(ActionKind.Bomb, 1, 1), out _);
                AttaxGame.GetMoveTargets(s, 0, 0, false);
                AttaxGame.GetMoveTargets(s, 0, 0, true);
                AttaxGame.GetAbilityTargets(s, AbilityKind.Wall);
                AttaxGame.CanUseAbility(s, AbilityKind.Wall);
                AttaxGame.GetAvailableAbilities(s);
                AttaxGame.HasStandardMove(s, PlayerColor.Red);
                AttaxGame.GetScore(s, PlayerColor.Red);
            }

            // Force GC settle
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // 1. Measure Validate
            {
                var action = GameAction.MoveAction(new Move(0, 0, 0, 1));
                long before = GC.GetAllocatedBytesForCurrentThread();
                var err = AttaxGame.Validate(state, PlayerColor.Red, action);
                long after = GC.GetAllocatedBytesForCurrentThread();
                Assert.Equal(ActionError.None, err);
                Assert.Equal(0, after - before);
            }

            // 2. Measure TryApply for normal Move
            {
                var s = state.Clone();
                var action = GameAction.MoveAction(new Move(0, 0, 0, 1));
                long before = GC.GetAllocatedBytesForCurrentThread();
                var err = AttaxGame.TryApply(s, PlayerColor.Red, action, out _);
                long after = GC.GetAllocatedBytesForCurrentThread();
                Assert.Equal(ActionError.None, err);
                Assert.Equal(0, after - before);
            }

            // 3. Measure TryApply for SuperJump
            {
                var s = state.Clone();
                var action = GameAction.MoveAction(new Move(0, 0, 3, 3));
                long before = GC.GetAllocatedBytesForCurrentThread();
                var err = AttaxGame.TryApply(s, PlayerColor.Red, action, out _);
                long after = GC.GetAllocatedBytesForCurrentThread();
                Assert.Equal(ActionError.None, err);
                Assert.Equal(0, after - before);
            }

            // 4. Measure TryApply for Convert
            {
                var s = state.Clone();
                var action = GameAction.Targeted(ActionKind.Convert, 1, 1);
                long before = GC.GetAllocatedBytesForCurrentThread();
                var err = AttaxGame.TryApply(s, PlayerColor.Red, action, out _);
                long after = GC.GetAllocatedBytesForCurrentThread();
                Assert.Equal(ActionError.None, err);
                Assert.Equal(0, after - before);
            }

            // 5. Measure TryApply for Block
            {
                var s = state.Clone();
                var action = GameAction.Targeted(ActionKind.Block, 1, 1);
                long before = GC.GetAllocatedBytesForCurrentThread();
                var err = AttaxGame.TryApply(s, PlayerColor.Red, action, out _);
                long after = GC.GetAllocatedBytesForCurrentThread();
                Assert.Equal(ActionError.None, err);
                Assert.Equal(0, after - before);
            }

            // 6. Measure TryApply for Wall
            {
                var s = state.Clone();
                var action = GameAction.Targeted(ActionKind.Wall, 3, 3);
                long before = GC.GetAllocatedBytesForCurrentThread();
                var err = AttaxGame.TryApply(s, PlayerColor.Red, action, out _);
                long after = GC.GetAllocatedBytesForCurrentThread();
                Assert.Equal(ActionError.None, err);
                Assert.Equal(0, after - before);
            }

            // 7. Measure TryApply for DoubleMove
            {
                var s = state.Clone();
                var action = GameAction.DoubleMoveAction();
                long before = GC.GetAllocatedBytesForCurrentThread();
                var err = AttaxGame.TryApply(s, PlayerColor.Red, action, out _);
                long after = GC.GetAllocatedBytesForCurrentThread();
                Assert.Equal(ActionError.None, err);
                Assert.Equal(0, after - before);
            }

            // 8. Measure TryApply for Spawn
            {
                var s = state.Clone();
                var action = GameAction.Targeted(ActionKind.Spawn, 3, 3);
                long before = GC.GetAllocatedBytesForCurrentThread();
                var err = AttaxGame.TryApply(s, PlayerColor.Red, action, out _);
                long after = GC.GetAllocatedBytesForCurrentThread();
                Assert.Equal(ActionError.None, err);
                Assert.Equal(0, after - before);
            }

            // 9. Measure TryApply for SuperClone
            {
                var s = state.Clone();
                var action = GameAction.Targeted(ActionKind.SuperClone, 3, 3);
                long before = GC.GetAllocatedBytesForCurrentThread();
                var err = AttaxGame.TryApply(s, PlayerColor.Red, action, out _);
                long after = GC.GetAllocatedBytesForCurrentThread();
                Assert.Equal(ActionError.None, err);
                Assert.Equal(0, after - before);
            }

            // 10. Measure TryApply for Unblock
            {
                var s = state.Clone();
                var action = GameAction.Targeted(ActionKind.Unblock, 2, 2);
                long before = GC.GetAllocatedBytesForCurrentThread();
                var err = AttaxGame.TryApply(s, PlayerColor.Red, action, out _);
                long after = GC.GetAllocatedBytesForCurrentThread();
                Assert.Equal(ActionError.None, err);
                Assert.Equal(0, after - before);
            }

            // 11. Measure TryApply for Bomb
            {
                var s = state.Clone();
                var action = GameAction.Targeted(ActionKind.Bomb, 1, 1);
                long before = GC.GetAllocatedBytesForCurrentThread();
                var err = AttaxGame.TryApply(s, PlayerColor.Red, action, out _);
                long after = GC.GetAllocatedBytesForCurrentThread();
                Assert.Equal(ActionError.None, err);
                Assert.Equal(0, after - before);
            }

            // 12. Measure queries
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                ulong moveTargets = AttaxGame.GetMoveTargets(state, 0, 0, true);
                ulong abilityTargets = AttaxGame.GetAbilityTargets(state, AbilityKind.Wall);
                bool canUse = AttaxGame.CanUseAbility(state, AbilityKind.Wall);
                int avail = AttaxGame.GetAvailableAbilities(state);
                bool hasMove = AttaxGame.HasStandardMove(state, PlayerColor.Red);
                int score = AttaxGame.GetScore(state, PlayerColor.Red);
                long after = GC.GetAllocatedBytesForCurrentThread();
                Assert.NotEqual(0UL, moveTargets);
                Assert.NotEqual(0UL, abilityTargets);
                Assert.True(canUse);
                Assert.NotEqual(0, avail);
                Assert.True(hasMove);
                Assert.Equal(1, score);
                Assert.Equal(0, after - before);
            }
        }

        [Fact]
        public void OutputDirectory_ContainsBothRulesJsonFiles() {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            Assert.True(File.Exists(Path.Combine(baseDir, "game-rules.json")), "game-rules.json must be present in output");
            Assert.True(File.Exists(Path.Combine(baseDir, "ai-ability-config.json")), "ai-ability-config.json must be present in output");
        }
    }
}
