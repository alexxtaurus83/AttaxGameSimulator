using System;
using System.Reflection;
using Xunit;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core.Tests {
    public class MaxRootParallelismTests {
        private static BitboardState Midgame() => TestBoards.Parse(
            "RR..B..",
            "R.R.BB.",
            "..R..B.",
            "X..B...",
            "..R..R.",
            "B..B.R.",
            ".B....R");

        [Fact]
        public void MaxRootParallelism_LimitsRootHelpers_AndProducesValidMove() {
            var prm = Prm.Base();
            var cfg = new AIEngineConfig {
                AiDepth = 2,
                DisableParallelRootSearch = false,
                MaxRootParallelism = 2,
                Seed = 42
            };

            using var engine = new AtaxxAIEngine(prm, cfg);
            var board = Midgame();
            TestBoards.SetBoard(engine, board, PlayerColor.Red);
            engine.AIPlayerColor = PlayerColor.Red;

            var move = engine.GetBestMove(PlayerColor.Red);
            Assert.False(move.Equals(default(Move)));

            var poolField = typeof(AtaxxAIEngine).GetField("_parallelHelpers", BindingFlags.NonPublic | BindingFlags.Instance);
            var helpers = (AtaxxThreadHelper[])poolField.GetValue(engine);
            Assert.NotNull(helpers);
            Assert.Equal(2, helpers.Length);
        }

        [Fact]
        public void MaxRootParallelism_Zero_DefaultsToProcessorCount() {
            var prm = Prm.Base();
            var cfg = new AIEngineConfig {
                AiDepth = 1,
                DisableParallelRootSearch = false,
                MaxRootParallelism = 0,
                Seed = 42
            };

            using var engine = new AtaxxAIEngine(prm, cfg);
            var board = Midgame();
            TestBoards.SetBoard(engine, board, PlayerColor.Red);
            engine.AIPlayerColor = PlayerColor.Red;

            var move = engine.GetBestMove(PlayerColor.Red);
            Assert.False(move.Equals(default(Move)));

            var poolField = typeof(AtaxxAIEngine).GetField("_parallelHelpers", BindingFlags.NonPublic | BindingFlags.Instance);
            var helpers = (AtaxxThreadHelper[])poolField.GetValue(engine);
            Assert.NotNull(helpers);
            // Parallelism is capped by allMoves.Count and ProcessorCount
            Assert.True(helpers.Length > 0 && helpers.Length <= Environment.ProcessorCount);
        }
    }
}
