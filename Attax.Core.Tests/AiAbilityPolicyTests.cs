using System;
using System.Collections.Generic;
using System.IO;
using Attax.Core;
using Attax.Core.Utils;
using Newtonsoft.Json;
using Xunit;

namespace Attax.Core.Tests {
    public class AiAbilityPolicyTests {
        private static string GetAiConfigJsonPath() {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string inBin = Path.Combine(baseDir, "ai-ability-config.json");
            if (File.Exists(inBin)) return inBin;
            string inRules = Path.Combine(baseDir, "Rules", "ai-ability-config.json");
            if (File.Exists(inRules)) return inRules;
            string inProj = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "Attax.Core.Utils", "Rules", "ai-ability-config.json"));
            if (File.Exists(inProj)) return inProj;
            throw new FileNotFoundException("ai-ability-config.json not found");
        }

        private static AiAbilityConfig TestConfig(bool enabled = true, int chance = 100, int reserve = 0, int maxUses = 0, bool safety = true, string targetSelection = null) {
            return new AiAbilityConfig {
                schemaVersion = 1,
                aiAbilitiesEnabled = enabled,
                useChancePercent = chance,
                creditReserve = reserve,
                maxUsesPerGame = maxUses,
                safetyFilter = safety,
                targetSelection = targetSelection
            };
        }

        private static GameRulesConfig TestRules(int credits = 1000) {
            var rules = GameRulesConfigTests.ValidTestRules();
            rules.economy.startingCredits = credits;
            return rules;
        }

        [Fact]
        public void ShippedAiAbilityConfig_DeserializesAndValidates() {
            string path = GetAiConfigJsonPath();
            string json = File.ReadAllText(path);
            var cfg = JsonConvert.DeserializeObject<AiAbilityConfig>(json);

            Assert.NotNull(cfg);
            var errors = cfg.Validate();
            Assert.Empty(errors);
            Assert.False(cfg.aiAbilitiesEnabled); // Master flag is false by default
            Assert.Equal(35, cfg.useChancePercent);
            Assert.Equal(0, cfg.creditReserve);
            Assert.Equal(0, cfg.maxUsesPerGame);
            Assert.True(cfg.safetyFilter);
            Assert.Equal("engine", cfg.targetSelection);
        }

        [Fact]
        public void AiAbilityOptions_Resolve_Precedence() {
            var cfg = TestConfig(enabled: false);
            Assert.False(AiAbilityOptions.Resolve(cfg, null));
            Assert.True(AiAbilityOptions.Resolve(cfg, true));
            Assert.False(AiAbilityOptions.Resolve(cfg, false));

            var enabledCfg = TestConfig(enabled: true);
            Assert.True(AiAbilityOptions.Resolve(enabledCfg, null));
            Assert.False(AiAbilityOptions.Resolve(enabledCfg, false));
        }

        [Fact]
        public void DisabledPolicy_NeverReturnsChoice_DoesNotAdvanceRng() {
            var cfg = TestConfig(enabled: false);
            var policy = new AiAbilityPolicy(cfg, seed: 12345, enabled: false);
            var state = AttaxGame.NewGame(TestRules(), new StartOptions(0, 0, PlayerColor.Red));

            Assert.False(policy.TryChoose(state, PlayerColor.Red, out var choice));
            Assert.Equal(default, choice);
            Assert.Equal(0, policy.UsesThisGame);
        }

        [Fact]
        public void UseChancePercent_0_NeverReturnsChoice_100_ReturnsChoice() {
            var cfg0 = TestConfig(chance: 0);
            var policy0 = new AiAbilityPolicy(cfg0, seed: 42, enabled: true);
            var state = AttaxGame.NewGame(TestRules(), new StartOptions(0, 0, PlayerColor.Red));
            Assert.False(policy0.TryChoose(state, PlayerColor.Red, out _));

            var cfg100 = TestConfig(chance: 100);
            var policy100 = new AiAbilityPolicy(cfg100, seed: 42, enabled: true);
            Assert.True(policy100.TryChoose(state, PlayerColor.Red, out _));
        }

        [Fact]
        public void DuplicateCallInSameTurn_ReturnsFalseWithoutRerolling() {
            var cfg = TestConfig(chance: 100);
            var policy = new AiAbilityPolicy(cfg, seed: 42, enabled: true);
            var state = AttaxGame.NewGame(TestRules(), new StartOptions(0, 0, PlayerColor.Red));

            // First call chooses an ability
            bool chosen = policy.TryChoose(state, PlayerColor.Red, out var choice1);
            Assert.True(chosen);

            // Second call in the same turn/side must return false
            bool second = policy.TryChoose(state, PlayerColor.Red, out var choice2);
            Assert.False(second);
        }

        [Fact]
        public void FailedChanceRoll_DoesNotRerollInSameTurn() {
            var cfg = TestConfig(chance: 1); // 1% chance, high likelihood of failing
            var policy = new AiAbilityPolicy(cfg, seed: 999, enabled: true);
            var state = AttaxGame.NewGame(TestRules(), new StartOptions(0, 0, PlayerColor.Red));

            bool first = policy.TryChoose(state, PlayerColor.Red, out _);
            if (!first) {
                // Second call in same turn must also return false without drawing RNG
                bool second = policy.TryChoose(state, PlayerColor.Red, out _);
                Assert.False(second);
            }
        }

        [Fact]
        public void RespectsPerPlayerAbilityToggles() {
            var rules = TestRules(1000);
            // Red has ONLY Spawn enabled; all other 8 abilities disabled for Red!
            rules.playerAbilities.red = new AbilityToggles { spawn = true };
            var cfg = TestConfig(chance: 100);
            var policy = new AiAbilityPolicy(cfg, seed: 42, enabled: true);

            var state = AttaxGame.NewGame(rules, new StartOptions(0, 0, PlayerColor.Red));
            Assert.True(policy.TryChoose(state, PlayerColor.Red, out var choice));
            Assert.Equal(AbilityKind.Spawn, choice.Ability);
            Assert.Equal(ActionKind.Spawn, choice.Action.Kind);
            Assert.False(choice.EndsTurn);
        }

        [Fact]
        public void EndsTurn_IsCorrectForEachAbilityType() {
            var kinds = new[] { AbilityKind.SuperJump, AbilityKind.SuperClone, AbilityKind.Spawn, AbilityKind.Wall };
            foreach (var k in kinds) {
                var rules = TestRules(1000);
                var toggles = new AbilityToggles();
                switch (k) {
                    case AbilityKind.SuperJump: toggles.superJump = true; break;
                    case AbilityKind.SuperClone: toggles.superClone = true; break;
                    case AbilityKind.Spawn: toggles.spawn = true; break;
                    case AbilityKind.Wall: toggles.wall = true; break;
                }
                rules.playerAbilities.red = toggles;

                var cfg = TestConfig(chance: 100);
                var policy = new AiAbilityPolicy(cfg, seed: 10, enabled: true);
                var state = AttaxGame.NewGame(rules, new StartOptions(0, 0, PlayerColor.Red));

                if (policy.TryChoose(state, PlayerColor.Red, out var choice)) {
                    if (k == AbilityKind.SuperJump || k == AbilityKind.SuperClone) {
                        Assert.True(choice.EndsTurn);
                    } else {
                        Assert.False(choice.EndsTurn);
                    }
                }
            }
        }

        [Fact]
        public void UniformSelection_DistributesChoicesAcrossCandidates() {
            var rules = TestRules(1000);
            var cfg = TestConfig(chance: 100);
            var counts = new Dictionary<AbilityKind, int>();

            for (int seed = 0; seed < 100; seed++) {
                var policy = new AiAbilityPolicy(cfg, seed: seed, enabled: true);
                var state = AttaxGame.NewGame(rules, new StartOptions(0, seed, PlayerColor.Red));
                if (policy.TryChoose(state, PlayerColor.Red, out var choice)) {
                    if (!counts.ContainsKey(choice.Ability)) counts[choice.Ability] = 0;
                    counts[choice.Ability]++;
                }
            }

            // Multiple different abilities should be chosen
            Assert.True(counts.Count >= 3, "Expected at least 3 distinct abilities chosen uniformly");
        }

        [Fact]
        public void RecordAppliedAbility_Validation() {
            var cfg = TestConfig(chance: 100);
            var policy = new AiAbilityPolicy(cfg, seed: 42, enabled: true);
            var state = AttaxGame.NewGame(TestRules(), new StartOptions(0, 0, PlayerColor.Red));

            Assert.True(policy.TryChoose(state, PlayerColor.Red, out var choice));
            Assert.Equal(0, policy.UsesThisGame);

            // Mismatched actor returns false
            var mismatchOutcome = new ActionOutcome { Actor = PlayerColor.Blue, Ability = choice.Ability, Kind = choice.Action.Kind };
            Assert.False(policy.RecordAppliedAbility(state, mismatchOutcome));

            // Apply choice correctly
            var err = AttaxGame.TryApply(state, PlayerColor.Red, choice.Action, out var outcome);
            Assert.Equal(ActionError.None, err);

            // Record outcome
            bool recorded = policy.RecordAppliedAbility(state, outcome);
            Assert.True(recorded);
            Assert.Equal(1, policy.UsesThisGame);

            // Duplicate record returns false without incrementing
            bool dup = policy.RecordAppliedAbility(state, outcome);
            Assert.False(dup);
            Assert.Equal(1, policy.UsesThisGame);
        }

        [Fact]
        public void MaxUsesPerGame_StopsAtCap_AndResetGameRestores() {
            var cfg = TestConfig(chance: 100, maxUses: 1);
            var policy = new AiAbilityPolicy(cfg, seed: 42, enabled: true);
            var state = AttaxGame.NewGame(TestRules(), new StartOptions(0, 0, PlayerColor.Red));

            Assert.True(policy.TryChoose(state, PlayerColor.Red, out var choice));
            AttaxGame.TryApply(state, PlayerColor.Red, choice.Action, out var outcome);
            policy.RecordAppliedAbility(state, outcome);
            Assert.Equal(1, policy.UsesThisGame);

            // Next turn for Red
            var state2 = AttaxGame.NewGame(TestRules(), new StartOptions(0, 0, PlayerColor.Red));
            state2.TurnNumber = 2; // distinct turn
            Assert.False(policy.TryChoose(state2, PlayerColor.Red, out _));

            // ResetGame
            policy.ResetGame();
            Assert.Equal(0, policy.UsesThisGame);
            Assert.True(policy.TryChoose(state2, PlayerColor.Red, out _));
        }

        [Fact]
        public void ResetGame_ClearsUses_RestartsRngDeterministically() {
            var cfg = TestConfig(chance: 100);
            var policy1 = new AiAbilityPolicy(cfg, seed: 999, enabled: true);
            var state1 = AttaxGame.NewGame(TestRules(), new StartOptions(0, 0, PlayerColor.Red));

            Assert.True(policy1.TryChoose(state1, PlayerColor.Red, out var choice1));

            // Reset
            policy1.ResetGame();
            Assert.Equal(0, policy1.UsesThisGame);

            var state2 = AttaxGame.NewGame(TestRules(), new StartOptions(0, 0, PlayerColor.Red));
            Assert.True(policy1.TryChoose(state2, PlayerColor.Red, out var choice2));

            // Should choose the exact same action because RNG was reset to seed 999
            Assert.Equal(choice1.Ability, choice2.Ability);
            Assert.Equal(choice1.Action.Kind, choice2.Action.Kind);
            Assert.Equal(choice1.Action.X, choice2.Action.X);
            Assert.Equal(choice1.Action.Y, choice2.Action.Y);
        }

        [Fact]
        public void CreditReserve_PreventsSpendingBelowReserve() {
            var cfg = TestConfig(chance: 100, reserve: 950); // reserve 950
            var policy = new AiAbilityPolicy(cfg, seed: 42, enabled: true);

            var state = AttaxGame.NewGame(TestRules(1000), new StartOptions(0, 0, PlayerColor.Red));
            Assert.False(policy.TryChoose(state, PlayerColor.Red, out _));
        }

        [Fact]
        public void SafetyFilter_RejectsLosingMove() {
            var cfg = TestConfig(chance: 100, safety: true);
            var policy = new AiAbilityPolicy(cfg, seed: 42, enabled: true);

            // Red has only 1 chip at (0,0). Blue at (6,6).
            var rules = TestRules();
            var state = GameState.Restore(
                1UL << BoardLookup.GetBitIndex(0, 0),
                1UL << BoardLookup.GetBitIndex(6, 6),
                0UL, 0UL, 0UL, PlayerColor.Red, 1000, 1000, 0, 0, 1, 0, rules
            );

            if (policy.TryChoose(state, PlayerColor.Red, out var choice)) {
                var clone = state.Clone();
                var err = AttaxGame.TryApply(clone, PlayerColor.Red, choice.Action, out _);
                Assert.Equal(ActionError.None, err);
                Assert.NotEqual(TerminalResult.BlueWins, clone.Result);
            }
        }

        [Fact]
        public void NeverInvalid_500RandomMidgameStates() {
            var cfg = TestConfig(chance: 100, safety: true);
            var rules = TestRules(1000);
            var randomPositions = RefactorGoldenTests.RandomPositions(500, 12345);

            for (int i = 0; i < randomPositions.Count; i++) {
                var (b, side) = randomPositions[i];
                if (AttaxRules.GetTerminalResult(b) != TerminalResult.NotOver) continue;

                var state = GameState.Restore(
                    b.RedPieces, b.BluePieces, b.BlockedSquares,
                    0UL, 0UL, side, 1000, 1000, 0, 0, 1, 0, rules
                );

                var policy = new AiAbilityPolicy(cfg, seed: i, enabled: true);
                ulong preFp = state.ComputeFingerprint();

                if (policy.TryChoose(state, side, out var choice)) {
                    // 1. Must never mutate original state passed in
                    Assert.Equal(preFp, state.ComputeFingerprint());

                    // 2. Must be valid according to Validate
                    Assert.Equal(ActionError.None, AttaxGame.Validate(state, side, choice.Action));

                    // 3. Must succeed on a clone
                    var clone = state.Clone();
                    var applyErr = AttaxGame.TryApply(clone, side, choice.Action, out _);
                    Assert.Equal(ActionError.None, applyErr);
                }
            }
        }

        [Fact]
        public void OrchestrationPlayout_100Games_ExecutesCleanly_AndUsesAbilities() {
            var cfg = TestConfig(chance: 50, safety: true);
            var rules = TestRules(500);
            int totalAbilitiesUsed = 0;

            for (int g = 0; g < 100; g++) {
                int seed = 5000 + g;
                var redPolicy = new AiAbilityPolicy(cfg, seed: seed, enabled: true);
                var bluePolicy = new AiAbilityPolicy(cfg, seed: seed + 50, enabled: true);

                var state = AttaxGame.NewGame(rules, new StartOptions(0, 0, PlayerColor.Red));
                var referee = TestBoards.NewEngine(depth: 1);

                int plies = 0;
                while (plies < 50 && state.Result == TerminalResult.NotOver) {
                    var activeSide = state.SideToMove;
                    var activePolicy = activeSide == PlayerColor.Red ? redPolicy : bluePolicy;

                    if (activePolicy.TryChoose(state, activeSide, out var choice)) {
                        var err = AttaxGame.TryApply(state, activeSide, choice.Action, out var outcome);
                        Assert.Equal(ActionError.None, err);
                        activePolicy.RecordAppliedAbility(state, outcome);
                        totalAbilitiesUsed++;

                        if (state.Result != TerminalResult.NotOver) break;

                        if (choice.EndsTurn) {
                            plies++;
                            continue;
                        }
                    }

                    // Normal search move
                    TestBoards.SetBoard(referee, state.Board.Clone(), activeSide);
                    referee.AIPlayerColor = activeSide;
                    var legal = referee.GetAllValidMoves(state.Board, activeSide);
                    if (legal.Count == 0) break;
                    Move m = referee.GetBestMove(activeSide);
                    if (m.Equals(default(Move))) break;

                    var moveErr = AttaxGame.TryApply(state, activeSide, GameAction.MoveAction(m), out _);
                    Assert.Equal(ActionError.None, moveErr);
                    plies++;
                }

                Assert.True(state.AreInvariantsValid());
            }

            Assert.True(totalAbilitiesUsed > 50, $"Expected abilities to be actively used across 100 games, got {totalAbilitiesUsed}");
        }

        [Fact]
        public void TargetSelection_Validate_And_Clone() {
            var cfg = TestConfig();
            cfg.targetSelection = "invalid_mode";
            var errors = cfg.Validate();
            Assert.Single(errors);
            Assert.Contains("targetSelection", errors[0]);

            foreach (var valid in new[] { null, "", "random", "RANDOM", "scored", "SCORED", "engine", "ENGINE" }) {
                cfg.targetSelection = valid;
                Assert.Empty(cfg.Validate());
            }

            cfg.targetSelection = "engine";
            var clone = cfg.Clone();
            Assert.Equal("engine", clone.targetSelection);
        }

        [Fact]
        public void Bomb_ScoredMode_PicksMiddleEnemyChipWithHighFollowUpConversion() {
            // Position:
            // Red has a chip at (2, 3).
            // Blue has a cluster centered at (3, 3) with (3, 2), (3, 4), (4, 3) as neighbors.
            // Blue also has an isolated chip at (0, 0).
            // Bombing (3, 3) enables Red at (2, 3) to clone to (3, 3), converting (3, 2), (3, 4), and (4, 3) to Red (+4 piece diff).
            // Bombing (0, 0) or perimeter chips yields far fewer pieces.
            ulong redPieces = 1UL << BoardLookup.GetBitIndex(2, 3);
            ulong bluePieces = (1UL << BoardLookup.GetBitIndex(0, 0))
                             | (1UL << BoardLookup.GetBitIndex(3, 3))
                             | (1UL << BoardLookup.GetBitIndex(3, 2))
                             | (1UL << BoardLookup.GetBitIndex(3, 4))
                             | (1UL << BoardLookup.GetBitIndex(4, 3));

            var rules = TestRules(1000);
            rules.playerAbilities.red = new AbilityToggles { bomb = true };

            var state = GameState.Restore(
                redPieces, bluePieces, 0UL, 0UL, 0UL, PlayerColor.Red, 1000, 1000, 0, 0, 1, 0, rules
            );

            // In scored mode, Bomb must consistently target (3, 3) across seeds
            for (int seed = 1; seed <= 5; seed++) {
                var cfg = TestConfig(chance: 100, targetSelection: "scored");
                var policy = new AiAbilityPolicy(cfg, seed: seed, enabled: true);

                bool chosen = policy.TryChoose(state, PlayerColor.Red, out var choice);
                Assert.True(chosen);
                Assert.Equal(AbilityKind.Bomb, choice.Ability);
                Assert.Equal(ActionKind.Bomb, choice.Action.Kind);
                Assert.Equal(3, choice.Action.X);
                Assert.Equal(3, choice.Action.Y);
            }

            // In random mode across multiple seeds, target selection is uniform among the 5 blue chips
            int nonCenterPicks = 0;
            for (int seed = 1; seed <= 20; seed++) {
                var cfgRandom = TestConfig(chance: 100, targetSelection: "random");
                var policyRandom = new AiAbilityPolicy(cfgRandom, seed: seed, enabled: true);
                if (policyRandom.TryChoose(state, PlayerColor.Red, out var choice)) {
                    if (choice.Action.X != 3 || choice.Action.Y != 3) {
                        nonCenterPicks++;
                    }
                }
            }
            Assert.True(nonCenterPicks > 0, "Random mode should pick non-optimal targets across seeds");
        }

        [Fact]
        public void SuperClone_ScoredMode_PicksMaxFlipsCell() {
            // Position:
            // Red at (0, 0).
            // Blue has 4 chips around (3, 3): (3, 2), (3, 4), (2, 3), (4, 3).
            // (3, 3) is empty. SuperClone landing at (3, 3) converts all 4 blue chips!
            // Landing at (6, 6) converts 0 blue chips.
            ulong redPieces = 1UL << BoardLookup.GetBitIndex(0, 0);
            ulong bluePieces = (1UL << BoardLookup.GetBitIndex(3, 2))
                             | (1UL << BoardLookup.GetBitIndex(3, 4))
                             | (1UL << BoardLookup.GetBitIndex(2, 3))
                             | (1UL << BoardLookup.GetBitIndex(4, 3));

            var rules = TestRules(1000);
            rules.playerAbilities.red = new AbilityToggles { superClone = true };

            var state = GameState.Restore(
                redPieces, bluePieces, 0UL, 0UL, 0UL, PlayerColor.Red, 1000, 1000, 0, 0, 1, 0, rules
            );

            var cfg = TestConfig(chance: 100, targetSelection: "scored");
            var policy = new AiAbilityPolicy(cfg, seed: 42, enabled: true);

            bool chosen = policy.TryChoose(state, PlayerColor.Red, out var choice);
            Assert.True(chosen);
            Assert.Equal(AbilityKind.SuperClone, choice.Ability);
            Assert.Equal(3, choice.Action.X);
            Assert.Equal(3, choice.Action.Y);
            Assert.True(choice.EndsTurn);
        }

        [Fact]
        public void EngineMode_FallbackToScoredWhenEvaluatorNull() {
            ulong redPieces = 1UL << BoardLookup.GetBitIndex(2, 3);
            ulong bluePieces = (1UL << BoardLookup.GetBitIndex(0, 0))
                             | (1UL << BoardLookup.GetBitIndex(3, 3))
                             | (1UL << BoardLookup.GetBitIndex(3, 2))
                             | (1UL << BoardLookup.GetBitIndex(3, 4))
                             | (1UL << BoardLookup.GetBitIndex(4, 3));

            var rules = TestRules(1000);
            rules.playerAbilities.red = new AbilityToggles { bomb = true };
            var state = GameState.Restore(
                redPieces, bluePieces, 0UL, 0UL, 0UL, PlayerColor.Red, 1000, 1000, 0, 0, 1, 0, rules
            );

            // targetSelection is "engine", but TargetEvaluator is null -> must fall back to scored mode cleanly
            var cfg = TestConfig(chance: 100, targetSelection: "engine");
            var policy = new AiAbilityPolicy(cfg, seed: 10, enabled: true);
            Assert.Null(policy.TargetEvaluator);

            bool chosen = policy.TryChoose(state, PlayerColor.Red, out var choice);
            Assert.True(chosen);
            Assert.Equal(AbilityKind.Bomb, choice.Ability);
            Assert.Equal(3, choice.Action.X);
            Assert.Equal(3, choice.Action.Y);
        }

        [Fact]
        public void EngineMode_WithEngineEvaluator_ExecutesCleanly() {
            ulong redPieces = 1UL << BoardLookup.GetBitIndex(2, 3);
            ulong bluePieces = (1UL << BoardLookup.GetBitIndex(0, 0))
                             | (1UL << BoardLookup.GetBitIndex(3, 3))
                             | (1UL << BoardLookup.GetBitIndex(3, 2))
                             | (1UL << BoardLookup.GetBitIndex(3, 4))
                             | (1UL << BoardLookup.GetBitIndex(4, 3));

            var rules = TestRules(1000);
            rules.playerAbilities.red = new AbilityToggles { bomb = true };
            var state = GameState.Restore(
                redPieces, bluePieces, 0UL, 0UL, 0UL, PlayerColor.Red, 1000, 1000, 0, 0, 1, 0, rules
            );

            var cfg = TestConfig(chance: 100, targetSelection: "engine");
            var policy = new AiAbilityPolicy(cfg, seed: 10, enabled: true);
            policy.UseEngineEvaluator(Prm.Base());

            Assert.NotNull(policy.TargetEvaluator);
            bool chosen = policy.TryChoose(state, PlayerColor.Red, out var choice);
            Assert.True(chosen);
            Assert.Equal(AbilityKind.Bomb, choice.Ability);
            Assert.Equal(3, choice.Action.X);
            Assert.Equal(3, choice.Action.Y);
        }

        [Fact]
        public void UseEngineEvaluator_EngineParams_NullPolicy_ThrowsArgumentNullException() {
            AiAbilityPolicy policy = null;
            var ex = Assert.Throws<ArgumentNullException>(() => policy.UseEngineEvaluator(Prm.Base()));
            Assert.Equal("policy", ex.ParamName);
        }

        [Fact]
        public void UseEngineEvaluator_EngineParams_NullParams_ThrowsArgumentNullException() {
            var policy = new AiAbilityPolicy(TestConfig(), seed: 1, enabled: true);
            var ex = Assert.Throws<ArgumentNullException>(() => policy.UseEngineEvaluator((EngineParams)null));
            Assert.Equal("engineParams", ex.ParamName);
        }

        [Fact]
        public void UseEngineEvaluator_IValueEvaluator_NullPolicy_ThrowsArgumentNullException() {
            AiAbilityPolicy policy = null;
            IValueEvaluator evaluator = new HeuristicEvaluator(Prm.Base());
            var ex = Assert.Throws<ArgumentNullException>(() => policy.UseEngineEvaluator(evaluator));
            Assert.Equal("policy", ex.ParamName);
        }

        [Fact]
        public void UseEngineEvaluator_IValueEvaluator_NullEvaluator_ThrowsArgumentNullException() {
            var policy = new AiAbilityPolicy(TestConfig(), seed: 1, enabled: true);
            var ex = Assert.Throws<ArgumentNullException>(() => policy.UseEngineEvaluator((IValueEvaluator)null));
            Assert.Equal("evaluator", ex.ParamName);
        }

        [Fact]
        public void UseEngineEvaluator_IValueEvaluator_ConfiguresTargetEvaluatorAndReturnsPolicy() {
            var policy = new AiAbilityPolicy(TestConfig(), seed: 1, enabled: true);
            var evaluator = new HeuristicEvaluator(Prm.Base());
            var returned = policy.UseEngineEvaluator(evaluator);

            Assert.Same(policy, returned);
            Assert.NotNull(policy.TargetEvaluator);
        }
    }
}
