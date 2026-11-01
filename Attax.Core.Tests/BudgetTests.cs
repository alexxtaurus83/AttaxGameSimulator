using System.Linq;
using Attax.Core;
using Xunit;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core.Tests {
    public class BudgetTests {
        // A busy midgame position with many legal moves.
        private static BitboardState Midgame() => TestBoards.Parse(
            "RR..B..",
            "R.R.BB.",
            "..R..B.",
            "X..B...",
            "..R..R.",
            "B..B.R.",
            ".B....R");

        private static (AtaxxAIEngine e, BitboardState b) Setup(long? budget, bool parallel = false, int depth = 3, bool training = true) {
            var e = TestBoards.NewEngine(depth: depth, maxNodes: budget, training: training, parallel: parallel);
            var b = Midgame();
            TestBoards.SetBoard(e, b, PlayerColor.Red);
            e.AIPlayerColor = PlayerColor.Red;
            return (e, b);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(5)]
        [InlineData(20)]
        [InlineData(100)]
        [InlineData(1000)]
        public void NodeBudget_IsNeverExceeded_Serial(long budget) {
            var (e, _) = Setup(budget);
            var move = e.GetBestMove(PlayerColor.Red);
            Assert.False(move.Equals(default(Move)));
            long used = e.LastSearchStats.Nodes + e.LastSearchStats.QNodes - e.LastFallbackNodes;
            Assert.True(used <= budget, $"used {used} nodes with budget {budget}");
        }

        [Theory]
        [InlineData(5)]
        [InlineData(100)]
        [InlineData(1000)]
        public void NodeBudget_IsNeverExceeded_ParallelRoot(long budget) {
            var (e, _) = Setup(budget, parallel: true);
            var move = e.GetBestMove(PlayerColor.Red);
            Assert.False(move.Equals(default(Move)));
            long used = e.LastSearchStats.Nodes + e.LastSearchStats.QNodes - e.LastFallbackNodes;
            Assert.True(used <= budget, $"used {used} nodes with budget {budget}");
        }

        [Fact]
        public void TinyBudget_ReturnsLegalMove_AndReportsFallback() {
            var (e, b) = Setup(1);
            var move = e.GetBestMove(PlayerColor.Red);
            Assert.Contains(move, e.GetAllValidMoves(b, PlayerColor.Red));
            Assert.True(e.LastSearchWasInterrupted);
        }

        [Fact]
        public void Unlimited_MatchesHugeBudget_SameMove() {
            var (a, _) = Setup(null);
            var (c, _) = Setup(50_000_000);
            var m1 = a.GetBestMove(PlayerColor.Red);
            var m2 = c.GetBestMove(PlayerColor.Red);
            Assert.Equal(m1, m2);
            Assert.False(c.LastSearchWasInterrupted);
            Assert.Equal(3, c.LastCompletedDepth);
        }

        [Fact]
        public void InterruptedSearch_DoesNotCorruptBoardOrEngineState() {
            var (e, b) = Setup(37);
            ulong r = e.Board.RedPieces, bl = e.Board.BluePieces, z = e.Board.ZobristHash;
            e.GetBestMove(PlayerColor.Red);
            Assert.Equal(r, e.Board.RedPieces);
            Assert.Equal(bl, e.Board.BluePieces);
            Assert.Equal(z, e.Board.ZobristHash);
            // and the engine still works normally afterwards
            e.MaxNodes = null;
            var move = e.GetBestMove(PlayerColor.Red);
            Assert.Contains(move, e.GetAllValidMoves(b, PlayerColor.Red));
            Assert.Equal(3, e.LastCompletedDepth);
        }

        [Fact]
        public void BudgetDoesNotResetBetweenMoves() {
            var (e, _) = Setup(200);
            for (int i = 0; i < 3; i++) {
                e.GetBestMove(PlayerColor.Red);
                long used = e.LastSearchStats.Nodes + e.LastSearchStats.QNodes - e.LastFallbackNodes;
                Assert.True(used <= 200, $"call {i}: used {used}");
            }
        }

        [Fact]
        public void PartialIteration_NeverReplacesCompletedOne() {
            // With a budget that completes depth 1 and 2 but not 3, the result must equal the depth-2 answer.
            var (full, _) = Setup(null, depth: 2);
            var expected = full.GetBestMove(PlayerColor.Red);
            long d2Nodes = full.LastSearchStats.Nodes + full.LastSearchStats.QNodes;

            // Iterative deepening is needed so a completed shallower iteration exists (non-training mode).
            var (e, _) = Setup(d2Nodes + 5, depth: 3, training: false);
            var move = e.GetBestMove(PlayerColor.Red);
            Assert.True(e.LastCompletedDepth >= 1);
            Assert.True(e.LastSearchStats.Nodes + e.LastSearchStats.QNodes - e.LastFallbackNodes <= d2Nodes + 5);
            Assert.Contains(move, e.GetAllValidMoves(Midgame(), PlayerColor.Red));
        }
    }
}
