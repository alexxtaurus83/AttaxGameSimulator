using System;
using System.Collections.Generic;
using Attax.Core.Utils;
using Xunit;

namespace Attax.Core.Tests {
    public class AttaxGameTests {
        private static GameRulesConfig CreateTestRules(int startingCredits = 1000, int maxCredits = 3000) {
            return new GameRulesConfig {
                schemaVersion = 1,
                economy = new EconomyRules {
                    enabled = true,
                    startingCredits = startingCredits,
                    maxCredits = maxCredits,
                    captureReward = 10,
                    footprintReward = 5,
                    abilityCosts = new AbilityCosts {
                        superJump = 100, convert = 100, block = 100, wall = 100,
                        doubleMove = 100, spawn = 100, superClone = 100, unblock = 100, bomb = 100
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
        public void NewGame_InitializesBoardAndStateCleanly() {
            var rules = CreateTestRules(500);
            var state = AttaxGame.NewGame(rules, new StartOptions(3, 42, PlayerColor.Red));

            Assert.Equal(PlayerColor.Red, state.SideToMove);
            Assert.Equal(500, state.RedCredits);
            Assert.Equal(500, state.BlueCredits);
            Assert.Equal(0, state.TurnNumber);
            Assert.Equal(0, state.ActionCount);
            Assert.Equal(1, state.MovesRemainingThisTurn);
            Assert.Equal(0, state.AbilityActionsUsedThisTurn);
            Assert.Equal(TerminalResult.NotOver, state.Result);
            Assert.Equal(3, BitboardOps.PopCount(state.Board.BlockedSquares));
            Assert.True(state.IsHashConsistent());
            Assert.True(state.AreInvariantsValid());
        }

        [Fact]
        public void NewGame_EdgeCasesAndInputValidation() {
            var rules = CreateTestRules();

            // Null rules
            Assert.Throws<ArgumentNullException>(() => AttaxGame.NewGame(null, new StartOptions(0, 0)));

            // Null start
            Assert.Throws<ArgumentNullException>(() => AttaxGame.NewGame(rules, null));

            // Invalid FirstPlayer
            Assert.Throws<ArgumentException>(() => AttaxGame.NewGame(rules, new StartOptions(0, 0, PlayerColor.None)));
            Assert.Throws<ArgumentException>(() => AttaxGame.NewGame(rules, new StartOptions(0, 0, PlayerColor.Blocked)));

            // BlockedCells out of range
            Assert.Throws<ArgumentException>(() => AttaxGame.NewGame(rules, new StartOptions(-1, 0)));
            Assert.Throws<ArgumentException>(() => AttaxGame.NewGame(rules, new StartOptions(13, 0)));

            // Blue first player
            var blueGame = AttaxGame.NewGame(rules, new StartOptions(0, 42, PlayerColor.Blue));
            Assert.Equal(PlayerColor.Blue, blueGame.SideToMove);
            Assert.True(blueGame.IsHashConsistent());
            Assert.NotEqual(StartPosition.Standard(PlayerColor.Red).ZobristHash, blueGame.Board.ZobristHash);
        }

        [Fact]
        public void ValidationMatrix_CoversActionErrors() {
            var rules = CreateTestRules(1000);
            var state = AttaxGame.NewGame(rules, new StartOptions(0, 0, PlayerColor.Red));

            // 1. GameOver (1)
            var overState = state.Clone();
            overState.Result = TerminalResult.RedWins;
            Assert.Equal(ActionError.GameOver, AttaxGame.Validate(overState, PlayerColor.Red, GameAction.MoveAction(new Move(0, 0, 1, 1))));

            // 2. NotYourTurn (2)
            Assert.Equal(ActionError.NotYourTurn, AttaxGame.Validate(state, PlayerColor.Blue, GameAction.MoveAction(new Move(0, 0, 1, 1))));

            // 3. OutOfBounds (3)
            Assert.Equal(ActionError.OutOfBounds, AttaxGame.Validate(state, PlayerColor.Red, GameAction.MoveAction(new Move(-1, 0, 1, 1))));
            Assert.Equal(ActionError.OutOfBounds, AttaxGame.Validate(state, PlayerColor.Red, GameAction.Targeted(ActionKind.Convert, 7, 0)));

            // 4. NotYourPiece (4)
            Assert.Equal(ActionError.NotYourPiece, AttaxGame.Validate(state, PlayerColor.Red, GameAction.MoveAction(new Move(0, 6, 1, 6)))); // (0,6) is Blue

            // 5. DestinationNotEmpty (5)
            Assert.Equal(ActionError.DestinationNotEmpty, AttaxGame.Validate(state, PlayerColor.Red, GameAction.MoveAction(new Move(0, 0, 0, 6)))); // (0,6) occupied by Blue
            Assert.Equal(ActionError.DestinationNotEmpty, AttaxGame.Validate(state, PlayerColor.Red, GameAction.Targeted(ActionKind.Wall, 0, 0))); // (0,0) occupied

            // 6. SameSquare (6)
            Assert.Equal(ActionError.SameSquare, AttaxGame.Validate(state, PlayerColor.Red, GameAction.MoveAction(new Move(0, 0, 0, 0))));

            // 7. NotEnemyChip (8)
            Assert.Equal(ActionError.NotEnemyChip, AttaxGame.Validate(state, PlayerColor.Red, GameAction.Targeted(ActionKind.Convert, 3, 3))); // (3,3) empty
            Assert.Equal(ActionError.NotEnemyChip, AttaxGame.Validate(state, PlayerColor.Red, GameAction.Targeted(ActionKind.Convert, 0, 0))); // (0,0) friendly

            // 8. NotBlockedCell (9)
            Assert.Equal(ActionError.NotBlockedCell, AttaxGame.Validate(state, PlayerColor.Red, GameAction.Targeted(ActionKind.Unblock, 3, 3))); // (3,3) empty

            // 9. InsufficientCredits (10)
            var poorState = state.Clone();
            poorState.RedCredits = 50; // cost is 100
            Assert.Equal(ActionError.InsufficientCredits, AttaxGame.Validate(poorState, PlayerColor.Red, GameAction.Targeted(ActionKind.Wall, 3, 3)));

            // 10. AbilityLimitReachedThisTurn (11)
            var usedState = state.Clone();
            usedState.AbilityActionsUsedThisTurn = 1;
            Assert.Equal(ActionError.AbilityLimitReachedThisTurn, AttaxGame.Validate(usedState, PlayerColor.Red, GameAction.Targeted(ActionKind.Wall, 3, 3)));

            // 11. DoubleMoveAlreadyActive (12)
            var doubleActiveState = state.Clone();
            doubleActiveState.MovesRemainingThisTurn = 2;
            Assert.Equal(ActionError.DoubleMoveAlreadyActive, AttaxGame.Validate(doubleActiveState, PlayerColor.Red, GameAction.DoubleMoveAction()));

            // 12. UnknownAction (13)
            var badKindAction = new GameAction { Kind = (ActionKind)99 };
            Assert.Equal(ActionError.UnknownAction, AttaxGame.Validate(state, PlayerColor.Red, badKindAction));

            // 13. AbilityDisabled (14) - Via price == 0 or toggle == false
            var disabledRules = CreateTestRules();
            disabledRules.economy.abilityCosts.wall = 0; // disabled globally
            var disState = AttaxGame.NewGame(disabledRules, new StartOptions(0, 0, PlayerColor.Red));
            Assert.Equal(ActionError.AbilityDisabled, AttaxGame.Validate(disState, PlayerColor.Red, GameAction.Targeted(ActionKind.Wall, 3, 3)));

            // 14. AbilityDisabled per player toggle
            var perPlayerRules = CreateTestRules();
            perPlayerRules.playerAbilities.red.bomb = false; // Red cannot Bomb, Blue can
            var ppState = AttaxGame.NewGame(perPlayerRules, new StartOptions(0, 0, PlayerColor.Red));
            // Red at (0,0) and (6,6), Blue at (0,6) and (6,0). Red trying Bomb on enemy at (0,6) gives AbilityDisabled:
            Assert.Equal(ActionError.AbilityDisabled, AttaxGame.Validate(ppState, PlayerColor.Red, GameAction.Targeted(ActionKind.Bomb, 0, 6)));
            // But Blue has Bomb enabled:
            Assert.True(ppState.ExportRules().IsAbilityEnabled(PlayerColor.Blue, AbilityKind.Bomb));
            Assert.False(ppState.ExportRules().IsAbilityEnabled(PlayerColor.Red, AbilityKind.Bomb));

            // 15. CounterOverflow (15)
            var ovfState = state.Clone();
            ovfState.ActionCount = int.MaxValue;
            Assert.Equal(ActionError.CounterOverflow, AttaxGame.Validate(ovfState, PlayerColor.Red, GameAction.MoveAction(new Move(0, 0, 1, 1))));

            var turnOvfState = state.Clone();
            turnOvfState.TurnNumber = int.MaxValue;
            turnOvfState.MovesRemainingThisTurn = 1;
            Assert.Equal(ActionError.CounterOverflow, AttaxGame.Validate(turnOvfState, PlayerColor.Red, GameAction.MoveAction(new Move(0, 0, 1, 1))));

            // Spawn at TurnNumber == int.MaxValue does NOT overflow turn number
            Assert.Equal(ActionError.None, AttaxGame.Validate(turnOvfState, PlayerColor.Red, GameAction.Targeted(ActionKind.Spawn, 3, 3)));
        }

        [Fact]
        public void ValidationPrecedence_FollowsSpecHierarchy() {
            var rules = CreateTestRules(0); // 0 credits
            rules.economy.abilityCosts.wall = 0; // Wall disabled

            var state = AttaxGame.NewGame(rules, new StartOptions(0, 0, PlayerColor.Red));
            state.Result = TerminalResult.RedWins;

            // GameOver precedes NotYourTurn
            Assert.Equal(ActionError.GameOver, AttaxGame.Validate(state, PlayerColor.Blue, GameAction.Targeted(ActionKind.Wall, 0, 0)));

            // NotYourTurn precedes target checks
            state.Result = TerminalResult.NotOver;
            Assert.Equal(ActionError.NotYourTurn, AttaxGame.Validate(state, PlayerColor.Blue, GameAction.Targeted(ActionKind.Wall, 0, 0)));

            // Target check precedes AbilityDisabled / InsufficientCredits
            // (0,0) is occupied; Wall target not empty
            Assert.Equal(ActionError.DestinationNotEmpty, AttaxGame.Validate(state, PlayerColor.Red, GameAction.Targeted(ActionKind.Wall, 0, 0)));

            // AbilityDisabled precedes InsufficientCredits
            // (3,3) is empty, Wall cost is 0 (disabled), balance is 0
            Assert.Equal(ActionError.AbilityDisabled, AttaxGame.Validate(state, PlayerColor.Red, GameAction.Targeted(ActionKind.Wall, 3, 3)));

            // InsufficientCredits precedes AbilityLimitReachedThisTurn
            var activeRules = CreateTestRules(50); // Wall costs 100, have 50
            var s = AttaxGame.NewGame(activeRules, new StartOptions(0, 0, PlayerColor.Red));
            s.AbilityActionsUsedThisTurn = 1;
            Assert.Equal(ActionError.InsufficientCredits, AttaxGame.Validate(s, PlayerColor.Red, GameAction.Targeted(ActionKind.Wall, 3, 3)));

            // DoubleMoveAlreadyActive precedes resource checks
            var doubleState = s.Clone();
            doubleState.MovesRemainingThisTurn = 2;
            doubleState.RedCredits = 0; // cannot afford, but active check fires first
            Assert.Equal(ActionError.DoubleMoveAlreadyActive, AttaxGame.Validate(doubleState, PlayerColor.Red, GameAction.DoubleMoveAction()));
        }

        [Fact]
        public void StateUntouchedOnAllActionErrors() {
            var rules = CreateTestRules(50); // 50 credits (cannot afford 100)
            rules.playerAbilities.red.bomb = false;
            var state = AttaxGame.NewGame(rules, new StartOptions(0, 0, PlayerColor.Red));
            ulong baseFp = state.ComputeFingerprint();

            var errorActions = new List<GameAction> {
                GameAction.MoveAction(new Move(-1, 0, 1, 1)), // OutOfBounds
                GameAction.MoveAction(new Move(0, 6, 1, 6)),  // NotYourPiece
                GameAction.MoveAction(new Move(0, 0, 0, 6)),  // DestinationNotEmpty
                GameAction.MoveAction(new Move(0, 0, 0, 0)),  // SameSquare
                GameAction.Targeted(ActionKind.Convert, 3, 3),// NotEnemyChip
                GameAction.Targeted(ActionKind.Unblock, 3, 3),// NotBlockedCell
                GameAction.Targeted(ActionKind.Wall, 3, 3),   // InsufficientCredits
                GameAction.Targeted(ActionKind.Bomb, 6, 6),   // AbilityDisabled (via player toggle)
                new GameAction { Kind = (ActionKind)99 }      // UnknownAction
            };

            foreach (var act in errorActions) {
                var err = AttaxGame.TryApply(state, PlayerColor.Red, act, out var outcome);
                Assert.NotEqual(ActionError.None, err);
                Assert.Equal(default(ActionOutcome), outcome);
                Assert.Equal(baseFp, state.ComputeFingerprint());
            }

            // NotYourTurn
            var notTurnErr = AttaxGame.TryApply(state, PlayerColor.Blue, GameAction.MoveAction(new Move(0, 6, 1, 6)), out var notTurnOutcome);
            Assert.Equal(ActionError.NotYourTurn, notTurnErr);
            Assert.Equal(default(ActionOutcome), notTurnOutcome);
            Assert.Equal(baseFp, state.ComputeFingerprint());

            // GameOver
            var overState = state.Clone();
            overState.Result = TerminalResult.RedWins;
            ulong overFp = overState.ComputeFingerprint();
            var overErr = AttaxGame.TryApply(overState, PlayerColor.Red, GameAction.MoveAction(new Move(0, 0, 1, 1)), out var overOutcome);
            Assert.Equal(ActionError.GameOver, overErr);
            Assert.Equal(default(ActionOutcome), overOutcome);
            Assert.Equal(overFp, overState.ComputeFingerprint());

            // DoubleMoveAlreadyActive
            var dmState = state.Clone();
            dmState.MovesRemainingThisTurn = 2;
            dmState.AbilityActionsUsedThisTurn = 1;
            ulong dmFp = dmState.ComputeFingerprint();
            var dmErr = AttaxGame.TryApply(dmState, PlayerColor.Red, GameAction.DoubleMoveAction(), out var dmOutcome);
            Assert.Equal(ActionError.DoubleMoveAlreadyActive, dmErr);
            Assert.Equal(default(ActionOutcome), dmOutcome);
            Assert.Equal(dmFp, dmState.ComputeFingerprint());
        }

        [Fact]
        public void EconomyDisabledOrZeroCost_DisablesAbilitiesCompletely() {
            var disabledRules = CreateTestRules();
            disabledRules.economy.enabled = false;
            var state = AttaxGame.NewGame(disabledRules, new StartOptions(0, 0, PlayerColor.Red));

            Assert.Equal(0, AttaxGame.GetAvailableAbilities(state));
            for (int k = 1; k <= 9; k++) {
                Assert.False(AttaxGame.CanUseAbility(state, (AbilityKind)k));
                Assert.Equal(0UL, AttaxGame.GetAbilityTargets(state, (AbilityKind)k));
            }

            var zeroCostRules = CreateTestRules();
            zeroCostRules.economy.abilityCosts = new AbilityCosts(); // all 0
            var zState = AttaxGame.NewGame(zeroCostRules, new StartOptions(0, 0, PlayerColor.Red));

            Assert.Equal(0, AttaxGame.GetAvailableAbilities(zState));
            for (int k = 1; k <= 9; k++) {
                Assert.False(AttaxGame.CanUseAbility(zState, (AbilityKind)k));
            }
        }

        [Fact]
        public void TerminalDecisions_D1_D2_D5() {
            var rules = CreateTestRules(1000);

            // D1/D2: Wall leaves opponent without legal moves -> ends game immediately
            // Red has piece at (0,0), Blue has single piece at (6,6).
            // All empty adjacent cells to (6,6) blocked except (5,5).
            // Red places Wall at (5,5), stranding Blue completely!
            ulong redPieces = 1UL << BoardLookup.GetBitIndex(0, 0);
            ulong bluePieces = 1UL << BoardLookup.GetBitIndex(6, 6);
            ulong blocked = 0UL;
            // Block all single and two-step targets from (6,6) except (5,5)
            int[] cellsToBlock = new[] {
                BoardLookup.GetBitIndex(5, 6),
                BoardLookup.GetBitIndex(6, 5),
                BoardLookup.GetBitIndex(4, 4),
                BoardLookup.GetBitIndex(4, 5),
                BoardLookup.GetBitIndex(4, 6),
                BoardLookup.GetBitIndex(5, 4),
                BoardLookup.GetBitIndex(6, 4)
            };
            foreach (var idx in cellsToBlock) {
                blocked |= 1UL << idx;
            }

            var s1 = GameState.Restore(redPieces, bluePieces, blocked, 0UL, 0UL, PlayerColor.Red, 1000, 1000, 0, 0, 1, 0, rules);
            Assert.True(AttaxGame.WouldEndGame(s1, PlayerColor.Red, GameAction.Targeted(ActionKind.Wall, 5, 5)));

            var err1 = AttaxGame.TryApply(s1, PlayerColor.Red, GameAction.Targeted(ActionKind.Wall, 5, 5), out var out1);
            Assert.Equal(ActionError.None, err1);
            Assert.Equal(TerminalResult.RedWins, s1.Result);
            Assert.Equal(TerminalResult.RedWins, out1.Result);

            // D5: DoubleMove where first move ends game
            // Red has piece at (0,0), Blue has single piece at (1,1).
            // Red arms DoubleMove, then first move clones to (0,1) flipping (1,1) -> Blue wiped out!
            var s2 = GameState.Restore(redPieces, 1UL << BoardLookup.GetBitIndex(1, 1), 0UL, 0UL, 0UL, PlayerColor.Red, 1000, 1000, 0, 0, 1, 0, rules);
            AttaxGame.TryApply(s2, PlayerColor.Red, GameAction.DoubleMoveAction(), out _);
            Assert.Equal(2, s2.MovesRemainingThisTurn);

            var err2 = AttaxGame.TryApply(s2, PlayerColor.Red, GameAction.MoveAction(new Move(0, 0, 0, 1)), out var out2);
            Assert.Equal(ActionError.None, err2);
            Assert.Equal(TerminalResult.RedWins, s2.Result);
            Assert.Equal(TerminalResult.RedWins, out2.Result);
        }

        [Fact]
        public void QueryConsistency_TargetsMatchValidationAndExistingEngine() {
            var rules = CreateTestRules(1000);
            var state = AttaxGame.NewGame(rules, new StartOptions(2, 42, PlayerColor.Red));

            // Compare GetMoveTargets with AttaxRules.GetValidMovesFrom
            for (int y = 0; y < 7; y++) {
                for (int x = 0; x < 7; x++) {
                    ulong moveTargetsNormal = AttaxGame.GetMoveTargets(state, x, y, false);
                    var legacyMovesNormal = AttaxRules.GetValidMovesFrom(state.Board, PlayerColor.Red, x, y, false);
                    ulong legacyMaskNormal = 0UL;
                    foreach (var m in legacyMovesNormal) {
                        legacyMaskNormal |= 1UL << BoardLookup.GetBitIndex(m.ToX, m.ToY);
                    }
                    Assert.Equal(legacyMaskNormal, moveTargetsNormal);

                    ulong moveTargetsSuper = AttaxGame.GetMoveTargets(state, x, y, true);
                    var legacyMovesSuper = AttaxRules.GetValidMovesFrom(state.Board, PlayerColor.Red, x, y, true);
                    ulong legacyMaskSuper = 0UL;
                    foreach (var m in legacyMovesSuper) {
                        legacyMaskSuper |= 1UL << BoardLookup.GetBitIndex(m.ToX, m.ToY);
                    }
                    Assert.Equal(legacyMaskSuper, moveTargetsSuper);
                }
            }

            // Target queries consistency with Validate
            var testAbilities = new[] { AbilityKind.Convert, AbilityKind.Block, AbilityKind.Wall, AbilityKind.Unblock, AbilityKind.Bomb, AbilityKind.Spawn, AbilityKind.SuperClone };
            foreach (var kind in testAbilities) {
                ulong targets = AttaxGame.GetAbilityTargets(state, kind);
                ActionKind aKind = (ActionKind)((int)kind - 1);
                for (int ty = 0; ty < 7; ty++) {
                    for (int tx = 0; tx < 7; tx++) {
                        ulong cellMask = 1UL << BoardLookup.GetBitIndex(tx, ty);
                        bool inMask = (targets & cellMask) != 0;
                        var valErr = AttaxGame.Validate(state, PlayerColor.Red, GameAction.Targeted(aKind, tx, ty));
                        if (inMask) {
                            Assert.Equal(ActionError.None, valErr);
                        } else {
                            Assert.NotEqual(ActionError.None, valErr);
                        }
                    }
                }
            }
        }

        [Fact]
        public void Restore_RoundTripsExplicitStates() {
            var rules = CreateTestRules(500);

            // 1. Mid-DoubleMove state
            var s1 = AttaxGame.NewGame(rules, new StartOptions(0, 0, PlayerColor.Red));
            AttaxGame.TryApply(s1, PlayerColor.Red, GameAction.DoubleMoveAction(), out _);
            AttaxGame.TryApply(s1, PlayerColor.Red, GameAction.MoveAction(new Move(0, 0, 1, 1)), out _);
            Assert.Equal(1, s1.MovesRemainingThisTurn);

            var r1 = GameState.Restore(
                s1.Board.RedPieces, s1.Board.BluePieces, s1.Board.BlockedSquares,
                s1.RedFootprints, s1.BlueFootprints, s1.SideToMove,
                s1.RedCredits, s1.BlueCredits, s1.TurnNumber, s1.ActionCount,
                s1.MovesRemainingThisTurn, s1.AbilityActionsUsedThisTurn, s1.ExportRules()
            );
            Assert.Equal(s1.ComputeFingerprint(), r1.ComputeFingerprint());

            // 2. State with footprints
            var s2 = AttaxGame.NewGame(rules, new StartOptions(0, 0, PlayerColor.Red));
            AttaxGame.TryApply(s2, PlayerColor.Red, GameAction.MoveAction(new Move(0, 0, 2, 2)), out _);
            Assert.NotEqual(0UL, s2.RedFootprints);

            var r2 = GameState.Restore(
                s2.Board.RedPieces, s2.Board.BluePieces, s2.Board.BlockedSquares,
                s2.RedFootprints, s2.BlueFootprints, s2.SideToMove,
                s2.RedCredits, s2.BlueCredits, s2.TurnNumber, s2.ActionCount,
                s2.MovesRemainingThisTurn, s2.AbilityActionsUsedThisTurn, s2.ExportRules()
            );
            Assert.Equal(s2.ComputeFingerprint(), r2.ComputeFingerprint());

            // 3. Terminal state
            s2.Result = TerminalResult.BlueWins;
            var r3 = GameState.Restore(
                s2.Board.RedPieces, s2.Board.BluePieces, s2.Board.BlockedSquares,
                s2.RedFootprints, s2.BlueFootprints, s2.SideToMove,
                s2.RedCredits, s2.BlueCredits, s2.TurnNumber, s2.ActionCount,
                s2.MovesRemainingThisTurn, s2.AbilityActionsUsedThisTurn, s2.ExportRules()
            );
            Assert.Equal(s2.ComputeFingerprint(), r3.ComputeFingerprint());
        }

        [Fact]
        public void ClassicParity_EconomyDisabled_PlaysIdenticalToAttaxRules() {
            var rules = CreateTestRules();
            rules.economy.enabled = false;

            var state = AttaxGame.NewGame(rules, new StartOptions(2, 77, PlayerColor.Red));
            var plainBoard = state.Board.Clone();
            var side = PlayerColor.Red;

            var referee = TestBoards.NewEngine();
            for (int ply = 0; ply < 30; ply++) {
                if (state.Result != TerminalResult.NotOver) break;
                var legal = referee.GetAllValidMoves(plainBoard, side);
                if (legal.Count == 0) break;
                var move = legal[0];

                // Play on plain board
                referee.MakeMove(plainBoard, move, side);

                // Play on GameState via AttaxGame
                var err = AttaxGame.TryApply(state, side, GameAction.MoveAction(move), out var outcome);
                Assert.Equal(ActionError.None, err);

                Assert.Equal(plainBoard.RedPieces, state.Board.RedPieces);
                Assert.Equal(plainBoard.BluePieces, state.Board.BluePieces);
                Assert.Equal(plainBoard.BlockedSquares, state.Board.BlockedSquares);
                Assert.Equal(plainBoard.ZobristHash, state.Board.ZobristHash);
                Assert.Equal(0, state.RedCredits);
                Assert.Equal(0, state.BlueCredits);
                Assert.Equal(0UL, state.RedFootprints);
                Assert.Equal(0UL, state.BlueFootprints);

                side = AttaxRules.SwitchPlayer(side);
            }
        }
    }
}
