using System;
using System.IO;
using Attax.Client.Protocol;
using Attax.Core;
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

            var defaultOptions = new BotOptions { Name = "bot1" };
            defaultOptions.Validate();
            Assert.False(defaultOptions.DisableQuiescence);
        }

        [Fact]
        public void BotOptions_ResolveSpec_HandlesCoresAndDisableQuiescence() {
            int sysCores = Math.Clamp(Environment.ProcessorCount, 1, 64);

            // Default: if cores is omitted, injects system processor count; preserves default quiescence (enabled)
            var optDefault = new BotOptions { Spec = "classic:depth=3", DisableQuiescence = false };
            Assert.Equal($"classic:depth=3,cores={sysCores}", optDefault.ResolveSpec());
            var specDefault = PlayerSpec.Parse(optDefault.ResolveSpec());
            var (trainDefault, qDefault) = PlayerFactory.EffectiveMode(specDefault, defaultTrain: false);
            Assert.True(qDefault);
            Assert.Equal(sysCores, specDefault.Cores);

            // Spec already specifying cores preserves that value when --cores is omitted
            var optSpecWithCores = new BotOptions { Spec = "classic:depth=3,cores=2" };
            Assert.Equal("classic:depth=3,cores=2", optSpecWithCores.ResolveSpec());
            var parsedWithCores = PlayerSpec.Parse(optSpecWithCores.ResolveSpec());
            Assert.Equal(2, parsedWithCores.Cores);

            // DisableQuiescence = true on spec with parameters
            var optDisableQ = new BotOptions { Spec = "classic:depth=3", DisableQuiescence = true };
            Assert.Equal($"classic:depth=3,cores={sysCores},quiescence=false", optDisableQ.ResolveSpec());
            var specDisableQ = PlayerSpec.Parse(optDisableQ.ResolveSpec());
            var (trainDisableQ, qDisableQ) = PlayerFactory.EffectiveMode(specDisableQ, defaultTrain: false);
            Assert.False(qDisableQ);

            // DisableQuiescence = true on bare spec without colon
            var optBare = new BotOptions { Spec = "classic", DisableQuiescence = true };
            Assert.Equal($"classic:cores={sysCores},quiescence=false", optBare.ResolveSpec());

            // DisableQuiescence = true overrides existing quiescence=true
            var optOverrideQ = new BotOptions { Spec = "classic:depth=3,quiescence=true", DisableQuiescence = true };
            Assert.Equal($"classic:depth=3,quiescence=false,cores={sysCores}", optOverrideQ.ResolveSpec());
            var specOverrideQ = PlayerSpec.Parse(optOverrideQ.ResolveSpec());
            Assert.Equal(sysCores, specOverrideQ.Cores);
            Assert.False(specOverrideQ.Quiescence);

            // Random spec is untouched by default cores
            var optRandom = new BotOptions { Spec = "random" };
            Assert.Equal("random", optRandom.ResolveSpec());

            // Explicit Cores adds or overrides cores
            var optCores = new BotOptions { Spec = "classic:depth=2", Cores = 4 };
            Assert.Equal("classic:depth=2,cores=4", optCores.ResolveSpec());

            var optOverrideCores = new BotOptions { Spec = "classic:depth=2,cores=1", Cores = 4 };
            Assert.Equal("classic:depth=2,cores=4", optOverrideCores.ResolveSpec());

            // Both explicit Cores and DisableQuiescence
            var optBoth = new BotOptions { Spec = "classic:depth=2", Cores = 2, DisableQuiescence = true };
            Assert.Equal("classic:depth=2,cores=2,quiescence=false", optBoth.ResolveSpec());
            var specBoth = PlayerSpec.Parse(optBoth.ResolveSpec());
            Assert.Equal(2, specBoth.Cores);
            Assert.False(specBoth.Quiescence);
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
            var coreRules = ValidTestRules();
            var startBoard = StartPosition.Create(0, 42, PlayerColor.Red);
            var initialCoreState = GameState.CreateInitial(startBoard, PlayerColor.Red, coreRules);

            var rulesDto = ToRulesDto(coreRules);

            var snapshot = new GameSnapshotDto {
                GameId = "game_1",
                ServerVersion = 1,
                ActionCount = 0,
                TurnNumber = 0,
                SideToMove = PlayerColorDto.Red,
                MovesRemainingThisTurn = 1,
                AbilityActionsUsedThisTurn = 0,
                FrozenSourcesMask = HexFormat.ToHex(initialCoreState.FrozenSourcesThisTurn),
                ForbiddenLandingsMask = HexFormat.ToHex(initialCoreState.ForbiddenLandingsThisTurn),
                ResolvedAbilityCosts = SnapshotMapper.ResolveAbilityCosts(rulesDto, initialCoreState.Board.VoidSquares),
                RedCredits = initialCoreState.RedCredits,
                BlueCredits = initialCoreState.BlueCredits,
                RedPiecesMask = HexFormat.ToHex(initialCoreState.Board.RedPieces),
                BluePiecesMask = HexFormat.ToHex(initialCoreState.Board.BluePieces),
                BlockedSquaresMask = HexFormat.ToHex(initialCoreState.Board.BlockedSquares),
                VoidSquaresMask = HexFormat.ToHex(initialCoreState.Board.VoidSquares),
                RedFootprintsMask = "0x0000000000000000",
                BlueFootprintsMask = "0x0000000000000000",
                ZobristHash = HexFormat.ToHex(initialCoreState.Board.ZobristHash),
                RulesFingerprint = HexFormat.ToHex(coreRules.Fingerprint()),
                StateFingerprint = HexFormat.ToHex(initialCoreState.ComputeFingerprint()),
                Rules = rulesDto
            };

            var restored = BotSnapshot.ToState(snapshot);
            Assert.Equal(initialCoreState.ComputeFingerprint(), restored.ComputeFingerprint());
            Assert.Equal(PlayerColor.Red, restored.SideToMove);
        }

        [Fact]
        public void BotSnapshot_ToState_RoundtripsRestrictedSnapshotAndScaledCosts() {
            var coreRules = ValidTestRules();
            coreRules.abilityAllowance = AbilityAllowance.All();
            coreRules.abilityAllowance.red[(int)AbilityKind.Spawn] = 2;

            ulong voidMask = (1UL << 10) | (1UL << 11) | (1UL << 12) |
                             (1UL << 24) | (1UL << 25) | (1UL << 26) |
                             (1UL << 31) | (1UL << 32) | (1UL << 33);
            ulong redPieces = (1UL << 0) | (1UL << 1) | (1UL << 48);
            ulong bluePieces = (1UL << 6) | (1UL << 42);
            ulong frozenMask = 1UL << 1;

            var restrictedState = GameState.Restore(
                red: redPieces,
                blue: bluePieces,
                blocked: 0UL,
                redFootprints: 0UL,
                blueFootprints: 0UL,
                sideToMove: PlayerColor.Red,
                redCredits: 120,
                blueCredits: 80,
                turnNumber: 1,
                actionCount: 2,
                movesRemainingThisTurn: 1,
                abilityActionsUsedThisTurn: 1,
                rules: coreRules,
                voidSquares: voidMask,
                frozenSources: frozenMask,
                forbiddenLandings: 0UL);

            var rulesDto = ToRulesDto(coreRules);
            var snapshot = new GameSnapshotDto {
                GameId = "game_restricted",
                ServerVersion = 3,
                ActionCount = restrictedState.ActionCount,
                TurnNumber = restrictedState.TurnNumber,
                SideToMove = PlayerColorDto.Red,
                MovesRemainingThisTurn = restrictedState.MovesRemainingThisTurn,
                AbilityActionsUsedThisTurn = restrictedState.AbilityActionsUsedThisTurn,
                FrozenSourcesMask = HexFormat.ToHex(restrictedState.FrozenSourcesThisTurn),
                ForbiddenLandingsMask = HexFormat.ToHex(restrictedState.ForbiddenLandingsThisTurn),
                ResolvedAbilityCosts = SnapshotMapper.ResolveAbilityCosts(rulesDto, voidMask),
                RedCredits = restrictedState.RedCredits,
                BlueCredits = restrictedState.BlueCredits,
                RedPiecesMask = HexFormat.ToHex(restrictedState.Board.RedPieces),
                BluePiecesMask = HexFormat.ToHex(restrictedState.Board.BluePieces),
                BlockedSquaresMask = HexFormat.ToHex(restrictedState.Board.BlockedSquares),
                VoidSquaresMask = HexFormat.ToHex(restrictedState.Board.VoidSquares),
                RedFootprintsMask = HexFormat.ToHex(restrictedState.RedFootprints),
                BlueFootprintsMask = HexFormat.ToHex(restrictedState.BlueFootprints),
                ZobristHash = HexFormat.ToHex(restrictedState.Board.ZobristHash),
                RulesFingerprint = HexFormat.ToHex(coreRules.Fingerprint()),
                StateFingerprint = HexFormat.ToHex(restrictedState.ComputeFingerprint()),
                Rules = rulesDto
            };

            var restored = BotSnapshot.ToState(snapshot);
            Assert.Equal(restrictedState.ComputeFingerprint(), restored.ComputeFingerprint());
            Assert.Equal(frozenMask, restored.FrozenSourcesThisTurn);
            Assert.Equal(0UL, restored.ForbiddenLandingsThisTurn);
            Assert.Equal(40, restored.PlayableCells);
            Assert.Equal(restrictedState.GetAbilityCost(AbilityKind.Spawn, 2), restored.GetAbilityCost(AbilityKind.Spawn, 2));
            Assert.Equal(ActionError.SourceFrozenThisTurn, AttaxGame.Validate(restored, PlayerColor.Red, GameAction.MoveAction(new Move(1, 0, 2, 0))));
            Assert.Equal(ActionError.AbilityLevelNotAllowed, AttaxGame.Validate(restored, PlayerColor.Red, GameAction.Targeted(ActionKind.Spawn, 2, 3, 3)));

            snapshot.ResolvedAbilityCosts["spawn"][0] += 5;
            Assert.Throws<InvalidOperationException>(() => BotSnapshot.ToState(snapshot));
        }

        [Fact]
        public void ActionIntentMapper_ToIntent_MapsAllActionKinds() {
            var move = GameAction.MoveAction(new Move(0, 0, 1, 1));
            var moveIntent = ActionIntentMapper.ToIntent(move);
            Assert.Equal(ActionKindDto.Move, moveIntent.Kind);
            Assert.Null(moveIntent.Level);
            Assert.Equal(0, moveIntent.FromX);
            Assert.Equal(0, moveIntent.FromY);
            Assert.Equal(1, moveIntent.ToX);
            Assert.Equal(1, moveIntent.ToY);

            var spawn = GameAction.Targeted(ActionKind.Spawn, 2, 3, 2);
            var spawnIntent = ActionIntentMapper.ToIntent(spawn);
            Assert.Equal(ActionKindDto.Spawn, spawnIntent.Kind);
            Assert.Equal(2, spawnIntent.Level);
            Assert.Equal(2, spawnIntent.TargetX);
            Assert.Equal(3, spawnIntent.TargetY);

            var doubleMove = GameAction.DoubleMoveAction();
            var dmIntent = ActionIntentMapper.ToIntent(doubleMove);
            Assert.Equal(ActionKindDto.DoubleMove, dmIntent.Kind);
            Assert.Equal(1, dmIntent.Level);
        }

        private static GameRulesConfigDto ToRulesDto(GameRulesConfig coreRules) => new GameRulesConfigDto {
            SchemaVersion = coreRules.schemaVersion,
            WinRule = coreRules.winRule,
            Economy = new EconomyRulesDto {
                Enabled = coreRules.economy.enabled,
                StartingCredits = coreRules.economy.startingCredits,
                MaxCredits = coreRules.economy.maxCredits,
                CaptureReward = coreRules.economy.captureReward,
                FootprintReward = coreRules.economy.footprintReward,
                AbilityCosts = new AbilityCostsDto {
                    SuperJump = (int[])coreRules.economy.abilityCosts.superJump.Clone(),
                    Convert = (int[])coreRules.economy.abilityCosts.convert.Clone(),
                    Block = (int[])coreRules.economy.abilityCosts.block.Clone(),
                    Wall = (int[])coreRules.economy.abilityCosts.wall.Clone(),
                    DoubleMove = (int[])coreRules.economy.abilityCosts.doubleMove.Clone(),
                    Spawn = (int[])coreRules.economy.abilityCosts.spawn.Clone(),
                    SuperClone = (int[])coreRules.economy.abilityCosts.superClone.Clone(),
                    Unblock = (int[])coreRules.economy.abilityCosts.unblock.Clone(),
                    Bomb = (int[])coreRules.economy.abilityCosts.bomb.Clone()
                },
                BoardScaling = new BoardScalingDto {
                    ReferencePlayable = coreRules.economy.boardScaling.referencePlayable,
                    Alpha = coreRules.economy.boardScaling.alpha,
                    MinFactor = coreRules.economy.boardScaling.minFactor,
                    RoundStep = coreRules.economy.boardScaling.roundStep
                }
            },
            Turn = new TurnRulesDto { MaxAbilityActionsPerTurn = coreRules.turn.maxAbilityActionsPerTurn },
            AbilityAllowance = coreRules.abilityAllowance == null ? null : new AbilityAllowanceDto {
                Red = (int[])coreRules.abilityAllowance.red.Clone(),
                Blue = (int[])coreRules.abilityAllowance.blue.Clone()
            }
        };

        private static GameRulesConfig ValidTestRules() => new GameRulesConfig {
            schemaVersion = GameRulesConfig.CurrentSchemaVersion,
            winRule = GameRulesConfig.WinRuleChips,
            economy = new EconomyRules {
                enabled = true,
                startingCredits = 0,
                maxCredits = 300,
                captureReward = 10,
                footprintReward = 5,
                abilityCosts = new AbilityCosts {
                    superJump = new[] { 100 },
                    convert = new[] { 60, 75, 90 },
                    block = new[] { 40, 50 },
                    wall = new[] { 50 },
                    doubleMove = new[] { 120 },
                    spawn = new[] { 60, 75, 90 },
                    superClone = new[] { 150 },
                    unblock = new[] { 50, 80 },
                    bomb = new[] { 170, 215, 255 }
                },
                boardScaling = new BoardScaling {
                    referencePlayable = 48,
                    alpha = 1.0,
                    minFactor = 0.5,
                    roundStep = 5
                }
            },
            turn = new TurnRules { maxAbilityActionsPerTurn = 1 }
        };
    }
}
