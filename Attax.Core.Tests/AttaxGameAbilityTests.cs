using System;
using Attax.Core.Utils;
using Xunit;

namespace Attax.Core.Tests {
    public class AttaxGameAbilityTests {
        private static GameRulesConfig CreateTestRules(int startingCredits = 1000) {
            return new GameRulesConfig {
                schemaVersion = 1,
                economy = new EconomyRules {
                    enabled = true,
                    startingCredits = startingCredits,
                    maxCredits = 3000,
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
        public void SuperJump_ConsumesMoveAndAbility_LeavesSourceFootprint_CapturesEnemies() {
            var rules = CreateTestRules();
            // Red at (0,0), Blue at (5,5) and adjacent enemy at (3,4)
            ulong red = 1UL << BoardLookup.GetBitIndex(0, 0);
            ulong blue = (1UL << BoardLookup.GetBitIndex(5, 5)) | (1UL << BoardLookup.GetBitIndex(3, 4));
            var state = GameState.Restore(red, blue, 0UL, 0UL, 0UL, PlayerColor.Red, 1000, 1000, 0, 0, 1, 0, rules);

            // SuperJump from (0,0) to (3,3) (Chebyshev dist = 3 >= 3)
            // (3,4) is adjacent to (3,3) so it should be captured!
            var action = GameAction.MoveAction(new Move(0, 0, 3, 3));
            var err = AttaxGame.TryApply(state, PlayerColor.Red, action, out var outcome);

            Assert.Equal(ActionError.None, err);
            Assert.Equal(ActionKind.Move, outcome.Kind);
            Assert.Equal(AbilityKind.SuperJump, outcome.Ability);
            Assert.Equal(100, outcome.CreditsSpent);
            Assert.Equal(900, state.RedCredits);

            // Source (0,0) vacated and has footprint
            int fromIdx = BoardLookup.GetBitIndex(0, 0);
            Assert.Equal(0UL, state.Board.RedPieces & (1UL << fromIdx));
            Assert.Equal(1UL << fromIdx, state.RedFootprints);
            Assert.Equal(1UL << fromIdx, outcome.FootprintPlacedMask);

            // Destination (3,3) filled
            int toIdx = BoardLookup.GetBitIndex(3, 3);
            Assert.NotEqual(0UL, state.Board.RedPieces & (1UL << toIdx));

            // Enemy at (3,4) flipped
            int flipIdx = BoardLookup.GetBitIndex(3, 4);
            Assert.NotEqual(0UL, state.Board.RedPieces & (1UL << flipIdx));
            Assert.Equal(0UL, state.Board.BluePieces & (1UL << flipIdx));
            Assert.Equal(1UL << flipIdx, outcome.FlippedMask);

            // Victim reward paid: 1 chip flipped -> 10 credits to Blue
            Assert.Equal(10, outcome.VictimCreditsGained);
            Assert.Equal(1010, state.BlueCredits);

            // Turn advanced
            Assert.True(outcome.TurnAdvanced);
            Assert.Equal(PlayerColor.Blue, state.SideToMove);
            Assert.Equal(1, state.TurnNumber);
            Assert.Equal(1, state.ActionCount);
            Assert.True(state.IsHashConsistent());
        }

        [Fact]
        public void Convert_TurnsEnemyChipIntoOwn_NoAdjacentCaptures_DoesNotAdvanceTurn() {
            var rules = CreateTestRules();
            // Red at (0,0), Blue at (1,1) and (1,2)
            ulong red = 1UL << BoardLookup.GetBitIndex(0, 0);
            ulong blue = (1UL << BoardLookup.GetBitIndex(1, 1)) | (1UL << BoardLookup.GetBitIndex(1, 2));
            var state = GameState.Restore(red, blue, 0UL, 0UL, 0UL, PlayerColor.Red, 1000, 1000, 0, 0, 1, 0, rules);

            var action = GameAction.Targeted(ActionKind.Convert, 1, 1);
            var err = AttaxGame.TryApply(state, PlayerColor.Red, action, out var outcome);

            Assert.Equal(ActionError.None, err);
            Assert.Equal(AbilityKind.Convert, outcome.Ability);
            Assert.Equal(100, outcome.CreditsSpent);
            Assert.Equal(900, state.RedCredits);

            // (1,1) converted to Red
            int idx11 = BoardLookup.GetBitIndex(1, 1);
            Assert.NotEqual(0UL, state.Board.RedPieces & (1UL << idx11));
            Assert.Equal(0UL, state.Board.BluePieces & (1UL << idx11));

            // (1,2) NOT captured
            int idx12 = BoardLookup.GetBitIndex(1, 2);
            Assert.NotEqual(0UL, state.Board.BluePieces & (1UL << idx12));
            Assert.Equal(0UL, outcome.FlippedMask);

            // Does not advance turn
            Assert.False(outcome.TurnAdvanced);
            Assert.Equal(PlayerColor.Red, state.SideToMove);
            Assert.Equal(0, state.TurnNumber);
            Assert.Equal(1, state.ActionCount);
            Assert.Equal(1, state.AbilityActionsUsedThisTurn);
            Assert.True(state.IsHashConsistent());
        }

        [Fact]
        public void Block_RemovesEnemyChip_PlacesBlockedCell_NoCompensation() {
            var rules = CreateTestRules();
            ulong red = 1UL << BoardLookup.GetBitIndex(0, 0);
            ulong blue = 1UL << BoardLookup.GetBitIndex(2, 2);
            var state = GameState.Restore(red, blue, 0UL, 0UL, 0UL, PlayerColor.Red, 1000, 1000, 0, 0, 1, 0, rules);

            var action = GameAction.Targeted(ActionKind.Block, 2, 2);
            var err = AttaxGame.TryApply(state, PlayerColor.Red, action, out var outcome);

            Assert.Equal(ActionError.None, err);
            Assert.Equal(AbilityKind.Block, outcome.Ability);
            Assert.Equal(100, outcome.CreditsSpent);
            int idx = BoardLookup.GetBitIndex(2, 2);
            Assert.Equal(0UL, state.Board.BluePieces & (1UL << idx));
            Assert.NotEqual(0UL, state.Board.BlockedSquares & (1UL << idx));
            Assert.Equal(1UL << idx, outcome.RemovedMask);
            Assert.Equal(1UL << idx, outcome.BlockedAddedMask);
            Assert.False(outcome.TurnAdvanced);
            Assert.True(state.IsHashConsistent());
        }

        [Fact]
        public void Wall_PlacesBlockedOnEmpty_ClearsFootprint() {
            var rules = CreateTestRules();
            ulong red = 1UL << BoardLookup.GetBitIndex(0, 0);
            ulong blue = 1UL << BoardLookup.GetBitIndex(6, 6);
            ulong footprint = 1UL << BoardLookup.GetBitIndex(3, 3);
            var state = GameState.Restore(red, blue, 0UL, footprint, 0UL, PlayerColor.Red, 1000, 1000, 0, 0, 1, 0, rules);

            var action = GameAction.Targeted(ActionKind.Wall, 3, 3);
            var err = AttaxGame.TryApply(state, PlayerColor.Red, action, out var outcome);

            Assert.Equal(ActionError.None, err);
            Assert.Equal(AbilityKind.Wall, outcome.Ability);
            Assert.NotEqual(0UL, state.Board.BlockedSquares & footprint);
            Assert.Equal(0UL, state.RedFootprints & footprint);
            Assert.Equal(footprint, outcome.FootprintClearedMask);
            Assert.False(outcome.TurnAdvanced);
            Assert.True(state.IsHashConsistent());
        }

        [Fact]
        public void Unblock_ClearsBlockedSquare() {
            var rules = CreateTestRules();
            ulong red = 1UL << BoardLookup.GetBitIndex(0, 0);
            ulong blue = 1UL << BoardLookup.GetBitIndex(6, 6);
            ulong blocked = 1UL << BoardLookup.GetBitIndex(3, 3);
            var state = GameState.Restore(red, blue, blocked, 0UL, 0UL, PlayerColor.Red, 1000, 1000, 0, 0, 1, 0, rules);

            var action = GameAction.Targeted(ActionKind.Unblock, 3, 3);
            var err = AttaxGame.TryApply(state, PlayerColor.Red, action, out var outcome);

            Assert.Equal(ActionError.None, err);
            Assert.Equal(AbilityKind.Unblock, outcome.Ability);
            Assert.Equal(0UL, state.Board.BlockedSquares & blocked);
            Assert.Equal(blocked, outcome.BlockedClearedMask);
            Assert.False(outcome.TurnAdvanced);
            Assert.True(state.IsHashConsistent());
        }

        [Fact]
        public void Bomb_RemovesEnemyChip_LeavesCellEmpty() {
            var rules = CreateTestRules();
            ulong red = 1UL << BoardLookup.GetBitIndex(0, 0);
            ulong blue = 1UL << BoardLookup.GetBitIndex(2, 2);
            var state = GameState.Restore(red, blue, 0UL, 0UL, 0UL, PlayerColor.Red, 1000, 1000, 0, 0, 1, 0, rules);

            var action = GameAction.Targeted(ActionKind.Bomb, 2, 2);
            var err = AttaxGame.TryApply(state, PlayerColor.Red, action, out var outcome);

            Assert.Equal(ActionError.None, err);
            Assert.Equal(AbilityKind.Bomb, outcome.Ability);
            int idx = BoardLookup.GetBitIndex(2, 2);
            Assert.Equal(0UL, state.Board.BluePieces & (1UL << idx));
            Assert.Equal(0UL, state.Board.BlockedSquares & (1UL << idx));
            Assert.Equal(1UL << idx, outcome.RemovedMask);
            Assert.False(outcome.TurnAdvanced);
            Assert.True(state.IsHashConsistent());
        }

        [Fact]
        public void Spawn_PaidNonMoveConsuming_LeavesRemainingMovesAndTurnUnchanged_EvaluatesTerminal() {
            var rules = CreateTestRules();
            ulong red = 1UL << BoardLookup.GetBitIndex(0, 0);
            ulong blue = 1UL << BoardLookup.GetBitIndex(6, 6);
            ulong enemyFootprint = 1UL << BoardLookup.GetBitIndex(3, 3);
            var state = GameState.Restore(red, blue, 0UL, 0UL, enemyFootprint, PlayerColor.Red, 1000, 1000, 0, 0, 1, 0, rules);

            // Spawn at (3,3) on enemy footprint
            var action = GameAction.Targeted(ActionKind.Spawn, 3, 3);
            var err = AttaxGame.TryApply(state, PlayerColor.Red, action, out var outcome);

            Assert.Equal(ActionError.None, err);
            Assert.Equal(AbilityKind.Spawn, outcome.Ability);
            Assert.Equal(100, outcome.CreditsSpent);
            Assert.Equal(5, outcome.ActorCreditsGained); // Landed on Blue footprint -> +5
            Assert.Equal(905, state.RedCredits); // 1000 - 100 + 5
            Assert.Equal(0UL, outcome.FlippedMask); // Spawn never captures

            // State checks
            Assert.False(outcome.TurnAdvanced);
            Assert.Equal(PlayerColor.Red, state.SideToMove);
            Assert.Equal(0, state.TurnNumber);
            Assert.Equal(1, state.ActionCount);
            Assert.Equal(1, state.MovesRemainingThisTurn); // Move NOT consumed!
            Assert.Equal(1, state.AbilityActionsUsedThisTurn); // Ability consumed!
            Assert.True(state.IsHashConsistent());

            // Destination footprint cleared
            Assert.Equal(0UL, state.BlueFootprints & enemyFootprint);
            Assert.Equal(enemyFootprint, outcome.FootprintClearedMask);

            // Red can follow up with a normal move in the same turn!
            var followUpMove = GameAction.MoveAction(new Move(0, 0, 1, 1));
            var followUpErr = AttaxGame.TryApply(state, PlayerColor.Red, followUpMove, out var followUpOutcome);
            Assert.Equal(ActionError.None, followUpErr);
            Assert.True(followUpOutcome.TurnAdvanced);
            Assert.Equal(PlayerColor.Blue, state.SideToMove);
            Assert.Equal(1, state.TurnNumber);
            Assert.Equal(2, state.ActionCount);
            Assert.True(state.IsHashConsistent());
        }

        [Fact]
        public void Spawn_CannotCombineWithSecondAbilityInSameTurn() {
            var rules = CreateTestRules();
            var state = AttaxGame.NewGame(rules, new StartOptions(0, 0, PlayerColor.Red));

            // First ability: Spawn
            var err1 = AttaxGame.TryApply(state, PlayerColor.Red, GameAction.Targeted(ActionKind.Spawn, 3, 3), out _);
            Assert.Equal(ActionError.None, err1);

            // Second ability in same turn (e.g. Wall) should be rejected by AbilityLimitReachedThisTurn
            var err2 = AttaxGame.TryApply(state, PlayerColor.Red, GameAction.Targeted(ActionKind.Wall, 4, 4), out _);
            Assert.Equal(ActionError.AbilityLimitReachedThisTurn, err2);
        }

        [Fact]
        public void SuperClone_PlacesChipAndCaptures_ConsumesMoveAndAbility() {
            var rules = CreateTestRules();
            // Red at (0,0), Blue at (3,2) and (3,4)
            ulong red = 1UL << BoardLookup.GetBitIndex(0, 0);
            ulong blue = (1UL << BoardLookup.GetBitIndex(3, 2)) | (1UL << BoardLookup.GetBitIndex(3, 4));
            var state = GameState.Restore(red, blue, 0UL, 0UL, 0UL, PlayerColor.Red, 1000, 1000, 0, 0, 1, 0, rules);

            // SuperClone at (3,3) adjacent to both Blue chips
            var action = GameAction.Targeted(ActionKind.SuperClone, 3, 3);
            var err = AttaxGame.TryApply(state, PlayerColor.Red, action, out var outcome);

            Assert.Equal(ActionError.None, err);
            Assert.Equal(AbilityKind.SuperClone, outcome.Ability);
            Assert.Equal(100, outcome.CreditsSpent);
            Assert.Equal(900, state.RedCredits);

            // Both Blue chips captured
            ulong expectedFlipped = (1UL << BoardLookup.GetBitIndex(3, 2)) | (1UL << BoardLookup.GetBitIndex(3, 4));
            Assert.Equal(expectedFlipped, outcome.FlippedMask);
            Assert.Equal(20, outcome.VictimCreditsGained);
            Assert.Equal(1020, state.BlueCredits);

            // Turn advanced
            Assert.True(outcome.TurnAdvanced);
            Assert.Equal(PlayerColor.Blue, state.SideToMove);
            Assert.Equal(1, state.TurnNumber);
            Assert.Equal(1, state.ActionCount);
            Assert.True(state.IsHashConsistent());
        }

        [Fact]
        public void DoubleMove_ArmsTwoStandardMoves_CorrectHashHandoffOnBothBoundaries() {
            var rules = CreateTestRules();
            var state = AttaxGame.NewGame(rules, new StartOptions(0, 0, PlayerColor.Red));

            // Arm DoubleMove
            var armAction = GameAction.DoubleMoveAction();
            var armErr = AttaxGame.TryApply(state, PlayerColor.Red, armAction, out var armOutcome);

            Assert.Equal(ActionError.None, armErr);
            Assert.Equal(AbilityKind.DoubleMove, armOutcome.Ability);
            Assert.Equal(100, armOutcome.CreditsSpent);
            Assert.Equal(2, state.MovesRemainingThisTurn);
            Assert.False(armOutcome.TurnAdvanced);
            Assert.Equal(PlayerColor.Red, state.SideToMove);
            Assert.Equal(0, state.TurnNumber);
            Assert.True(state.IsHashConsistent());

            // First move: Red clone (0,0) -> (1,1)
            var move1 = GameAction.MoveAction(new Move(0, 0, 1, 1));
            var move1Err = AttaxGame.TryApply(state, PlayerColor.Red, move1, out var move1Outcome);

            Assert.Equal(ActionError.None, move1Err);
            Assert.False(move1Outcome.TurnAdvanced);
            Assert.Equal(PlayerColor.Red, state.SideToMove); // Still Red!
            Assert.Equal(1, state.MovesRemainingThisTurn);
            Assert.Equal(0, state.TurnNumber);
            Assert.True(state.IsHashConsistent()); // Side key correctly un-toggled!

            // Second move: Red jump (1,1) -> (3,3)
            var move2 = GameAction.MoveAction(new Move(1, 1, 3, 3));
            var move2Err = AttaxGame.TryApply(state, PlayerColor.Red, move2, out var move2Outcome);

            Assert.Equal(ActionError.None, move2Err);
            Assert.True(move2Outcome.TurnAdvanced);
            Assert.Equal(PlayerColor.Blue, state.SideToMove); // Now Blue!
            Assert.Equal(1, state.MovesRemainingThisTurn);
            Assert.Equal(0, state.AbilityActionsUsedThisTurn);
            Assert.Equal(1, state.TurnNumber);
            Assert.True(state.IsHashConsistent());
        }

        [Fact]
        public void TerminalFreeAction_BombLastEnemy_EndsGameImmediately() {
            var rules = CreateTestRules();
            // Red at (0,0), Blue at only (2,2)
            ulong red = 1UL << BoardLookup.GetBitIndex(0, 0);
            ulong blue = 1UL << BoardLookup.GetBitIndex(2, 2);
            var state = GameState.Restore(red, blue, 0UL, 0UL, 0UL, PlayerColor.Red, 1000, 1000, 0, 0, 1, 0, rules);

            var err = AttaxGame.TryApply(state, PlayerColor.Red, GameAction.Targeted(ActionKind.Bomb, 2, 2), out var outcome);

            Assert.Equal(ActionError.None, err);
            Assert.Equal(TerminalResult.RedWins, state.Result);
            Assert.Equal(TerminalResult.RedWins, outcome.Result);
        }
    }
}
