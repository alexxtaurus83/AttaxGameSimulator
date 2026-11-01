using System;
using System.Collections.Generic;
using System.IO;
using Attax.Core.Utils;
using Newtonsoft.Json;
using Xunit;

namespace Attax.Core.Tests {
    public class GameRulesConfigTests {
        private static string GetRulesJsonPath() {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string inBin = Path.Combine(baseDir, "game-rules.json");
            if (File.Exists(inBin)) return inBin;
            string inRules = Path.Combine(baseDir, "Rules", "game-rules.json");
            if (File.Exists(inRules)) return inRules;
            string inProj = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "Attax.Core.Utils", "Rules", "game-rules.json"));
            if (File.Exists(inProj)) return inProj;
            throw new FileNotFoundException("game-rules.json not found");
        }

        [Fact]
        public void ShippedGameRulesJson_DeserializesAndValidatesCleanly() {
            string path = GetRulesJsonPath();
            string json = File.ReadAllText(path);
            var config = JsonConvert.DeserializeObject<GameRulesConfig>(json);

            Assert.NotNull(config);
            var errors = config.Validate();
            Assert.Empty(errors);

            Assert.Equal(1, config.schemaVersion);
            Assert.NotNull(config.economy);
            Assert.True(config.economy.enabled);
            Assert.Equal(0, config.economy.startingCredits);
            Assert.Equal(3000, config.economy.maxCredits);
            Assert.Equal(10, config.economy.captureReward);
            Assert.Equal(5, config.economy.footprintReward);

            Assert.NotNull(config.economy.abilityCosts);
            Assert.Equal(100, config.economy.abilityCosts.superJump);
            Assert.Equal(100, config.economy.abilityCosts.convert);
            Assert.Equal(100, config.economy.abilityCosts.block);
            Assert.Equal(100, config.economy.abilityCosts.wall);
            Assert.Equal(100, config.economy.abilityCosts.doubleMove);
            Assert.Equal(100, config.economy.abilityCosts.spawn);
            Assert.Equal(100, config.economy.abilityCosts.superClone);
            Assert.Equal(100, config.economy.abilityCosts.unblock);
            Assert.Equal(100, config.economy.abilityCosts.bomb);

            Assert.NotNull(config.turn);
            Assert.Equal(1, config.turn.maxAbilityActionsPerTurn);

            Assert.NotNull(config.playerAbilities);
            Assert.NotNull(config.playerAbilities.red);
            Assert.NotNull(config.playerAbilities.blue);
            Assert.True(config.playerAbilities.red.superJump);
            Assert.True(config.playerAbilities.blue.superJump);

            ulong fp = config.Fingerprint();
            Assert.NotEqual(0UL, fp);
            // Pin the golden fingerprint of the shipped game-rules.json
            Assert.Equal(7854671563140731726UL, fp);
        }

        [Fact]
        public void BlankConfig_FailsValidationWithAllMissingGroups() {
            var blank = new GameRulesConfig();
            var errors = blank.Validate();
            Assert.NotEmpty(errors);
            Assert.Contains(errors, e => e.Contains("economy group is missing"));
            Assert.Contains(errors, e => e.Contains("turn group is missing"));
            Assert.Contains(errors, e => e.Contains("playerAbilities group is missing"));
        }

        [Fact]
        public void Validation_ReportsEveryInvalidField() {
            var cfg = new GameRulesConfig {
                schemaVersion = 99,
                economy = new EconomyRules {
                    enabled = true,
                    maxCredits = -10,
                    startingCredits = -5,
                    captureReward = -1,
                    footprintReward = -2,
                    abilityCosts = new AbilityCosts {
                        superJump = -1,
                        convert = -2,
                        block = -3,
                        wall = -4,
                        doubleMove = -5,
                        spawn = -6,
                        superClone = -7,
                        unblock = -8,
                        bomb = -9
                    }
                },
                turn = new TurnRules { maxAbilityActionsPerTurn = 2 },
                playerAbilities = new PlayerAbilitiesConfig {
                    red = null,
                    blue = null
                }
            };

            var errors = cfg.Validate();
            Assert.Contains(errors, e => e.Contains("Unsupported schemaVersion"));
            Assert.Contains(errors, e => e.Contains("maxCredits must be positive"));
            Assert.Contains(errors, e => e.Contains("startingCredits cannot be negative"));
            Assert.Contains(errors, e => e.Contains("captureReward cannot be negative"));
            Assert.Contains(errors, e => e.Contains("footprintReward cannot be negative"));
            Assert.Contains(errors, e => e.Contains("superJump cost cannot be negative"));
            Assert.Contains(errors, e => e.Contains("convert cost cannot be negative"));
            Assert.Contains(errors, e => e.Contains("block cost cannot be negative"));
            Assert.Contains(errors, e => e.Contains("wall cost cannot be negative"));
            Assert.Contains(errors, e => e.Contains("doubleMove cost cannot be negative"));
            Assert.Contains(errors, e => e.Contains("spawn cost cannot be negative"));
            Assert.Contains(errors, e => e.Contains("superClone cost cannot be negative"));
            Assert.Contains(errors, e => e.Contains("unblock cost cannot be negative"));
            Assert.Contains(errors, e => e.Contains("bomb cost cannot be negative"));
            Assert.Contains(errors, e => e.Contains("maxAbilityActionsPerTurn must be exactly 1"));
            Assert.Contains(errors, e => e.Contains("playerAbilities.red group is missing"));
            Assert.Contains(errors, e => e.Contains("playerAbilities.blue group is missing"));
        }

        [Fact]
        public void StartingCredits_ExceedingMaxCredits_IsRejected() {
            var cfg = ValidTestRules();
            cfg.economy.maxCredits = 1000;
            cfg.economy.startingCredits = 1001;
            var errors = cfg.Validate();
            Assert.Contains(errors, e => e.Contains("startingCredits cannot exceed maxCredits"));
        }

        [Fact]
        public void Fingerprint_IsDeterministic_AndSensitiveToEveryField() {
            var baseCfg = ValidTestRules();
            ulong fpBase = baseCfg.Fingerprint();
            Assert.NotEqual(0UL, fpBase);

            // Clone produces identical fingerprint
            Assert.Equal(fpBase, baseCfg.Clone().Fingerprint());

            // Each mutated field changes fingerprint
            void AssertDiffers(Action<GameRulesConfig> mutate) {
                var c = ValidTestRules();
                mutate(c);
                Assert.NotEqual(fpBase, c.Fingerprint());
            }

            AssertDiffers(c => c.schemaVersion = 2);
            AssertDiffers(c => c.economy.enabled = !c.economy.enabled);
            AssertDiffers(c => c.economy.startingCredits += 10);
            AssertDiffers(c => c.economy.maxCredits += 500);
            AssertDiffers(c => c.economy.captureReward += 1);
            AssertDiffers(c => c.economy.footprintReward += 1);
            AssertDiffers(c => c.economy.abilityCosts.superJump += 10);
            AssertDiffers(c => c.economy.abilityCosts.convert += 10);
            AssertDiffers(c => c.economy.abilityCosts.block += 10);
            AssertDiffers(c => c.economy.abilityCosts.wall += 10);
            AssertDiffers(c => c.economy.abilityCosts.doubleMove += 10);
            AssertDiffers(c => c.economy.abilityCosts.spawn += 10);
            AssertDiffers(c => c.economy.abilityCosts.superClone += 10);
            AssertDiffers(c => c.economy.abilityCosts.unblock += 10);
            AssertDiffers(c => c.economy.abilityCosts.bomb += 10);
            AssertDiffers(c => c.turn.maxAbilityActionsPerTurn += 1);
            AssertDiffers(c => c.playerAbilities.red.superJump = false);
            AssertDiffers(c => c.playerAbilities.blue.bomb = false);
        }

        [Fact]
        public void HostLibraryContract_RejectsUnknownKeysWhenConfiguredStrictly() {
            string jsonWithExtra = @"{
                ""schemaVersion"": 1,
                ""unknownField"": 123,
                ""economy"": {
                    ""enabled"": true, ""startingCredits"": 0, ""maxCredits"": 3000, ""captureReward"": 10, ""footprintReward"": 5,
                    ""abilityCosts"": { ""superJump"": 100, ""convert"": 100, ""block"": 100, ""wall"": 100, ""doubleMove"": 100, ""spawn"": 100, ""superClone"": 100, ""unblock"": 100, ""bomb"": 100 }
                },
                ""turn"": { ""maxAbilityActionsPerTurn"": 1 },
                ""playerAbilities"": {
                    ""red"": { ""superJump"": true, ""convert"": true, ""block"": true, ""wall"": true, ""doubleMove"": true, ""spawn"": true, ""superClone"": true, ""unblock"": true, ""bomb"": true },
                    ""blue"": { ""superJump"": true, ""convert"": true, ""block"": true, ""wall"": true, ""doubleMove"": true, ""spawn"": true, ""superClone"": true, ""unblock"": true, ""bomb"": true }
                }
            }";

            var settings = new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error };
            Assert.Throws<JsonSerializationException>(() => JsonConvert.DeserializeObject<GameRulesConfig>(jsonWithExtra, settings));
        }

        [Fact]
        public void GameState_ComputeFingerprint_ChangesWithAnyField() {
            var rules = ValidTestRules();
            var state = GameState.Restore(
                red: 1UL << 0, blue: 1UL << 6, blocked: 0UL,
                redFootprints: 0UL, blueFootprints: 0UL,
                sideToMove: PlayerColor.Red, redCredits: 100, blueCredits: 150,
                turnNumber: 1, actionCount: 2, movesRemainingThisTurn: 1,
                abilityActionsUsedThisTurn: 0, rules: rules
            );

            ulong fpBase = state.ComputeFingerprint();
            Assert.NotEqual(0UL, fpBase);

            var s1 = state.Clone(); s1.Board.RedPieces |= 1UL << 1; Assert.NotEqual(fpBase, s1.ComputeFingerprint());
            var s2 = state.Clone(); s2.Board.BluePieces |= 1UL << 5; Assert.NotEqual(fpBase, s2.ComputeFingerprint());
            var s3 = state.Clone(); s3.Board.BlockedSquares |= 1UL << 24; Assert.NotEqual(fpBase, s3.ComputeFingerprint());
            var s4 = state.Clone(); s4.SideToMove = PlayerColor.Blue; Assert.NotEqual(fpBase, s4.ComputeFingerprint());
            var s5 = state.Clone(); s5.RedCredits += 5; Assert.NotEqual(fpBase, s5.ComputeFingerprint());
            var s6 = state.Clone(); s6.BlueCredits += 5; Assert.NotEqual(fpBase, s6.ComputeFingerprint());
            var s7 = state.Clone(); s7.TurnNumber += 1; Assert.NotEqual(fpBase, s7.ComputeFingerprint());
            var s8 = state.Clone(); s8.ActionCount += 1; Assert.NotEqual(fpBase, s8.ComputeFingerprint());
            var s9 = state.Clone(); s9.MovesRemainingThisTurn = 2; Assert.NotEqual(fpBase, s9.ComputeFingerprint());
            var s10 = state.Clone(); s10.AbilityActionsUsedThisTurn = 1; Assert.NotEqual(fpBase, s10.ComputeFingerprint());
            var s11 = state.Clone(); s11.RedFootprints |= 1UL << 20; Assert.NotEqual(fpBase, s11.ComputeFingerprint());
            var s12 = state.Clone(); s12.BlueFootprints |= 1UL << 20; Assert.NotEqual(fpBase, s12.ComputeFingerprint());
        }

        [Fact]
        public void GameState_Restore_StrictValidationRejections() {
            var rules = ValidTestRules();

            // Null rules
            Assert.Throws<ArgumentNullException>(() => GameState.Restore(1UL, 2UL, 0UL, 0UL, 0UL, PlayerColor.Red, 0, 0, 0, 0, 1, 0, null));

            // Invalid rules
            var badRules = ValidTestRules(); badRules.turn.maxAbilityActionsPerTurn = 3;
            Assert.Throws<ArgumentException>(() => GameState.Restore(1UL, 2UL, 0UL, 0UL, 0UL, PlayerColor.Red, 0, 0, 0, 0, 1, 0, badRules));

            // Out-of-board masks (bit 50)
            ulong oob = 1UL << 50;
            Assert.Throws<ArgumentException>(() => GameState.Restore(1UL | oob, 2UL, 0UL, 0UL, 0UL, PlayerColor.Red, 0, 0, 0, 0, 1, 0, rules));
            Assert.Throws<ArgumentException>(() => GameState.Restore(1UL, 2UL, 0UL, oob, 0UL, PlayerColor.Red, 0, 0, 0, 0, 1, 0, rules));

            // Overlapping masks
            Assert.Throws<ArgumentException>(() => GameState.Restore(1UL, 1UL, 0UL, 0UL, 0UL, PlayerColor.Red, 0, 0, 0, 0, 1, 0, rules));
            Assert.Throws<ArgumentException>(() => GameState.Restore(1UL, 2UL, 1UL, 0UL, 0UL, PlayerColor.Red, 0, 0, 0, 0, 1, 0, rules));

            // Footprint on occupied cell
            Assert.Throws<ArgumentException>(() => GameState.Restore(1UL, 2UL, 0UL, 1UL, 0UL, PlayerColor.Red, 0, 0, 0, 0, 1, 0, rules));

            // Overlapping footprints
            Assert.Throws<ArgumentException>(() => GameState.Restore(1UL, 2UL, 0UL, 4UL, 4UL, PlayerColor.Red, 0, 0, 0, 0, 1, 0, rules));

            // Invalid side
            Assert.Throws<ArgumentException>(() => GameState.Restore(1UL, 2UL, 0UL, 0UL, 0UL, PlayerColor.None, 0, 0, 0, 0, 1, 0, rules));

            // Credits outside range [0, maxCredits]
            Assert.Throws<ArgumentException>(() => GameState.Restore(1UL, 2UL, 0UL, 0UL, 0UL, PlayerColor.Red, -1, 0, 0, 0, 1, 0, rules));
            Assert.Throws<ArgumentException>(() => GameState.Restore(1UL, 2UL, 0UL, 0UL, 0UL, PlayerColor.Red, rules.economy.maxCredits + 1, 0, 0, 0, 1, 0, rules));

            // Counters invalid
            Assert.Throws<ArgumentException>(() => GameState.Restore(1UL, 2UL, 0UL, 0UL, 0UL, PlayerColor.Red, 0, 0, -1, 0, 1, 0, rules));
            Assert.Throws<ArgumentException>(() => GameState.Restore(1UL, 2UL, 0UL, 0UL, 0UL, PlayerColor.Red, 0, 0, 2, 1, 1, 0, rules)); // TurnNumber > ActionCount
            Assert.Throws<ArgumentException>(() => GameState.Restore(1UL, 2UL, 0UL, 0UL, 0UL, PlayerColor.Red, 0, 0, 1, 1, 3, 0, rules)); // movesRemaining = 3
            Assert.Throws<ArgumentException>(() => GameState.Restore(1UL, 2UL, 0UL, 0UL, 0UL, PlayerColor.Red, 0, 0, 1, 1, 2, 0, rules)); // movesRemaining=2 but ability=0
            Assert.Throws<ArgumentException>(() => GameState.Restore(1UL, 2UL, 0UL, 0UL, 0UL, PlayerColor.Red, 0, 0, 1, 0, 1, 0, rules)); // ActionCount=0 but TurnNumber=1

            // Disabled economy strict checks
            var disabledRules = ValidTestRules(); disabledRules.economy.enabled = false;
            Assert.Throws<ArgumentException>(() => GameState.Restore(1UL, 2UL, 0UL, 0UL, 0UL, PlayerColor.Red, 10, 0, 0, 0, 1, 0, disabledRules));
            Assert.Throws<ArgumentException>(() => GameState.Restore(1UL, 2UL, 0UL, 4UL, 0UL, PlayerColor.Red, 0, 0, 0, 0, 1, 0, disabledRules));
        }

        private static GameRulesConfig _cachedShippedRules;

        public static GameRulesConfig ValidTestRules() {
            if (_cachedShippedRules == null) {
                string path = GetRulesJsonPath();
                _cachedShippedRules = JsonConvert.DeserializeObject<GameRulesConfig>(File.ReadAllText(path));
            }
            return _cachedShippedRules.Clone();
        }
    }
}
