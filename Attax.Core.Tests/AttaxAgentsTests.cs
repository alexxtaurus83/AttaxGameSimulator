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
            var rules = GameRulesConfigTests.ValidTestRules();
            var state = AttaxGame.NewGame(rules, new StartOptions(0, 42, PlayerColor.Red));
            var spec = PlayerSpec.Parse("random");
            using var player = PlayerFactory.Create(spec, null!, seed: 123, defaultTrain: false);

            var planner = new AgentTurnPlanner(player, abilities: null);
            var action = planner.Decide(state, PlayerColor.Red, out bool isAbility, out var decision);

            Assert.False(isAbility);
            Assert.Equal(ActionKind.Move, action.Kind);
            Assert.True(decision.HasMove);
            Assert.Equal(ActionError.None, AttaxGame.Validate(state, PlayerColor.Red, action));
        }
    }
}
