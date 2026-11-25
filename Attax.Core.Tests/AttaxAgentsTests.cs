using System;
using Attax.Agents;
using Attax.Core;
using Attax.Core.Utils;
using Attax.Play;
using Xunit;

namespace Attax.Core.Tests {
    public class AttaxAgentsTests {
        [Fact]
        public void LocalGameRunner_PlaysSeededRandomGamesToCompletion() {
            using var referee = LocalGameRunner.NewReferee();
            for (int seed = 1; seed <= 3; seed++) {
                var rng = new Random(seed);
                var board = StartBoard.Create(referee, 0, 0, rng);
                board.ZobristHash = referee.ComputeZobristHash(board, PlayerColor.Red);

                var end = LocalGameRunner.RunGame(
                    referee,
                    board,
                    startPly: 0,
                    maxPlies: 200,
                    chooser: (ply, side, b) => LocalGameRunner.RandomDecision(referee, b, side, rng)
                );

                Assert.True(end.Plies > 0);
                Assert.True(end.Terminal || end.Plies == 200);
                Assert.True(end.ResultRedPov is 1 or -1 or 0);
            }
        }

        [Fact]
        public void BoardRenderer_StartBoard_MatchesExpectedSnapshot() {
            var board = new BitboardState();
            // Start board has 2 red pieces (top-left, bottom-right) and 2 blue pieces (top-right, bottom-left)
            // (0,0)=0, (6,0)=6, (0,6)=42, (6,6)=48
            board.RedPieces = (1UL << 0) | (1UL << 48);
            board.BluePieces = (1UL << 6) | (1UL << 42);

            string rendered = BoardRenderer.Render(board, PlayerColor.Red, redCredits: 100, blueCredits: 200);
            Assert.Contains("  a b c d e f g", rendered);
            Assert.Contains("1 R . . . . . B", rendered);
            Assert.Contains("7 B . . . . . R", rendered);
            Assert.Contains("Red 2 (100 credits) | Blue 2 (200 credits) | to move: Red", rendered);
        }

        [Fact]
        public void AgentTurnPlanner_Decide_ReturnsLegalMove() {
            var rules = ValidTestRules();
            var state = NewGame(rules, 0, 42, PlayerColor.Red);
            var spec = PlayerSpec.Parse("random");
            using var player = PlayerFactory.Create(spec, null!, seed: 123, defaultTrain: false);

            var planner = new AgentTurnPlanner(player, abilityConfig: null);
            var action = planner.Decide(state, PlayerColor.Red, out bool isAbility, out var decision);

            Assert.False(isAbility);
            Assert.Equal(ActionKind.Move, action.Kind);
            Assert.Equal(0, action.Level);
            Assert.True(decision.HasMove);
            Assert.Equal(ActionError.None, AttaxGame.Validate(state, PlayerColor.Red, action));
        }

        [Fact]
        public void AgentTurnPlanner_WithAbilityConfig_PlaysLegalActionsAndUsesAnAbilityOverAGame() {
            var rules = ValidTestRules();
            rules.abilityAllowance = AbilityAllowance.All();
            var state = NewGame(rules, 0, 42, PlayerColor.Red);
            state.RedCredits = state.BlueCredits = 300;
            using var player = PlayerFactory.Create(PlayerSpec.Parse("classic:depth=2"), null!, seed: 123, defaultTrain: false);
            var cfg = new AiAbilityConfig {
                schemaVersion = AiAbilityConfig.CurrentSchemaVersion, aiAbilitiesEnabled = true, creditValueChips = 0.05f,
                minGainChips = 0.3f, maxCandidatesVerified = 6, verifyDepthOffset = 1, savingEnabled = false
            };
            var planner = new AgentTurnPlanner(player, cfg);

            int abilities = 0;
            for (int ply = 0; ply < 120 && state.Result == TerminalResult.NotOver; ply++) {
                var side = state.SideToMove;
                var action = planner.Decide(state, side, out bool isAbility, out _);
                Assert.Equal(ActionError.None, AttaxGame.TryApply(state, side, action, out _));
                if (isAbility) abilities++;
            }
            Assert.True(abilities > 0, "the AI must verify and cast at least one ability");
        }

