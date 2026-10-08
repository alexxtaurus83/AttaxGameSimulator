using System;
using System.Collections.Generic;
using System.Linq;
using Attax.Core;
using Xunit;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core.Tests {
    // Tests for the client-side super jump ability: GetSuperJumpMovesFrom, IsLegalMove, IsSuperJump.
    // The engine's move application (MakeMove / MakeMoveFast / UnmakeMove) must handle distance >= 3 as an ordinary jump.
    public class SuperJumpTests {
        private static AtaxxAIEngine Engine() => TestBoards.NewEngine();

        private static int Cheb(Move m) => Math.Max(Math.Abs(m.ToX - m.FromX), Math.Abs(m.ToY - m.FromY));

        // Only the super jump part of GetValidMovesFrom(..., superJump: true).
        private static List<Move> SuperJumpOnly(AtaxxAIEngine e, BitboardState b, PlayerColor p, int x, int y) =>
            e.GetValidMovesFrom(b, p, x, y, true).Where(IsSuperJump).ToList();

        private static ulong Bit(int x, int y) => 1UL << (y * 7 + x);

        private static BitboardState WithHash(AtaxxAIEngine e, BitboardState b, PlayerColor sideToMove) {
            b.ZobristHash = e.ComputeZobristHash(b, sideToMove);
            return b;
        }

        [Fact]
        public void FromCorner_ReturnsEveryEmptyCellAtDistanceThreeOrMore() {
            var e = Engine();
            var b = TestBoards.Parse(
                "R......",
                ".......",
                ".......",
                ".......",
                ".......",
                ".......",
                ".......");
            var moves = SuperJumpOnly(e, b, PlayerColor.Red, 0, 0);

            Assert.Equal(40, moves.Count);
            Assert.All(moves, m => {
                Assert.Equal(0, m.FromX);
                Assert.Equal(0, m.FromY);
                Assert.True(Cheb(m) >= 3);
                Assert.True(IsSuperJump(m));
            });
            Assert.Equal(moves.Count, moves.Distinct().Count());
            for (int y = 0; y < 7; y++)
                for (int x = 0; x < 7; x++) {
                    bool expected = Math.Max(x, y) >= 3;
                    Assert.Equal(expected, moves.Contains(new Move(0, 0, x, y)));
                }
        }

        [Fact]
        public void ExcludesEnemyBlockedAndOwnChips() {
            var e = Engine();
            var b = TestBoards.Parse(
                "R..B...",
                "...X...",
                ".......",
                "R..R.B.",
                ".......",
                ".......",
                ".......");
            var moves = SuperJumpOnly(e, b, PlayerColor.Red, 0, 0);

            Assert.DoesNotContain(new Move(0, 0, 3, 0), moves); // enemy
            Assert.DoesNotContain(new Move(0, 0, 5, 3), moves); // enemy
            Assert.DoesNotContain(new Move(0, 0, 3, 1), moves); // blocked
            Assert.DoesNotContain(new Move(0, 0, 0, 3), moves); // own chip
            Assert.DoesNotContain(new Move(0, 0, 3, 3), moves); // own chip
            Assert.Contains(new Move(0, 0, 4, 0), moves);
            Assert.Contains(new Move(0, 0, 6, 6), moves);
            Assert.All(moves, m => Assert.NotEqual(0UL, b.EmptySquares() & Bit(m.ToX, m.ToY)));
        }

        [Fact]
        public void EmptyForEnemySourceEmptySourceAndOutOfBounds() {
            var e = Engine();
            var b = TestBoards.Parse(
                "R......",
                ".......",
                ".......",
                ".......",
                "......B",
                ".......",
                ".......");
            Assert.Empty(SuperJumpOnly(e, b, PlayerColor.Red, 6, 4));  // enemy-owned
            Assert.Empty(SuperJumpOnly(e, b, PlayerColor.Blue, 0, 0)); // enemy-owned
            Assert.Empty(SuperJumpOnly(e, b, PlayerColor.Red, 3, 3));  // empty
            Assert.Empty(SuperJumpOnly(e, b, PlayerColor.Red, -1, 0));
            Assert.Empty(SuperJumpOnly(e, b, PlayerColor.Red, 0, 7));
            Assert.Empty(SuperJumpOnly(e, b, PlayerColor.Red, 7, 7));
        }

        [Fact]
        public void SuperJumpFlag_ExtendsDefaultResultToAllEmptyCellsWithoutOverlap() {
            var e = Engine();
            var b = TestBoards.Parse(
                "RR..B..",
                "R.R.BB.",
                "..R..B.",
                "X..B...",
                "..R..R.",
                "B..B.R.",
                ".B....R");
            foreach (var (x, y) in new[] { (0, 0), (2, 2), (5, 4), (6, 6) }) {
                var normal = e.GetValidMovesFrom(b, PlayerColor.Red, x, y);
                var normalExplicit = e.GetValidMovesFrom(b, PlayerColor.Red, x, y, false);
                var all = e.GetValidMovesFrom(b, PlayerColor.Red, x, y, true);

                // Default and superJump = false are identical, and no standard move is a super jump.
                Assert.Equal(normal, normalExplicit);
                Assert.DoesNotContain(normal, IsSuperJump);
                // superJump = true keeps the standard moves as a prefix and appends only super jumps.
                Assert.Equal(normal, all.Take(normal.Count).ToList());
                Assert.All(all.Skip(normal.Count), m => Assert.True(IsSuperJump(m)));

                ulong union = 0;
                foreach (var m in all) union |= Bit(m.ToX, m.ToY);
                Assert.Equal(b.EmptySquares() & ~Bit(x, y), union);
                Assert.Equal(all.Count, System.Numerics.BitOperations.PopCount(union)); // no duplicates
            }
        }

        [Fact]
        public void IsLegalMove_DistanceRules() {
            var e = Engine();
            var b = TestBoards.Parse(
                "R......",
                ".......",
                ".......",
                ".......",
                ".......",
                ".......",
                ".......");
            var clone = new Move(0, 0, 1, 1);
            var jump = new Move(0, 0, 2, 2);
            var far = new Move(0, 0, 3, 0);
            var farthest = new Move(0, 0, 6, 6);

            Assert.True(e.IsLegalMove(b, PlayerColor.Red, clone, false));
            Assert.True(e.IsLegalMove(b, PlayerColor.Red, jump, false));
            Assert.False(e.IsLegalMove(b, PlayerColor.Red, far, false));
            Assert.False(e.IsLegalMove(b, PlayerColor.Red, farthest, false));

            Assert.True(e.IsLegalMove(b, PlayerColor.Red, clone, true));
            Assert.True(e.IsLegalMove(b, PlayerColor.Red, jump, true));
            Assert.True(e.IsLegalMove(b, PlayerColor.Red, far, true));
            Assert.True(e.IsLegalMove(b, PlayerColor.Red, farthest, true));
        }

        [Fact]
        public void IsLegalMove_RejectsInvalidMovesEvenWithSuperJump() {
            var e = Engine();
            var b = TestBoards.Parse(
                "R..B...",
                ".X.....",
                "R......",
                ".......",
                "......B",
                ".......",
                ".......");

            Assert.False(e.IsLegalMove(b, PlayerColor.Red, new Move(0, 0, 3, 0), true));  // enemy destination, super jump
            Assert.False(e.IsLegalMove(b, PlayerColor.Red, new Move(0, 0, 0, 2), true));  // own chip destination
            Assert.False(e.IsLegalMove(b, PlayerColor.Red, new Move(0, 0, 1, 1), true));  // blocked destination
            Assert.False(e.IsLegalMove(b, PlayerColor.Red, new Move(0, 0, 6, 4), true));  // enemy destination, far
            Assert.False(e.IsLegalMove(b, PlayerColor.Red, new Move(0, 0, 0, 0), true));  // same cell
            Assert.False(e.IsLegalMove(b, PlayerColor.Blue, new Move(0, 0, 4, 4), true)); // wrong-owner source
            Assert.False(e.IsLegalMove(b, PlayerColor.Red, new Move(4, 4, 5, 5), true));  // empty source
            Assert.False(e.IsLegalMove(b, PlayerColor.Red, new Move(0, 0, 7, 0), true));  // out of bounds destination
            Assert.False(e.IsLegalMove(b, PlayerColor.Red, new Move(-1, 0, 2, 0), true)); // out of bounds source
            Assert.False(e.IsLegalMove(b, PlayerColor.Red, new Move(0, 0, 0, -3), true));
        }

        [Fact]
        public void IsSuperJump_MatchesChebyshevDistance() {
            Assert.False(IsSuperJump(new Move(3, 3, 4, 4)));
            Assert.False(IsSuperJump(new Move(3, 3, 5, 5)));
            Assert.False(IsSuperJump(new Move(0, 0, 2, 1)));
            Assert.True(IsSuperJump(new Move(3, 3, 0, 3)));
            Assert.True(IsSuperJump(new Move(0, 0, 3, 1)));
            Assert.True(IsSuperJump(new Move(0, 6, 6, 0)));
        }

        [Fact]
        public void MakeMove_SuperJump_MovesChipFlipsNeighboursAndKeepsHashConsistent() {
            var e = Engine();
            var b = WithHash(e, TestBoards.Parse(
                "R......",
                ".......",
                ".......",
                "....B..",
                "...BB..",
                ".......",
                "......."), PlayerColor.Red);
            var move = new Move(0, 0, 3, 3);
            Assert.True(e.IsLegalMove(b, PlayerColor.Red, move, true));

            var result = e.MakeMove(b, move, PlayerColor.Red);

            // Source emptied, destination set.
            Assert.Equal(0UL, b.RedPieces & Bit(0, 0));
            Assert.NotEqual(0UL, b.RedPieces & Bit(3, 3));
            // Adjacent enemies (4,3), (3,4), (4,4) flipped.
            Assert.Equal(Bit(3, 3) | Bit(4, 3) | Bit(3, 4) | Bit(4, 4), b.RedPieces);
            Assert.Equal(0UL, b.BluePieces);
            Assert.Equal(3, result.FlippedPieces.Count);
            Assert.Equal(0UL, b.RedPieces & b.BluePieces);
            // Incremental hash equals a full recompute (side to move is now Blue).
            Assert.Equal(e.ComputeZobristHash(b, PlayerColor.Blue), b.ZobristHash);
        }

        [Fact]
        public void MakeMove_SuperJump_WithoutFlipsKeepsHashConsistent() {
            var e = Engine();
            var b = WithHash(e, TestBoards.Parse(
                ".......",
                ".......",
                ".......",
                "...X...",
                ".......",
                ".......",
                "B.....R"), PlayerColor.Red);
            var move = new Move(6, 6, 0, 0);

            e.MakeMove(b, move, PlayerColor.Red);

            Assert.Equal(Bit(0, 0), b.RedPieces);
            Assert.Equal(e.ComputeZobristHash(b, PlayerColor.Blue), b.ZobristHash);
        }

        [Fact]
        public void MakeMoveFast_ThenUnmakeMove_RestoresBoardAndHashExactly() {
            var e = Engine();
            var original = WithHash(e, TestBoards.Parse(
                "R.....B",
                ".......",
                "..X....",
                "....B..",
                "...BB..",
                "B......",
                "......R"), PlayerColor.Red);

            foreach (var move in new[] { new Move(0, 0, 3, 3), new Move(0, 0, 6, 3), new Move(6, 6, 2, 3), new Move(0, 0, 0, 3) }) {
                var b = original.Clone();
                Assert.True(e.IsLegalMove(b, PlayerColor.Red, move, true));

                var undo = e.MakeMoveFast(b, move, PlayerColor.Red);

                Assert.Equal(0UL, b.RedPieces & b.BluePieces);
                Assert.Equal(e.ComputeZobristHash(b, PlayerColor.Blue), b.ZobristHash);

                e.UnmakeMove(b, move, PlayerColor.Red, undo);

                Assert.Equal(original.RedPieces, b.RedPieces);
                Assert.Equal(original.BluePieces, b.BluePieces);
                Assert.Equal(original.BlockedSquares, b.BlockedSquares);
                Assert.Equal(original.ZobristHash, b.ZobristHash);
            }
        }

        [Fact]
        public void SuperJump_NeverOverlapsDistanceOneOrTwoMoves() {
            var e = Engine();
            var b = TestBoards.Parse(
                ".......",
                ".......",
                ".......",
                "...R...",
                ".......",
                ".......",
                ".......");
            var super = SuperJumpOnly(e, b, PlayerColor.Red, 3, 3);

            // From the centre every cell is within distance 3, but nothing closer than 3 may be offered.
            Assert.All(super, m => Assert.True(Cheb(m) >= 3));
            Assert.DoesNotContain(super, m => e.IsCloneMove(m));
            Assert.Equal(7 * 7 - 25, super.Count);
        }

        // ---- Convert-enemy-chip cheat -------------------------------------------------------------------------------------------

        [Fact]
        public void Convert_ChangesOnlyTheTargetOwnerAndKeepsHashConsistent() {
            var e = Engine();
            var b = WithHash(e, TestBoards.Parse(
                "R......",
                ".......",
                "..BB...",
                "..BB...",
                ".......",
                ".......",
                "......R"), PlayerColor.Red);
            int before = System.Numerics.BitOperations.PopCount(b.RedPieces | b.BluePieces);

            Assert.True(e.TryConvertEnemyChip(b, PlayerColor.Red, 2, 2));

            Assert.NotEqual(0UL, b.RedPieces & Bit(2, 2));
            Assert.Equal(0UL, b.BluePieces & Bit(2, 2));
            // Neighbours (3,2), (2,3), (3,3) stay blue: no automatic capture.
            Assert.Equal(Bit(3, 2) | Bit(2, 3) | Bit(3, 3), b.BluePieces);
            Assert.Equal(Bit(0, 0) | Bit(6, 6) | Bit(2, 2), b.RedPieces);
            Assert.Equal(before, System.Numerics.BitOperations.PopCount(b.RedPieces | b.BluePieces));
            Assert.Equal(e.ComputeZobristHash(b, PlayerColor.Red), b.ZobristHash);
        }

        [Fact]
        public void Convert_WorksForBlueToo() {
            var e = Engine();
            var b = WithHash(e, TestBoards.Parse(
                "R......",
                ".......",
                ".......",
                "...B...",
                ".......",
                ".......",
                "......."), PlayerColor.Blue);

            Assert.True(e.TryConvertEnemyChip(b, PlayerColor.Blue, 0, 0));

            Assert.Equal(0UL, b.RedPieces);
            Assert.Equal(Bit(0, 0) | Bit(3, 3), b.BluePieces);
            Assert.Equal(e.ComputeZobristHash(b, PlayerColor.Blue), b.ZobristHash);
        }

        [Fact]
        public void Convert_RejectsInvalidTargetsAndLeavesBoardUntouched() {
            var e = Engine();
            var b = WithHash(e, TestBoards.Parse(
                "R..X...",
                ".......",
                ".......",
                "...B...",
                ".......",
                ".......",
                "......."), PlayerColor.Red);
            ulong red = b.RedPieces, blue = b.BluePieces, blocked = b.BlockedSquares, hash = b.ZobristHash;

            Assert.False(e.TryConvertEnemyChip(b, PlayerColor.Red, 0, 0));  // own chip
            Assert.False(e.TryConvertEnemyChip(b, PlayerColor.Red, 3, 0));  // blocked
            Assert.False(e.TryConvertEnemyChip(b, PlayerColor.Red, 5, 5));  // empty
            Assert.False(e.TryConvertEnemyChip(b, PlayerColor.Red, -1, 0));
            Assert.False(e.TryConvertEnemyChip(b, PlayerColor.Red, 0, 7));
            Assert.False(e.TryConvertEnemyChip(b, PlayerColor.None, 3, 3));

            Assert.Equal(red, b.RedPieces);
            Assert.Equal(blue, b.BluePieces);
            Assert.Equal(blocked, b.BlockedSquares);
            Assert.Equal(hash, b.ZobristHash);
        }

        [Fact]
        public void Convert_LastEnemyChip_EndsTheGame() {
            var e = Engine();
            var b = WithHash(e, TestBoards.Parse(
                "R......",
                ".......",
                ".......",
                "...B...",
                ".......",
                ".......",
                "......."), PlayerColor.Red);
            Assert.False(e.IsGameOver(b));

            Assert.True(e.TryConvertEnemyChip(b, PlayerColor.Red, 3, 3));

            Assert.True(e.IsGameOver(b));
            Assert.Equal(TerminalResult.RedWins, e.GetTerminalResult(b));
        }

        // ---- Block-enemy-chip cheat ---------------------------------------------------------------------------------------------

        [Fact]
        public void Block_TurnsEnemyChipIntoBlockedCellAndKeepsHashConsistent() {
            var e = Engine();
            var b = WithHash(e, TestBoards.Parse(
                "R......",
                ".......",
                "..BB...",
                "..BB...",
                ".......",
                ".......",
                "......R"), PlayerColor.Red);

            Assert.True(e.TryConvertEnemyChip(b, PlayerColor.Red, 2, 2, ChipConversion.ToBlocked));

            Assert.Equal(Bit(2, 2), b.BlockedSquares);
            Assert.Equal(0UL, b.BluePieces & Bit(2, 2));
            Assert.Equal(0UL, b.RedPieces & Bit(2, 2));
            // Neighbours untouched, own chips untouched.
            Assert.Equal(Bit(3, 2) | Bit(2, 3) | Bit(3, 3), b.BluePieces);
            Assert.Equal(Bit(0, 0) | Bit(6, 6), b.RedPieces);
            Assert.Equal(e.ComputeZobristHash(b, PlayerColor.Red), b.ZobristHash);
            Assert.Equal(0UL, b.EmptySquares() & Bit(2, 2));
            // The new blocked cell is no longer a legal destination or a super jump target.
            Assert.False(e.IsLegalMove(b, PlayerColor.Red, new Move(0, 0, 2, 2), true));
        }

        [Fact]
        public void Block_WorksForBlueToo() {
            var e = Engine();
            var b = WithHash(e, TestBoards.Parse(
                "R......",
                ".......",
                ".......",
                "...B...",
                ".......",
                ".......",
                "......."), PlayerColor.Blue);

            Assert.True(e.TryConvertEnemyChip(b, PlayerColor.Blue, 0, 0, ChipConversion.ToBlocked));

            Assert.Equal(0UL, b.RedPieces);
            Assert.Equal(Bit(3, 3), b.BluePieces);
            Assert.Equal(Bit(0, 0), b.BlockedSquares);
            Assert.Equal(e.ComputeZobristHash(b, PlayerColor.Blue), b.ZobristHash);
        }

        [Fact]
        public void Block_RejectsInvalidTargetsAndLeavesBoardUntouched() {
            var e = Engine();
            var b = WithHash(e, TestBoards.Parse(
                "R..X...",
                ".......",
                ".......",
                "...B...",
                ".......",
                ".......",
                "......."), PlayerColor.Red);
            ulong red = b.RedPieces, blue = b.BluePieces, blocked = b.BlockedSquares, hash = b.ZobristHash;

            Assert.False(e.TryConvertEnemyChip(b, PlayerColor.Red, 0, 0, ChipConversion.ToBlocked));  // own chip
            Assert.False(e.TryConvertEnemyChip(b, PlayerColor.Red, 3, 0, ChipConversion.ToBlocked));  // already blocked
            Assert.False(e.TryConvertEnemyChip(b, PlayerColor.Red, 5, 5, ChipConversion.ToBlocked));  // empty
            Assert.False(e.TryConvertEnemyChip(b, PlayerColor.Red, -1, 0, ChipConversion.ToBlocked));
            Assert.False(e.TryConvertEnemyChip(b, PlayerColor.Red, 0, 7, ChipConversion.ToBlocked));
            Assert.False(e.TryConvertEnemyChip(b, PlayerColor.None, 3, 3, ChipConversion.ToBlocked));

            Assert.Equal(red, b.RedPieces);
            Assert.Equal(blue, b.BluePieces);
            Assert.Equal(blocked, b.BlockedSquares);
            Assert.Equal(hash, b.ZobristHash);
        }

        [Fact]
        public void Block_LastEnemyChip_EndsTheGame() {
            var e = Engine();
            var b = WithHash(e, TestBoards.Parse(
                "R......",
                ".......",
                ".......",
                "...B...",
                ".......",
                ".......",
                "......."), PlayerColor.Red);
            Assert.False(e.IsGameOver(b));

            Assert.True(e.TryConvertEnemyChip(b, PlayerColor.Red, 3, 3, ChipConversion.ToBlocked));

            Assert.True(e.IsGameOver(b));
            Assert.Equal(TerminalResult.RedWins, e.GetTerminalResult(b));
        }

        [Fact]
        public void GetConvertibleChips_EqualsTheOpponentBitboard() {
            var e = Engine();
            var b = TestBoards.Parse(
                "RR..B..",
                "R.R.BB.",
                "..R..B.",
                "X..B...",
                "..R..R.",
                "B..B.R.",
                ".B....R");

            foreach (var player in new[] { PlayerColor.Red, PlayerColor.Blue }) {
                ulong enemy = player == PlayerColor.Red ? b.BluePieces : b.RedPieces;
                var cells = e.GetConvertibleChips(b, player);
                ulong mask = 0;
                foreach (var (x, y) in cells) mask |= Bit(x, y);
                Assert.Equal(enemy, mask);
                Assert.Equal(System.Numerics.BitOperations.PopCount(enemy), cells.Count);
            }
        }
    }
}
