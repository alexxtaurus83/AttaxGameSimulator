using System;
using System.Linq;
using Attax.Core;
using Attax.Play;
using Xunit;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core.Tests {
    public class StartBoardTests {
        private static AtaxxAIEngine Referee() => new AtaxxAIEngine(Prm.Base());

        [Fact]
        public void ParseRange_AcceptsNAndMinMax_AndRejectsBadInput() {
            Assert.Equal((0, 0), StartBoard.ParseRange(null));
            Assert.Equal((0, 0), StartBoard.ParseRange("0,0"));
            Assert.Equal((4, 4), StartBoard.ParseRange("4"));
            Assert.Equal((0, 6), StartBoard.ParseRange("0,6"));
            Assert.Equal((2, 5), StartBoard.ParseRange(" 2 , 5 "));
            foreach (var bad in new[] { "6,0", "a", "1,2,3", "-1,3", "0,13", "1.5", "," })
                Assert.Throws<FormatException>(() => StartBoard.ParseRange(bad));
        }

        [Fact]
        public void NoBlocks_IsTheStandardStart_AndDoesNotConsumeTheRng() {
            var rng = new Random(5); int expected = new Random(5).Next();
            var b = StartBoard.Create(Referee(), 0, 0, rng);
            Assert.Equal(0UL, b.BlockedSquares);
            Assert.Equal(StartBoard.Standard().RedPieces, b.RedPieces);
            Assert.Equal(expected, rng.Next());     // untouched: existing seeds keep producing the same games
        }

        [Theory]
        [InlineData(1)] [InlineData(3)] [InlineData(6)]
        public void ExactCount_BlocksOnlyEmptySquares_AndNeverTheStartPieces(int n) {
            for (int seed = 0; seed < 200; seed++) {
                var b = StartBoard.Create(Referee(), n, n, new Random(seed));
                Assert.Equal(n, PopCount(b.BlockedSquares));
                Assert.Equal(0UL, b.BlockedSquares & (b.RedPieces | b.BluePieces));
                Assert.Equal(StartBoard.Standard().RedPieces, b.RedPieces); Assert.Equal(StartBoard.Standard().BluePieces, b.BluePieces);
            }
        }

        [Fact]
        public void Range_CoversEveryCount_AndIsDeterministicPerSeed() {
            var counts = Enumerable.Range(0, 400).Select(s => PopCount(StartBoard.Create(Referee(), 0, 6, new Random(s)).BlockedSquares)).ToList();
            for (int k = 0; k <= 6; k++) Assert.Contains(k, counts);
            Assert.DoesNotContain(counts, c => c > 6);
            Assert.Equal(StartBoard.Create(Referee(), 0, 6, new Random(77)).BlockedSquares, StartBoard.Create(Referee(), 0, 6, new Random(77)).BlockedSquares);
        }

        [Fact]
        public void BlockedBoards_PlayCleanly_WithTheShippedEngine() {
            // Whole games with blocks: legal moves only, terminates, blocked squares never change owner.
            var p = Prm.Base();
            for (int seed = 0; seed < 6; seed++) {
                var rng = new Random(seed);
                var referee = Referee();
                var board = StartBoard.Create(referee, 3, 6, rng);
                ulong blocks = board.BlockedSquares;
                var e = new AtaxxAIEngine(p, new AIEngineConfig { AiDepth = 2, DisableParallelRootSearch = true, Seed = seed });
                var side = PlayerColor.Red;
                for (int ply = 0; ply < 200 && !referee.IsGameOver(board); ply++) {
                    var legal = referee.GetAllValidMoves(board, side);
                    if (legal.Count == 0) break;
                    TestBoards.SetBoard(e, board.Clone(), side); e.AIPlayerColor = side;
                    var m = e.GetBestMove(side);
                    Assert.Contains(m, legal);
                    referee.MakeMove(board, m, side);
                    Assert.Equal(blocks, board.BlockedSquares);
                    side = SwitchPlayer(side);
                }
            }
        }
    }
}