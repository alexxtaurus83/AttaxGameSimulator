using System;
using Attax.Core.Utils;
using Xunit;

namespace Attax.Core.Tests {
    public class AttaxGameEconomyTests {
        private static GameRulesConfig CreateEconomyRules(int startingCredits = 0, int maxCredits = 3000) {
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
        public void CreditCap_ClampsRewardsToRemainingCapacity() {
            var rules = CreateEconomyRules(startingCredits: 2995, maxCredits: 3000);
            // Victim has 2995 credits. Flipping 1 chip pays 10, but capacity is 5 -> pays exactly 5!
            ulong red = 1UL << BoardLookup.GetBitIndex(0, 0);
            ulong blue = 1UL << BoardLookup.GetBitIndex(1, 0);
            var state = GameState.Restore(red, blue, 0UL, 0UL, 0UL, PlayerColor.Red, 0, 2995, 0, 0, 1, 0, rules);

            var action = GameAction.MoveAction(new Move(0, 0, 1, 1));
            var err = AttaxGame.TryApply(state, PlayerColor.Red, action, out var outcome);

            Assert.Equal(ActionError.None, err);
            Assert.Equal(5, outcome.VictimCreditsGained);
            Assert.Equal(3000, state.BlueCredits); // Clamped at 3000
        }

        [Fact]
        public void FootprintReward_ClampsToRemainingCapacity() {
            var rules = CreateEconomyRules(startingCredits: 2998, maxCredits: 3000);
            // Red has 2998 credits, cap is 3000. Footprint reward is 5.
            // Landing on enemy footprint should gain exactly 2 credits!
            ulong red = 1UL << BoardLookup.GetBitIndex(0, 0);
            ulong blue = 1UL << BoardLookup.GetBitIndex(6, 6);
            ulong blueFootprint = 1UL << BoardLookup.GetBitIndex(1, 1);
            var state = GameState.Restore(red, blue, 0UL, 0UL, blueFootprint, PlayerColor.Red, 2998, 0, 0, 0, 1, 0, rules);

            var action = GameAction.MoveAction(new Move(0, 0, 1, 1));
            var err = AttaxGame.TryApply(state, PlayerColor.Red, action, out var outcome);

            Assert.Equal(ActionError.None, err);
            Assert.Equal(2, outcome.ActorCreditsGained);
            Assert.Equal(3000, state.RedCredits);
        }

        [Fact]
        public void CreditCap_DiscardsRewardsAtMaxCapacity() {
            var rules = CreateEconomyRules(startingCredits: 3000, maxCredits: 3000);
            ulong red = 1UL << BoardLookup.GetBitIndex(0, 0);
            ulong blue = 1UL << BoardLookup.GetBitIndex(1, 0);
            var state = GameState.Restore(red, blue, 0UL, 0UL, 0UL, PlayerColor.Red, 0, 3000, 0, 0, 1, 0, rules);

            var action = GameAction.MoveAction(new Move(0, 0, 1, 1));
            var err = AttaxGame.TryApply(state, PlayerColor.Red, action, out var outcome);

            Assert.Equal(ActionError.None, err);
            Assert.Equal(0, outcome.VictimCreditsGained);
            Assert.Equal(3000, state.BlueCredits);
        }

        [Fact]
        public void SpendingCreatesCapacity_BeforeSubsequentReward() {
            var rules = CreateEconomyRules(startingCredits: 3000, maxCredits: 3000);
            // Actor is at 3000 cap. SuperClone costs 100.
            // There is also an enemy footprint at destination paying 5.
            // Spend happens first (3000 - 100 = 2900).
            // Then footprint reward pays 5 into the newly opened capacity (2900 + 5 = 2905).
            ulong red = 1UL << BoardLookup.GetBitIndex(0, 0);
            ulong blue = 1UL << BoardLookup.GetBitIndex(6, 6);
            ulong blueFootprint = 1UL << BoardLookup.GetBitIndex(3, 3);
            var state = GameState.Restore(red, blue, 0UL, 0UL, blueFootprint, PlayerColor.Red, 3000, 0, 0, 0, 1, 0, rules);

            var action = GameAction.Targeted(ActionKind.SuperClone, 3, 3);
            var err = AttaxGame.TryApply(state, PlayerColor.Red, action, out var outcome);

            Assert.Equal(ActionError.None, err);
            Assert.Equal(100, outcome.CreditsSpent);
            Assert.Equal(5, outcome.ActorCreditsGained);
            Assert.Equal(2905, state.RedCredits);
        }

        [Fact]
        public void HugeRewards_HandleLongIntermediatesWithoutOverflow() {
            var rules = CreateEconomyRules(startingCredits: 0, maxCredits: 3000);
            rules.economy.captureReward = 1_000_000; // very large reward
            ulong red = 1UL << BoardLookup.GetBitIndex(0, 0);
            ulong blue = 1UL << BoardLookup.GetBitIndex(1, 0);
            var state = GameState.Restore(red, blue, 0UL, 0UL, 0UL, PlayerColor.Red, 0, 0, 0, 0, 1, 0, rules);

            var action = GameAction.MoveAction(new Move(0, 0, 1, 1));
            var err = AttaxGame.TryApply(state, PlayerColor.Red, action, out var outcome);

            Assert.Equal(ActionError.None, err);
            Assert.Equal(3000, outcome.VictimCreditsGained); // clamped to maxCredits
            Assert.Equal(3000, state.BlueCredits);
        }

        [Fact]
        public void OwnFootprintLanding_ViaSpawnAndSuperClone_ClearsFootprintWithoutReward() {
            var rules = CreateEconomyRules(startingCredits: 1000, maxCredits: 3000);
            ulong red = 1UL << BoardLookup.GetBitIndex(0, 0);
            ulong blue = 1UL << BoardLookup.GetBitIndex(6, 6);
            ulong redFootprint = 1UL << BoardLookup.GetBitIndex(3, 3);
            var state = GameState.Restore(red, blue, 0UL, redFootprint, 0UL, PlayerColor.Red, 1000, 0, 0, 0, 1, 0, rules);

            // Red Spawn at (3,3) where Red already has a footprint
            var action = GameAction.Targeted(ActionKind.Spawn, 3, 3);
            var err = AttaxGame.TryApply(state, PlayerColor.Red, action, out var outcome);

            Assert.Equal(ActionError.None, err);
            Assert.Equal(0, outcome.ActorCreditsGained); // no reward for own footprint
            Assert.Equal(900, state.RedCredits); // 1000 - 100 cost
            Assert.Equal(0UL, state.RedFootprints & redFootprint); // cleared!
            Assert.Equal(redFootprint, outcome.FootprintClearedMask);
        }

        #region Unity V1 to V8 Parity Vectors

        private static GameRulesConfig UnityVectorRules() {
            return new GameRulesConfig {
                schemaVersion = 1,
                economy = new EconomyRules {
                    enabled = true,
                    startingCredits = 0,
                    maxCredits = 3000,
                    captureReward = 10,
                    footprintReward = 5,
                    abilityCosts = new AbilityCosts {
                        superJump = 0, convert = 0, block = 0, wall = 0, doubleMove = 0,
                        spawn = 0, superClone = 0, unblock = 0, bomb = 0
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
        public void Vector_V1_JumpCaptures3_VictimEarns30_FootprintOnSource() {
            var rules = UnityVectorRules();
            ulong blue = 1UL << BoardLookup.GetBitIndex(0, 0);
            ulong red = (1UL << BoardLookup.GetBitIndex(3, 3)) |
                        (1UL << BoardLookup.GetBitIndex(3, 2)) |
                        (1UL << BoardLookup.GetBitIndex(1, 3)) |
                        (1UL << BoardLookup.GetBitIndex(6, 6));

            var state = GameState.Restore(red, blue, 0UL, 0UL, 0UL, PlayerColor.Blue, 0, 0, 0, 0, 1, 0, rules);
            var err = AttaxGame.TryApply(state, PlayerColor.Blue, GameAction.MoveAction(new Move(0, 0, 2, 2)), out var outcome);

            Assert.Equal(ActionError.None, err);
            Assert.Equal(0, outcome.ActorCreditsGained);
            Assert.Equal(30, outcome.VictimCreditsGained);

            ulong expectedFlipped = (1UL << BoardLookup.GetBitIndex(3, 3)) |
                                   (1UL << BoardLookup.GetBitIndex(3, 2)) |
                                   (1UL << BoardLookup.GetBitIndex(1, 3));
            Assert.Equal(expectedFlipped, outcome.FlippedMask);
            Assert.Equal(1UL << BoardLookup.GetBitIndex(0, 0), state.BlueFootprints);
            Assert.Equal(0UL, state.Board.BluePieces & (1UL << BoardLookup.GetBitIndex(0, 0)));
        }

        [Fact]
        public void Vector_V2_CloneOnEnemyFootprint_ActorEarns5_FootprintCleared() {
            var rules = UnityVectorRules();
            ulong red = (1UL << BoardLookup.GetBitIndex(1, 1)) | (1UL << BoardLookup.GetBitIndex(6, 6));
            ulong blue = 1UL << BoardLookup.GetBitIndex(4, 4);
            ulong blueFootprint = 1UL << BoardLookup.GetBitIndex(0, 0);

            var state = GameState.Restore(red, blue, 0UL, 0UL, blueFootprint, PlayerColor.Red, 0, 0, 0, 0, 1, 0, rules);
            var err = AttaxGame.TryApply(state, PlayerColor.Red, GameAction.MoveAction(new Move(1, 1, 0, 0)), out var outcome);

            Assert.Equal(ActionError.None, err);
            Assert.Equal(5, outcome.ActorCreditsGained);
            Assert.Equal(0, outcome.VictimCreditsGained);
            Assert.Equal(0UL, state.BlueFootprints & (1UL << BoardLookup.GetBitIndex(0, 0)));
            Assert.Equal(1UL << BoardLookup.GetBitIndex(0, 0), outcome.FootprintClearedMask);
        }

        [Fact]
        public void Vector_V3_Stacking_FootprintAndCaptures_BothPaid() {
            var rules = UnityVectorRules();
            ulong red = (1UL << BoardLookup.GetBitIndex(1, 1)) | (1UL << BoardLookup.GetBitIndex(6, 6));
            ulong blue = (1UL << BoardLookup.GetBitIndex(1, 0)) |
                         (1UL << BoardLookup.GetBitIndex(0, 1)) |
                         (1UL << BoardLookup.GetBitIndex(4, 4));
            ulong blueFootprint = 1UL << BoardLookup.GetBitIndex(0, 0);

            var state = GameState.Restore(red, blue, 0UL, 0UL, blueFootprint, PlayerColor.Red, 0, 0, 0, 0, 1, 0, rules);
            var err = AttaxGame.TryApply(state, PlayerColor.Red, GameAction.MoveAction(new Move(1, 1, 0, 0)), out var outcome);

            Assert.Equal(ActionError.None, err);
            Assert.Equal(5, outcome.ActorCreditsGained);
            Assert.Equal(20, outcome.VictimCreditsGained);
            Assert.Equal(0UL, state.BlueFootprints & (1UL << BoardLookup.GetBitIndex(0, 0)));
        }

        [Fact]
        public void Vector_V4_CloneOnOwnFootprint_NoReward_FootprintCleared() {
            var rules = UnityVectorRules();
            ulong blue = (1UL << BoardLookup.GetBitIndex(1, 1)) | (1UL << BoardLookup.GetBitIndex(6, 6));
            ulong red = 1UL << BoardLookup.GetBitIndex(4, 4);
            ulong blueFootprint = 1UL << BoardLookup.GetBitIndex(0, 0);

            var state = GameState.Restore(red, blue, 0UL, 0UL, blueFootprint, PlayerColor.Blue, 0, 0, 0, 0, 1, 0, rules);
            var err = AttaxGame.TryApply(state, PlayerColor.Blue, GameAction.MoveAction(new Move(1, 1, 0, 0)), out var outcome);

            Assert.Equal(ActionError.None, err);
            Assert.Equal(0, outcome.ActorCreditsGained);
            Assert.Equal(0, outcome.VictimCreditsGained);
            Assert.Equal(0UL, state.BlueFootprints & (1UL << BoardLookup.GetBitIndex(0, 0)));
            Assert.Equal(1UL << BoardLookup.GetBitIndex(0, 0), outcome.FootprintClearedMask);
        }

        [Fact]
        public void Vector_V5_TwoMoveSequence_JumpLeavesFootprint_EnemyCloneLandsOnIt() {
            var rules = UnityVectorRules();
            ulong red = (1UL << BoardLookup.GetBitIndex(3, 3)) | (1UL << BoardLookup.GetBitIndex(6, 6));
            ulong blue = (1UL << BoardLookup.GetBitIndex(2, 2)) | (1UL << BoardLookup.GetBitIndex(0, 6));

            var state = GameState.Restore(red, blue, 0UL, 0UL, 0UL, PlayerColor.Red, 0, 0, 0, 0, 1, 0, rules);

            // Move 1: Red jump (3,3) -> (5,5)
            var err1 = AttaxGame.TryApply(state, PlayerColor.Red, GameAction.MoveAction(new Move(3, 3, 5, 5)), out var outcome1);
            Assert.Equal(ActionError.None, err1);
            Assert.Equal(0, outcome1.ActorCreditsGained);
            Assert.Equal(0, outcome1.VictimCreditsGained);
            Assert.Equal(1UL << BoardLookup.GetBitIndex(3, 3), state.RedFootprints);

            // Move 2: Blue clone (2,2) -> (3,3)
            var err2 = AttaxGame.TryApply(state, PlayerColor.Blue, GameAction.MoveAction(new Move(2, 2, 3, 3)), out var outcome2);
            Assert.Equal(ActionError.None, err2);
            Assert.Equal(5, outcome2.ActorCreditsGained);
            Assert.Equal(0, outcome2.VictimCreditsGained);
            Assert.Equal(0UL, state.RedFootprints & (1UL << BoardLookup.GetBitIndex(3, 3)));
        }

        [Fact]
        public void Vector_V6_CloneLeavesNoFootprint() {
            var rules = UnityVectorRules();
            ulong blue = (1UL << BoardLookup.GetBitIndex(1, 1)) | (1UL << BoardLookup.GetBitIndex(6, 6));
            ulong red = 1UL << BoardLookup.GetBitIndex(4, 4);

            var state = GameState.Restore(red, blue, 0UL, 0UL, 0UL, PlayerColor.Blue, 0, 0, 0, 0, 1, 0, rules);
            var err = AttaxGame.TryApply(state, PlayerColor.Blue, GameAction.MoveAction(new Move(1, 1, 0, 0)), out var outcome);

            Assert.Equal(ActionError.None, err);
            Assert.Equal(0, outcome.ActorCreditsGained);
            Assert.Equal(0, outcome.VictimCreditsGained);
            Assert.Equal(0UL, outcome.FootprintPlacedMask);
            Assert.Equal(0UL, state.BlueFootprints);
        }

        [Fact]
        public void Vector_V7_JumpWithoutCaptures_LeavesFootprint_PaysNothing() {
            var rules = UnityVectorRules();
            ulong blue = 1UL << BoardLookup.GetBitIndex(0, 0);
            ulong red = 1UL << BoardLookup.GetBitIndex(3, 3);

            var state = GameState.Restore(red, blue, 0UL, 0UL, 0UL, PlayerColor.Blue, 0, 0, 0, 0, 1, 0, rules);
            var err = AttaxGame.TryApply(state, PlayerColor.Blue, GameAction.MoveAction(new Move(0, 0, 2, 0)), out var outcome);

            Assert.Equal(ActionError.None, err);
            Assert.Equal(0, outcome.ActorCreditsGained);
            Assert.Equal(0, outcome.VictimCreditsGained);
            Assert.Equal(1UL << BoardLookup.GetBitIndex(0, 0), state.BlueFootprints);
        }

        [Theory]
        [InlineData(1, 10)]
        [InlineData(2, 20)]
        [InlineData(4, 40)]
        [InlineData(8, 80)]
        public void Vector_V8_MoveCapturesN_VictimEarns10xN(int chipCount, int expectedReward) {
            var rules = UnityVectorRules();
            ulong red = 1UL << BoardLookup.GetBitIndex(0, 0);
            ulong blue = 1UL << BoardLookup.GetBitIndex(6, 6);

            (int x, int y)[] adj = new[] {
                (1, 1), (2, 1), (3, 1),
                (1, 2),         (3, 2),
                (1, 3), (2, 3), (3, 3)
            };
            for (int i = 0; i < chipCount; i++) {
                var (ax, ay) = adj[i];
                blue |= 1UL << BoardLookup.GetBitIndex(ax, ay);
            }

            var state = GameState.Restore(red, blue, 0UL, 0UL, 0UL, PlayerColor.Red, 0, 0, 0, 0, 1, 0, rules);
            var err = AttaxGame.TryApply(state, PlayerColor.Red, GameAction.MoveAction(new Move(0, 0, 2, 2)), out var outcome);

            Assert.Equal(ActionError.None, err);
            int actualFlipped = BitboardOps.PopCount(outcome.FlippedMask);
            Assert.Equal(expectedReward, outcome.VictimCreditsGained);
            Assert.Equal(actualFlipped * 10, outcome.VictimCreditsGained);
        }

        #endregion
    }
}
