using System;
using System.Collections.Generic;
using System.Linq;
using Attax.Core;
using Xunit;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core.Tests {
    public class SearchSafetyTests {
        private static BitboardState Midgame() => TestBoards.Parse(
            "RR..B..",
            "R.R.BB.",
            "..R..B.",
            "X..B...",
            "..R..R.",
            "B..B.R.",
            ".B....R");

        [Fact]
        public void ParallelRoot_ManyRepeatedSearches_NoCorruptionAndSameMoveAsSerial() {
            var serial = TestBoards.NewEngine(depth: 3, parallel: false);
            TestBoards.SetBoard(serial, Midgame(), PlayerColor.Red);
            serial.AIPlayerColor = PlayerColor.Red;
            var expected = serial.GetBestMove(PlayerColor.Red);

            var par = TestBoards.NewEngine(depth: 3, parallel: true);
            TestBoards.SetBoard(par, Midgame(), PlayerColor.Red);
            par.AIPlayerColor = PlayerColor.Red;
            for (int i = 0; i < 25; i++) {
                var m = par.GetBestMove(PlayerColor.Red);
                Assert.Equal(expected, m);
            }
        }

        [Fact]
        public void LeaseOwnership_NoHelperSharedByTwoLiveWorkers() {
            // Many concurrent independent engines each running parallel root search; any shared helper
            // would surface as a wrong board, exception, or differing move.
            var results = new Move[8];
            System.Threading.Tasks.Parallel.For(0, 8, i => {
                var e = TestBoards.NewEngine(depth: 3, parallel: true);
                TestBoards.SetBoard(e, Midgame(), PlayerColor.Red);
                e.AIPlayerColor = PlayerColor.Red;
                results[i] = e.GetBestMove(PlayerColor.Red);
            });
            Assert.All(results, m => Assert.Equal(results[0], m));
        }

        [Fact]
        public void TerminalScore_FromTransposition_KeepsShortestWin() {
            // Red can eliminate Blue at once. With a transposition-heavy deeper search the engine must
            // still pick an immediately winning move rather than a slower one.
            var b = TestBoards.Parse(
                "B......", ".......", ".......", ".......", "...RR..", "...RR..", ".......");
            // Blue's only piece is in the corner, far from Red: no immediate win. Use a near case instead.
            var near = TestBoards.Parse(
                "BR.....", ".......", ".......", ".......", ".......", "......R", ".......");
            var e = TestBoards.NewEngine(depth: 4, training: false);
            TestBoards.SetBoard(e, near, PlayerColor.Red);
            e.AIPlayerColor = PlayerColor.Red;
            var move = e.GetBestMove(PlayerColor.Red);
            var after = near.Clone();
            e.MakeMove(after, move, PlayerColor.Red);
            Assert.Equal(0, AtaxxAIEngine.PopCount(after.BluePieces));
        }

        [Fact]
        public void DeepSearch_DoesNotOverflowPerPlyBuffers() {
            var e = TestBoards.NewEngine(depth: 6, maxNodes: 200_000, training: false);
            TestBoards.SetBoard(e, Midgame(), PlayerColor.Red);
            e.AIPlayerColor = PlayerColor.Red;
            var move = e.GetBestMove(PlayerColor.Red);
            Assert.False(move.Equals(default(Move)));
        }

        [Fact]
        public void FillMoves_NeverExceedsBuffer_OnSparseBoards() {
            // Upper bound for one side: 49 clone destinations + 16 jump targets per piece capped by empties.
            // A lone piece in the centre of an otherwise empty board is the worst sparse case per piece.
            var e = TestBoards.NewEngine();
            var rng = new Random(7);
            int max = 0;
            for (int t = 0; t < 2000; t++) {
                var b = new BitboardState();
                int pieces = rng.Next(1, 12);
                for (int i = 0; i < pieces; i++) b.RedPieces |= 1UL << rng.Next(49);
                for (int i = 0; i < rng.Next(0, 6); i++) b.BluePieces |= 1UL << rng.Next(49);
                b.BluePieces &= ~b.RedPieces;
                max = Math.Max(max, e.GetAllValidMoves(b, PlayerColor.Red).Count);
            }
            Assert.True(max < AtaxxThreadHelper.MaxMovesBuffer, $"max moves {max} vs buffer {AtaxxThreadHelper.MaxMovesBuffer}");
        }
    }
}
