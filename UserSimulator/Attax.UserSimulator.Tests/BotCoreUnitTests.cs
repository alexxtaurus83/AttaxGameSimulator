using System;
using System.IO;
using Attax.Core;
using Attax.Core.Tests;
using Attax.Core.Utils;
using Attax.Play;
using Attax.Server.Contracts.Actions;
using Attax.Server.Contracts.Common;
using Attax.Server.Contracts.Gameplay;
using Xunit;

namespace Attax.UserSimulator.Tests {
    public class BotCoreUnitTests {
        [Fact]
        public void BotOptions_Validation_ValidatesCorrectly() {
            var options = new BotOptions {
                Name = "bot_alpha",
                Mode = "lobby",
                ThinkMs = "500-1500"
            };
            options.Validate();
            Assert.Equal((500, 1500), options.ParsedThinkMs);

            var invalidName = new BotOptions { Name = "bad name with spaces!" };
            Assert.Throws<ArgumentException>(() => invalidName.Validate());

            var invalidMode = new BotOptions { Name = "bot1", Mode = "invalid" };
            Assert.Throws<ArgumentException>(() => invalidMode.Validate());

            var privateWithoutRole = new BotOptions { Name = "bot1", Mode = "private" };
            Assert.Throws<ArgumentException>(() => privateWithoutRole.Validate());

            var invalidThink = new BotOptions { Name = "bot1", Mode = "lobby", ThinkMs = "2000-1000" };
            Assert.Throws<ArgumentException>(() => invalidThink.Validate());

            var waitBot = new BotOptions { Name = "bot1", Mode = "lobby", LobbyBehavior = "wait" };
            waitBot.Validate();
            Assert.Equal("wait", waitBot.LobbyBehavior);

            var idleBot = new BotOptions { Name = "bot1", Mode = "lobby", LobbyBehavior = "idle" };
            idleBot.Validate();
            Assert.Equal("wait", idleBot.LobbyBehavior);

            var searchBot = new BotOptions { Name = "bot1", Mode = "lobby", LobbyBehavior = "search" };
            searchBot.Validate();
            Assert.Equal("search", searchBot.LobbyBehavior);

            var invalidBehavior = new BotOptions { Name = "bot1", Mode = "lobby", LobbyBehavior = "invalid" };
            Assert.Throws<ArgumentException>(() => invalidBehavior.Validate());
        }

        [Fact]
        public void CredentialStore_SavesAndLoadsCorrectly() {
            string tempDir = Path.Combine(Path.GetTempPath(), "bot_state_" + Guid.NewGuid().ToString("N"));
            try {
                var store = new CredentialStore(tempDir);
                Assert.Null(store.Load("bot1"));

                store.Save("bot1", "p_12345", "sec_abcdef");
                var loaded = store.Load("bot1");
                Assert.NotNull(loaded);
                Assert.Equal("p_12345", loaded!.PlayerId);
                Assert.Equal("sec_abcdef", loaded!.RecoverySecret);
            } finally {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void SnapshotMapper_ToState_RoundtripsFingerprint() {
            var coreRules = GameRulesConfigTests.ValidTestRules();
            var initialCoreState = AttaxGame.NewGame(coreRules, new StartOptions(0, 42, PlayerColor.Red));

            var rulesDto = new GameRulesConfigDto {
                SchemaVersion = coreRules.schemaVersion,
                Economy = new EconomyRulesDto {
                    Enabled = coreRules.economy.enabled,
                    StartingCredits = coreRules.economy.startingCredits,
                    MaxCredits = coreRules.economy.maxCredits,
                    CaptureReward = coreRules.economy.captureReward,
                    FootprintReward = coreRules.economy.footprintReward,
                    AbilityCosts = new AbilityCostsDto {
                        SuperJump = coreRules.economy.abilityCosts.superJump,
                        Convert = coreRules.economy.abilityCosts.convert,
                        Block = coreRules.economy.abilityCosts.block,
                        Wall = coreRules.economy.abilityCosts.wall,
                        DoubleMove = coreRules.economy.abilityCosts.doubleMove,
                        Spawn = coreRules.economy.abilityCosts.spawn,
                        SuperClone = coreRules.economy.abilityCosts.superClone,
                        Unblock = coreRules.economy.abilityCosts.unblock,
                        Bomb = coreRules.economy.abilityCosts.bomb
                    }
                },
                Turn = new TurnRulesDto { MaxAbilityActionsPerTurn = coreRules.turn.maxAbilityActionsPerTurn },
                PlayerAbilities = new PlayerAbilitiesConfigDto {
                    Red = new AbilityTogglesDto(),
                    Blue = new AbilityTogglesDto()
                }
            };

            var snapshot = new GameSnapshotDto {
                GameId = "game_1",
                ServerVersion = 1,
                ActionCount = 0,
                TurnNumber = 0,
                SideToMove = PlayerColorDto.Red,
                MovesRemainingThisTurn = 1,
                AbilityActionsUsedThisTurn = 0,
                RedCredits = initialCoreState.RedCredits,
                BlueCredits = initialCoreState.BlueCredits,
                RedPiecesMask = HexFormat.ToHex(initialCoreState.Board.RedPieces),
                BluePiecesMask = HexFormat.ToHex(initialCoreState.Board.BluePieces),
                BlockedSquaresMask = HexFormat.ToHex(initialCoreState.Board.BlockedSquares),
                RedFootprintsMask = "0x0000000000000000",
                BlueFootprintsMask = "0x0000000000000000",
                ZobristHash = HexFormat.ToHex(initialCoreState.Board.ZobristHash),
                RulesFingerprint = HexFormat.ToHex(coreRules.Fingerprint()),
                StateFingerprint = HexFormat.ToHex(initialCoreState.ComputeFingerprint()),
                Rules = rulesDto
            };

            var restored = SnapshotMapper.ToState(snapshot);
            Assert.Equal(initialCoreState.ComputeFingerprint(), restored.ComputeFingerprint());
            Assert.Equal(PlayerColor.Red, restored.SideToMove);
        }

        [Fact]
        public void SnapshotMapper_ToIntent_MapsAllActionKinds() {
            var move = GameAction.MoveAction(new Move(0, 0, 1, 1));
            var moveIntent = SnapshotMapper.ToIntent(move);
            Assert.Equal(ActionKindDto.Move, moveIntent.Kind);
            Assert.Equal(0, moveIntent.FromX);
            Assert.Equal(0, moveIntent.FromY);
            Assert.Equal(1, moveIntent.ToX);
            Assert.Equal(1, moveIntent.ToY);

            var spawn = GameAction.Targeted(ActionKind.Spawn, 2, 3);
            var spawnIntent = SnapshotMapper.ToIntent(spawn);
            Assert.Equal(ActionKindDto.Spawn, spawnIntent.Kind);
            Assert.Equal(2, spawnIntent.ToX);
            Assert.Equal(3, spawnIntent.ToY);

            var doubleMove = GameAction.DoubleMoveAction();
            var dmIntent = SnapshotMapper.ToIntent(doubleMove);
            Assert.Equal(ActionKindDto.DoubleMove, dmIntent.Kind);
        }
    }
}
