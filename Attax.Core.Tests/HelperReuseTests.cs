using System.Collections.Generic;
using Attax.Core;
using Xunit;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core.Tests {
    public class HelperReuseTests {
        private static BitboardState Midgame() => TestBoards.Parse(
            "RR..B..",
            "R.R.BB.",
            "..R..B.",
            "X..B...",
            "..R..R.",
            "B..B.R.",
            ".B....R");

        // Plays a short self-play game and records every chosen move and score-bearing stat.
        private static List<(Move move, long nodes)> PlayGame(int depth, bool training) {
            var red = TestBoards.NewEngine(depth: depth, training: training);
            var blue = TestBoards.NewEngine(depth: depth, training: training);
            var board = Midgame();
            var side = PlayerColor.Red;
            var log = new List<(Move, long)>();
            for (int ply = 0; ply < 14; ply++) {
                var e = side == PlayerColor.Red ? red : blue;
                e.AIPlayerColor = side;
                TestBoards.SetBoard(e, board.Clone(), side);
                var m = e.GetBestMove(side);
                if (m.Equals(default(Move))) break;
                log.Add((m, e.LastSearchStats.Nodes + e.LastSearchStats.QNodes));
                e.MakeMove(board, m, side);
                side = AtaxxAIEngine.SwitchPlayer(side);
                if (red.IsGameOver(board)) break;
            }
            return log;
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ReusedHelper_PlaysIdentically_ToFreshEngines(bool training) {
            // Engines are reused across many moves inside PlayGame (helper reuse); a second independent run
            // with brand-new engines must match move for move and node for node.
            var a = PlayGame(3, training);
            var b = PlayGame(3, training);
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++) {
                Assert.Equal(a[i].move, b[i].move);
                Assert.Equal(a[i].nodes, b[i].nodes);
            }
        }

        [Fact]
        public void SameEngine_RepeatedSearch_GivesSameResultAndNodes() {
            var e = TestBoards.NewEngine(depth: 3);
            TestBoards.SetBoard(e, Midgame(), PlayerColor.Red);
            e.AIPlayerColor = PlayerColor.Red;
            var first = e.GetBestMove(PlayerColor.Red);
            long n1 = e.LastSearchStats.Nodes + e.LastSearchStats.QNodes;
            for (int i = 0; i < 5; i++) {
                var again = e.GetBestMove(PlayerColor.Red);
                Assert.Equal(first, again);
                Assert.Equal(n1, e.LastSearchStats.Nodes + e.LastSearchStats.QNodes);
            }
        }

        [Fact]
        public void IterativeDeepeningInTraining_IsOffByDefault_AndChangesOnlyWhenEnabled() {
            var off = TestBoards.NewEngine(depth: 3);
            TestBoards.SetBoard(off, Midgame(), PlayerColor.Red);
            off.AIPlayerColor = PlayerColor.Red;
            off.GetBestMove(PlayerColor.Red);
            long offNodes = off.LastSearchStats.Nodes;

            var cfg = new AIEngineConfig { AiDepth = 3, TrainingMode = true, DisableParallelRootSearch = true, DisableQuiescenceSearch = true, Seed = 1, IterativeDeepeningInTraining = true };
            var on = new AtaxxAIEngine(new HeuristicEvaluator(), null, null, cfg);
            TestBoards.SetBoard(on, Midgame(), PlayerColor.Red);
            on.AIPlayerColor = PlayerColor.Red;
            on.GetBestMove(PlayerColor.Red);
            Assert.True(on.LastSearchStats.Nodes > offNodes); // searches depth 1 and 2 as well
            Assert.Equal(3, on.LastCompletedDepth);
        }
    }
}