        [Fact]
        public void AgentTurnPlanner_NonSearchPlayer_NeverCastsAbilities() {
            var state = NewGame(ValidTestRules(), 0, 42, PlayerColor.Red);
            state.RedCredits = 300;
            using var player = PlayerFactory.Create(PlayerSpec.Parse("random"), null!, seed: 1, defaultTrain: false);
            var cfg = new AiAbilityConfig {
                schemaVersion = AiAbilityConfig.CurrentSchemaVersion, aiAbilitiesEnabled = true, creditValueChips = 0f,
                minGainChips = 0f, maxCandidatesVerified = 8, verifyDepthOffset = 1, savingEnabled = true
            };
            planner_Decide(new AgentTurnPlanner(player, cfg), state);
        }

        private static void planner_Decide(AgentTurnPlanner planner, GameState state) {
            var action = planner.Decide(state, PlayerColor.Red, out bool isAbility, out _);
            Assert.False(isAbility);
            Assert.Equal(ActionKind.Move, action.Kind);
        }

        [Fact]
        public void AgentTurnPlanner_RejectsAV1Config() {
            using var player = PlayerFactory.Create(PlayerSpec.Parse("random"), null!, seed: 1, defaultTrain: false);
            Assert.Throws<ArgumentException>(() => new AgentTurnPlanner(player, new AiAbilityConfig { schemaVersion = 1 }));
        }

        [Fact]
        public void AgentTurnPlanner_SearchPlayer_HonoursFrozenSourceWithoutFallback() {
            var state = GameState.Restore(1UL | (1UL << 24), 1UL << 48, 0, 0, 0,
                PlayerColor.Red, 0, 0, 0, 1, 1, 1, ValidTestRules(), frozenSources: 1UL << 24);
            using var player = PlayerFactory.Create(PlayerSpec.Parse("classic:depth=2"), null!, seed: 5, defaultTrain: false);
            var planner = new AgentTurnPlanner(player);
            var action = planner.Decide(state, PlayerColor.Red, out bool isAbility, out var decision);

            Assert.False(isAbility);
            Assert.True(decision.HasMove);
            Assert.False(decision.UsedFallback);
            Assert.NotEqual((3, 3), (action.Move.FromX, action.Move.FromY));
            Assert.Equal(ActionError.None, AttaxGame.Validate(state, PlayerColor.Red, action));
        }

        [Fact]
        public void AgentTurnPlanner_FallsBackWhenRawSearchSelectsFrozenSource() {
            var state = GameState.Restore(1UL | (1UL << 24), 1UL << 48, 0, 0, 0,
                PlayerColor.Red, 0, 0, 0, 1, 1, 1, ValidTestRules(), frozenSources: 1UL << 24);
            using var player = new FixedMovePlayer(new Move(3, 3, 4, 3));
            var planner = new AgentTurnPlanner(player);
            var action = planner.Decide(state, PlayerColor.Red, out bool isAbility, out var decision);

            Assert.False(isAbility);
            Assert.True(decision.HasMove);
            Assert.True(decision.UsedFallback);
            Assert.Equal(ActionCodec.Encode(action.Move), decision.Action);
            Assert.Equal(ActionError.None, AttaxGame.Validate(state, PlayerColor.Red, action));
        }

        private sealed class FixedMovePlayer(Move move) : IPlayer {
            public PlayerSpec Spec => PlayerSpec.Parse("random");
            public MoveDecision Choose(BitboardState board, PlayerColor side) => new MoveDecision { HasMove = true, Move = move };
            public void Dispose() { }
        }

        [Fact]
        public void SimulatorReferee_UsesChipCountOnly_RegardlessOfWinRuleConfig() {
            var rules = ValidTestRules();
            Assert.Equal(WinRule.Chips, rules.GetWinRule());
            using var referee = LocalGameRunner.NewReferee();
            var board = new BitboardState {
                RedPieces = 1UL << BoardLookup.GetBitIndex(0, 0),
                BluePieces = 1UL << BoardLookup.GetBitIndex(6, 6),
                BlockedSquares = BitboardState.BoardMask
                    & ~((1UL << BoardLookup.GetBitIndex(0, 0)) | (1UL << BoardLookup.GetBitIndex(6, 6)) | (1UL << BoardLookup.GetBitIndex(3, 3)))
            };
            // Equal chips (1 vs 1) with neither side able to move is Draw under chip-only simulator rules
            Assert.Equal(TerminalResult.Draw, referee.GetTerminalResult(board));
        }

        private static GameState NewGame(GameRulesConfig rules, int blockedCells, int seed, PlayerColor firstPlayer) {
            BitboardState board = StartPosition.Create(blockedCells, seed, firstPlayer);
            return GameState.CreateInitial(board, firstPlayer, rules);
        }

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
