using System;
using System.Collections.Generic;
using System.Linq;
using Attax.Core;
using Attax.Model;
using Xunit;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core.Tests {
    public class ActionCodecTests {
        private static BitboardState RandomBoard(Random rng, int pieces, int blocks) {
            var b = new BitboardState();
            var squares = Enumerable.Range(0, 49).OrderBy(_ => rng.Next()).ToList();
            int i = 0;
            for (int k = 0; k < blocks; k++) b.BlockedSquares |= 1UL << squares[i++];
            for (int k = 0; k < pieces; k++) {
                if (rng.Next(2) == 0) b.RedPieces |= 1UL << squares[i++];
                else b.BluePieces |= 1UL << squares[i++];
            }
            return b;
        }

        private static BitboardState Transform(BitboardState b, int sym) {
            ulong r = b.RedPieces, bl = b.BluePieces, x = b.BlockedSquares;
            BitboardOps.TransformState(ref r, ref bl, ref x, sym);
            return new BitboardState { RedPieces = r, BluePieces = bl, BlockedSquares = x };
        }

        [Fact]
        public void Constants_AreAsDocumented() {
            Assert.Equal(833, ActionCodec.ActionCount);
            Assert.Equal(17, ActionCodec.Kinds);
        }

        [Fact]
        public void TransformSquare_MatchesApplySymmetry() {
            for (int s = 0; s < 8; s++)
                for (int sq = 0; sq < 49; sq++)
                    Assert.Equal(BitboardOps.ApplySymmetry(1UL << sq, s), 1UL << ActionCodec.TransformSquare(sq, s));
        }

        [Fact]
        public void EveryEngineMove_RoundTripsThroughCodec() {
            var e = TestBoards.NewEngine();
            var rng = new Random(11);
            for (int n = 0; n < 300; n++) {
                var b = RandomBoard(rng, rng.Next(2, 30), rng.Next(0, 6));
                foreach (var side in new[] { PlayerColor.Red, PlayerColor.Blue }) {
                    foreach (var m in e.GetAllValidMoves(b, side)) {
                        int a = ActionCodec.Encode(m);
                        Assert.InRange(a, 0, ActionCodec.ActionCount - 1);
                        Assert.True(ActionCodec.TryDecode(a, b, side, out var back));
                        // Destination must match and the board after the move must be identical.
                        Assert.Equal(m.ToX, back.ToX);
                        Assert.Equal(m.ToY, back.ToY);
                        Assert.Equal(e.IsCloneMove(m), e.IsCloneMove(back));
                        var b1 = b.Clone(); e.MakeMove(b1, m, side);
                        var b2 = b.Clone(); e.MakeMove(b2, back, side);
                        Assert.Equal(b1.RedPieces, b2.RedPieces);
                        Assert.Equal(b1.BluePieces, b2.BluePieces);
                    }
                }
            }
        }

        [Fact]
        public void DecodedMove_EqualsEngineMove_ForCanonicalSources() {
            // The engine generator reports one clone per destination, from the lowest-index source; decode must agree.
            var e = TestBoards.NewEngine();
            var rng = new Random(12);
            for (int n = 0; n < 200; n++) {
                var b = RandomBoard(rng, rng.Next(2, 30), rng.Next(0, 6));
                foreach (var m in e.GetAllValidMoves(b, PlayerColor.Red)) {
                    Assert.True(ActionCodec.TryDecode(ActionCodec.Encode(m), b, PlayerColor.Red, out var back));
                    Assert.True(m.Equals(back), $"engine {m.FromX},{m.FromY}->{m.ToX},{m.ToY} vs decoded {back.FromX},{back.FromY}->{back.ToX},{back.ToY}");
                }
            }
        }

        [Fact]
        public void LegalMask_EqualsEngineMoveSet() {
            var e = TestBoards.NewEngine();
            var rng = new Random(13);
            var mask = new bool[ActionCodec.ActionCount];
            for (int n = 0; n < 400; n++) {
                var b = RandomBoard(rng, rng.Next(1, 40), rng.Next(0, 8));
                foreach (var side in new[] { PlayerColor.Red, PlayerColor.Blue }) {
                    int count = ActionCodec.FillLegalMask(b, side, mask);
                    var expected = new HashSet<int>(e.GetAllValidMoves(b, side).Select(ActionCodec.Encode));
                    var actual = new HashSet<int>(Enumerable.Range(0, ActionCodec.ActionCount).Where(i => mask[i]));
                    Assert.Equal(expected.Count, count);
                    Assert.True(expected.SetEquals(actual));
                }
            }
        }

        [Fact]
        public void LegalMask_EmptyWhenSideHasNoMoves() {
            var b = TestBoards.Parse(
                "RRXX...",
                "RRXX...",
                "XXXX...",
                "XXXX...",
                ".......",
                ".......",
                "......B");
            var mask = new bool[ActionCodec.ActionCount];
            Assert.Equal(0, ActionCodec.FillLegalMask(b, PlayerColor.Red, mask));
            Assert.True(ActionCodec.FillLegalMask(b, PlayerColor.Blue, mask) > 0);
        }

        [Fact]
        public void IllegalActions_AreRejected() {
            var b = TestBoards.Parse("R.....B", ".......", ".......", ".......", ".......", ".......", "B.....R");
            Assert.False(ActionCodec.TryDecode(-1, b, PlayerColor.Red, out _));
            Assert.False(ActionCodec.TryDecode(ActionCodec.ActionCount, b, PlayerColor.Red, out _));
            // Clone onto an occupied square (Blue piece at index 6).
            Assert.False(ActionCodec.TryDecode(6, b, PlayerColor.Red, out _));
            // Clone with no adjacent friendly piece.
            Assert.False(ActionCodec.TryDecode(24, b, PlayerColor.Red, out _));
            // Every jump whose source is off the board is illegal.
            for (int a = 49; a < ActionCodec.ActionCount; a++) {
                if (ActionCodec.JumpSourceOf(a) < 0) Assert.False(ActionCodec.TryDecode(a, b, PlayerColor.Red, out _));
            }
        }

        [Fact]
        public void EncodeRejectsNonMoves() {
            Assert.Throws<ArgumentException>(() => ActionCodec.Encode(0, 3));
            Assert.Throws<ArgumentException>(() => ActionCodec.Encode(0, 0));
        }

        [Fact]
        public void SymmetryPermutations_AreBijections_AndInvertible() {
            for (int s = 0; s < 8; s++) {
                var perm = ActionCodec.GetPermutation(s);
                Assert.Equal(ActionCodec.ActionCount, perm.Distinct().Count());
                int inv = ActionCodec.InverseSymmetry(s);
                for (int a = 0; a < ActionCodec.ActionCount; a++) {
                    Assert.Equal(a, ActionCodec.TransformAction(ActionCodec.TransformAction(a, s), inv));
                }
            }
        }

        [Fact]
        public void Identity_IsIdentity() {
            for (int a = 0; a < ActionCodec.ActionCount; a++) Assert.Equal(a, ActionCodec.TransformAction(a, 0));
        }

        [Fact]
        public void Symmetry_CommutesWithLegalityAndMoves() {
            var e = TestBoards.NewEngine();
            var rng = new Random(14);
            var mask = new bool[ActionCodec.ActionCount];
            var maskT = new bool[ActionCodec.ActionCount];
            for (int n = 0; n < 120; n++) {
                var b = RandomBoard(rng, rng.Next(2, 35), rng.Next(0, 7));
                for (int s = 0; s < 8; s++) {
                    var bt = Transform(b, s);
                    foreach (var side in new[] { PlayerColor.Red, PlayerColor.Blue }) {
                        ActionCodec.FillLegalMask(b, side, mask);
                        ActionCodec.FillLegalMask(bt, side, maskT);
                        for (int a = 0; a < ActionCodec.ActionCount; a++) {
                            Assert.Equal(mask[a], maskT[ActionCodec.TransformAction(a, s)]);
                        }
                    }
                }

                // Playing action a then transforming the board equals transforming both first.
                var moves = e.GetAllValidMoves(b, PlayerColor.Red);
                foreach (var m in moves.Take(6)) {
                    int a = ActionCodec.Encode(m);
                    var played = b.Clone(); e.MakeMove(played, m, PlayerColor.Red);
                    for (int s = 0; s < 8; s++) {
                        var bt = Transform(b, s);
                        Assert.True(ActionCodec.TryDecode(ActionCodec.TransformAction(a, s), bt, PlayerColor.Red, out var mt));
                        var playedT = bt.Clone(); e.MakeMove(playedT, mt, PlayerColor.Red);
                        var expected = Transform(played, s);
                        Assert.Equal(expected.RedPieces, playedT.RedPieces);
                        Assert.Equal(expected.BluePieces, playedT.BluePieces);
                    }
                }
            }
        }

        [Fact]
        public void SymmetryGroup_ComposesConsistently() {
            // TransformAction(., s) must be the permutation induced by squares, for every pair of symmetries.
            for (int s = 0; s < 8; s++) {
                for (int t = 0; t < 8; t++) {
                    // Find u with u = apply s then t on squares.
                    int u = -1;
                    for (int c = 0; c < 8 && u < 0; c++) {
                        bool ok = true;
                        for (int sq = 0; sq < 49 && ok; sq++)
                            ok = ActionCodec.TransformSquare(ActionCodec.TransformSquare(sq, s), t) == ActionCodec.TransformSquare(sq, c);
                        if (ok) u = c;
                    }
                    Assert.True(u >= 0);
                    for (int a = 0; a < ActionCodec.ActionCount; a++) {
                        Assert.Equal(ActionCodec.TransformAction(a, u), ActionCodec.TransformAction(ActionCodec.TransformAction(a, s), t));
                    }
                }
            }
        }
    }
}
