using System;

namespace Attax.Core {
    public static class BitboardOps {
        // De Bruijn lookup table for 64-bit TrailingZeroCount.
        private static readonly int[] DeBruijnIndex64 = new int[64] {
            0, 1, 48, 2, 57, 49, 28, 3,
            61, 58, 50, 42, 38, 29, 17, 4,
            62, 55, 59, 36, 53, 51, 43, 22,
            45, 39, 33, 30, 24, 18, 12, 5,
            63, 47, 56, 27, 60, 41, 37, 16,
            54, 35, 52, 21, 44, 32, 23, 11,
            46, 26, 40, 15, 34, 20, 31, 10,
            25, 14, 19, 9, 13, 8, 7, 6
        };

        /// <summary>
        /// Counts the number of set bits (population count) in a 64-bit unsigned integer.
        /// Uses SWAR algorithm, portable and fast.
        /// </summary>
        public static int PopCount(ulong value) {
            value = value - ((value >> 1) & 0x5555555555555555UL);
            value = (value & 0x3333333333333333UL) + ((value >> 2) & 0x3333333333333333UL);
            value = (value + (value >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            value = value + (value >> 8);
            value = value + (value >> 16);
            value = value + (value >> 32);
            return (int)(value & 0x7FUL);
        }

        /// <summary>
        /// Returns the number of trailing zero bits in a 64-bit unsigned integer.
        /// Returns 64 if the value is zero.
        /// </summary>
        public static int TrailingZeroCount(ulong value) {
            if (value == 0) return 64;
            // Isolate least significant set bit, then map it to index via de Bruijn sequence.
            ulong isolated = value & (0UL - value);
            int lookupIndex = (int)((isolated * 0x03F79D71B4CB0A89UL) >> 58);
            return DeBruijnIndex64[lookupIndex];
        }

        public static ulong ApplySymmetry(ulong bitboard, int symmetryIndex) {
            const int boardSize = AttaxConstants.BaseConst.BoardSize;
            const int boardCells = boardSize * boardSize;

            if (symmetryIndex < 0 || symmetryIndex > 7) {
                throw new ArgumentOutOfRangeException(nameof(symmetryIndex), "symmetryIndex must be in range [0,7].");
            }

            ulong transformed = 0UL;
            ulong remaining = bitboard;

            while (remaining != 0UL) {
                int srcIndex = TrailingZeroCount(remaining);
                if (srcIndex >= boardCells) {
                    break;
                }

                int x = srcIndex % boardSize;
                int y = srcIndex / boardSize;

                int tx;
                int ty;
                switch (symmetryIndex) {
                    case 0: // Identity
                        tx = x; ty = y;
                        break;
                    case 1: // Rot90 (clockwise)
                        tx = boardSize - 1 - y; ty = x;
                        break;
                    case 2: // Rot180
                        tx = boardSize - 1 - x; ty = boardSize - 1 - y;
                        break;
                    case 3: // Rot270 (clockwise)
                        tx = y; ty = boardSize - 1 - x;
                        break;
                    case 4: // FlipX (mirror across horizontal axis)
                        tx = x; ty = boardSize - 1 - y;
                        break;
                    case 5: // FlipY (mirror across vertical axis)
                        tx = boardSize - 1 - x; ty = y;
                        break;
                    case 6: // FlipDiag1 (main diagonal)
                        tx = y; ty = x;
                        break;
                    case 7: // FlipDiag2 (anti-diagonal)
                        tx = boardSize - 1 - y; ty = boardSize - 1 - x;
                        break;
                    default:
                        tx = x; ty = y;
                        break;
                }

                int dstIndex = (ty * boardSize) + tx;
                transformed |= (1UL << dstIndex);

                remaining &= (remaining - 1);
            }

            return transformed;
        }

        public static void TransformState(ref ulong red, ref ulong blue, ref ulong blocked, int symmetryIndex) {
            red = ApplySymmetry(red, symmetryIndex);
            blue = ApplySymmetry(blue, symmetryIndex);
            blocked = ApplySymmetry(blocked, symmetryIndex);
        }
    }
}
