using System;
using System.Collections.Generic;
using Attax.Core;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core {
    /// <summary>
    /// Policy action space shared by the engine, the log format, the trainer and the ONNX contract.
    ///
    /// Layout: <c>action = kind * 49 + toSquare</c> (toSquare = y * 7 + x), 17 kinds, 833 actions.
    ///   kind 0      : clone to toSquare. The source is irrelevant (every source gives the same board), so all
    ///                 clones to one destination are a single action.
    ///   kind 1..16  : jump to toSquare from toSquare + (dx, dy), the 16 offsets at Chebyshev distance 2 in
    ///                 row-major order (dy = -2..2, dx = -2..2). A source that falls off the board is never legal.
    ///
    /// The encoding is expressed relative to the side to move (friendly pieces), so it carries no colour.
    /// A policy head of shape [17, 7, 7] flattens to exactly this index order.
    /// Terminal positions (a side without a legal move) have no actions and are never training samples;
    /// there are no pass actions.
    /// </summary>
    public static class ActionCodec {
        public const int BoardSize = AttaxConstants.BaseConst.BoardSize;
        public const int Squares = BoardSize * BoardSize;
        public const int CloneKind = 0;
        public const int JumpKinds = 16;
        public const int Kinds = JumpKinds + 1;
        public const int ActionCount = Kinds * Squares;
        public const int SymmetryCount = 8;
        public const int NoAction = -1;

        private static readonly int[] OffsetDx = new int[Kinds];
        private static readonly int[] OffsetDy = new int[Kinds];
        private static readonly int[,] KindFromOffset = new int[5, 5];
        private static readonly int[][] PermTable = new int[SymmetryCount][];
        private static readonly int[] InverseSym = new int[SymmetryCount];

        static ActionCodec() {
            for (int dy = 0; dy < 5; dy++)
                for (int dx = 0; dx < 5; dx++)
                    KindFromOffset[dy, dx] = -1;

            int kind = 1;
            for (int dy = -2; dy <= 2; dy++) {
                for (int dx = -2; dx <= 2; dx++) {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != 2) continue;
                    OffsetDx[kind] = dx;
                    OffsetDy[kind] = dy;
                    KindFromOffset[dy + 2, dx + 2] = kind;
                    kind++;
                }
            }
            if (kind != Kinds) throw new InvalidOperationException("Jump offset table is inconsistent.");

            for (int s = 0; s < SymmetryCount; s++) {
                var perm = new int[ActionCount];
                for (int a = 0; a < ActionCount; a++) perm[a] = ComputeTransformedAction(a, s);
                PermTable[s] = perm;
            }

            for (int s = 0; s < SymmetryCount; s++) {
                InverseSym[s] = -1;
                for (int t = 0; t < SymmetryCount; t++) {
                    bool identity = true;
                    for (int sq = 0; sq < Squares && identity; sq++) {
                        identity = TransformSquare(TransformSquare(sq, s), t) == sq;
                    }
                    if (identity) { InverseSym[s] = t; break; }
                }
                if (InverseSym[s] < 0) throw new InvalidOperationException("Symmetry set is not closed under inverse.");
            }
        }

        /// <summary>
        /// Maps a coordinate through symmetry <paramref name="symmetryIndex"/> using exactly the same formulas as
        /// <c>BitboardOps.ApplySymmetry</c> (a test asserts they agree). The map is affine, so it also works for off-board
        /// coordinates, which the codec relies on to transform jump sources that lie outside the board.
        /// Kept here, not in Core, because only the policy action space needs it.
        /// </summary>
        public static void TransformXY(int x, int y, int symmetryIndex, out int tx, out int ty) {
            const int n = BoardSize;
            switch (symmetryIndex) {
                case 0: tx = x; ty = y; break;
                case 1: tx = n - 1 - y; ty = x; break;
                case 2: tx = n - 1 - x; ty = n - 1 - y; break;
                case 3: tx = y; ty = n - 1 - x; break;
                case 4: tx = x; ty = n - 1 - y; break;
                case 5: tx = n - 1 - x; ty = y; break;
                case 6: tx = y; ty = x; break;
                case 7: tx = n - 1 - y; ty = n - 1 - x; break;
                default: throw new ArgumentOutOfRangeException(nameof(symmetryIndex), "symmetryIndex must be in range [0,7].");
            }
        }

        /// <summary>Maps a square index (y * 7 + x) through a symmetry.</summary>
        public static int TransformSquare(int squareIndex, int symmetryIndex) {
            TransformXY(squareIndex % BoardSize, squareIndex / BoardSize, symmetryIndex, out int tx, out int ty);
            return ty * BoardSize + tx;
        }

        private static int ComputeTransformedAction(int action, int sym) {
            int kind = action / Squares;
            int to = action % Squares;
            int newTo = TransformSquare(to, sym);
            if (kind == CloneKind) return newTo;

            int tx = to % BoardSize, ty = to / BoardSize;
            int fx = tx + OffsetDx[kind], fy = ty + OffsetDy[kind];
            TransformXY(tx, ty, sym, out int ntx, out int nty);
            TransformXY(fx, fy, sym, out int nfx, out int nfy);
            int ndx = nfx - ntx, ndy = nfy - nty;
            int newKind = KindFromOffset[ndy + 2, ndx + 2];
            if (newKind < 0) throw new InvalidOperationException("Symmetry produced an invalid jump offset.");
            return newKind * Squares + newTo;
        }

        public static int KindOf(int action) => action / Squares;
        public static int ToSquareOf(int action) => action % Squares;
        public static bool IsClone(int action) => action < Squares;
        public static bool IsValidIndex(int action) => action >= 0 && action < ActionCount;

        /// <summary>Jump offset (source minus destination) for a kind in 1..16.</summary>
        public static void GetJumpOffset(int kind, out int dx, out int dy) {
            if (kind < 1 || kind >= Kinds) throw new ArgumentOutOfRangeException(nameof(kind));
            dx = OffsetDx[kind];
            dy = OffsetDy[kind];
        }

        /// <summary>Source square of a jump action, or -1 when it lies off the board. Throws for clone actions.</summary>
        public static int JumpSourceOf(int action) {
            int kind = action / Squares;
            if (kind == CloneKind) throw new ArgumentException("Clone actions have no fixed source.", nameof(action));
            int to = action % Squares;
            int fx = to % BoardSize + OffsetDx[kind];
            int fy = to / BoardSize + OffsetDy[kind];
            if (fx < 0 || fx >= BoardSize || fy < 0 || fy >= BoardSize) return -1;
            return fy * BoardSize + fx;
        }

        public static int Encode(Move move) {
            int from = move.FromY * BoardSize + move.FromX;
            int to = move.ToY * BoardSize + move.ToX;
            return Encode(from, to);
        }

        public static int Encode(int fromSquare, int toSquare) {
            int dx = toSquare % BoardSize - fromSquare % BoardSize;
            int dy = toSquare / BoardSize - fromSquare / BoardSize;
            int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
            if (dist == 1) return toSquare;
            if (dist != 2) throw new ArgumentException("A move must be a clone (distance 1) or a jump (distance 2).");
            int kind = KindFromOffset[-dy + 2, -dx + 2]; // offset = from - to
            return kind * Squares + toSquare;
        }

        /// <summary>
        /// Converts an action to the engine's move for the given friendly/empty bitboards.
        /// Returns false if the action is not legal there. Clone sources are canonicalised to the lowest-index
        /// adjacent friendly piece, which is the same source the engine's move generator reports.
        /// </summary>
        public static bool TryDecode(int action, ulong friendly, ulong empty, out Move move) {
            move = default;
            if (!IsValidIndex(action)) return false;
            int to = action % Squares;
            if (((empty >> to) & 1UL) == 0) return false;

            int from;
            if (action < Squares) {
                ulong sources = BoardLookup.SingleStepMoves[to] & friendly;
                if (sources == 0) return false;
                from = BitboardOps.TrailingZeroCount(sources);
            } else {
                from = JumpSourceOf(action);
                if (from < 0 || ((friendly >> from) & 1UL) == 0) return false;
            }

            move = new Move(from % BoardSize, from / BoardSize, to % BoardSize, to / BoardSize);
            return true;
        }

        public static bool TryDecode(int action, BitboardState board, PlayerColor player, out Move move) {
            ulong friendly = player == PlayerColor.Red ? board.RedPieces : board.BluePieces;
            return TryDecode(action, friendly, board.EmptySquares(), out move);
        }

        public static bool IsLegal(int action, BitboardState board, PlayerColor player) => TryDecode(action, board, player, out _);

        /// <summary>
        /// Fills <paramref name="mask"/> (length ActionCount) with the legal actions for <paramref name="player"/>
        /// and returns how many there are. Computed straight from the bitboards.
        /// </summary>
        public static int FillLegalMask(BitboardState board, PlayerColor player, bool[] mask) {
            if (mask == null || mask.Length != ActionCount) throw new ArgumentException("mask must have ActionCount entries.", nameof(mask));
            Array.Clear(mask, 0, mask.Length);

            ulong friendly = player == PlayerColor.Red ? board.RedPieces : board.BluePieces;
            ulong empty = board.EmptySquares();
            int count = 0;

            ulong dests = empty;
            while (dests != 0) {
                int to = BitboardOps.TrailingZeroCount(dests);
                dests &= dests - 1;
                int tx = to % BoardSize, ty = to / BoardSize;

                if ((BoardLookup.SingleStepMoves[to] & friendly) != 0) {
                    mask[to] = true;
                    count++;
                }
                for (int kind = 1; kind < Kinds; kind++) {
                    int fx = tx + OffsetDx[kind], fy = ty + OffsetDy[kind];
                    if (fx < 0 || fx >= BoardSize || fy < 0 || fy >= BoardSize) continue;
                    if (((friendly >> (fy * BoardSize + fx)) & 1UL) != 0) {
                        mask[kind * Squares + to] = true;
                        count++;
                    }
                }
            }
            return count;
        }

        /// <summary>Appends the legal actions in ascending index order.</summary>
        public static void GetLegalActions(BitboardState board, PlayerColor player, List<int> actions) {
            var mask = new bool[ActionCount];
            FillLegalMask(board, player, mask);
            actions.Clear();
            for (int a = 0; a < ActionCount; a++) if (mask[a]) actions.Add(a);
        }

        /// <summary>Maps an action through a board symmetry (same ids as BitboardOps.ApplySymmetry).</summary>
        public static int TransformAction(int action, int symmetryIndex) {
            if (symmetryIndex < 0 || symmetryIndex >= SymmetryCount) throw new ArgumentOutOfRangeException(nameof(symmetryIndex));
            if (!IsValidIndex(action)) throw new ArgumentOutOfRangeException(nameof(action));
            return PermTable[symmetryIndex][action];
        }

        public static int InverseSymmetry(int symmetryIndex) => InverseSym[symmetryIndex];

        /// <summary>Copy of the permutation table for a symmetry: newIndex = perm[oldIndex].</summary>
        public static int[] GetPermutation(int symmetryIndex) => (int[])PermTable[symmetryIndex].Clone();
    }
}
