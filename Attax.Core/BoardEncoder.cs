using System;
using Attax.Core;
using Attax.Core.Utils;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core {
    /// <summary>
    /// Model input encoding. One board becomes 4 planes of 7x7 in CHW order (index = channel * 49 + y * 7 + x):
    ///   0 friendly (side to move), 1 enemy, 2 blocked, 3 constant 1.0 (lets convolutions see the board edge).
    /// The perspective is always the side to move, so the model never sees a colour.
    /// </summary>
    public static class BoardEncoder {
        public const int Channels = 4;
        public const int Squares = AttaxConstants.BaseConst.BoardSize * AttaxConstants.BaseConst.BoardSize;
        public const int FloatsPerBoard = Channels * Squares;

        public static void Encode(BitboardState board, PlayerColor sideToMove, float[] dest, int offset) {
            if (board == null) throw new ArgumentNullException(nameof(board));
            if (sideToMove != PlayerColor.Red && sideToMove != PlayerColor.Blue)
                throw new ArgumentException("sideToMove must be Red or Blue.", nameof(sideToMove));
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            if (offset < 0 || offset + FloatsPerBoard > dest.Length) throw new ArgumentOutOfRangeException(nameof(offset));

            ulong friendly = sideToMove == PlayerColor.Red ? board.RedPieces : board.BluePieces;
            ulong enemy = sideToMove == PlayerColor.Red ? board.BluePieces : board.RedPieces;
            ulong blocked = board.BlockedSquares;

            for (int sq = 0; sq < Squares; sq++) {
                dest[offset + sq] = (friendly >> sq) & 1UL;
                dest[offset + Squares + sq] = (enemy >> sq) & 1UL;
                dest[offset + 2 * Squares + sq] = (blocked >> sq) & 1UL;
                dest[offset + 3 * Squares + sq] = 1.0f;
            }
        }

        /// <summary>Encodes boards into one [N, 4, 7, 7] buffer (all from the same side-to-move perspective).</summary>
        public static float[] EncodeBatch(System.Collections.Generic.IReadOnlyList<BitboardState> boards, PlayerColor sideToMove) {
            var data = new float[boards.Count * FloatsPerBoard];
            for (int i = 0; i < boards.Count; i++) Encode(boards[i], sideToMove, data, i * FloatsPerBoard);
            return data;
        }
    }
}
