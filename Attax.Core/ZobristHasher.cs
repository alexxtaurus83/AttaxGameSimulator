using System;

namespace Attax.Core {
    public static class ZobristHasher {
        private static readonly ulong[,,] Table = new ulong[AttaxConstants.BaseConst.BoardSize, AttaxConstants.BaseConst.BoardSize, 3];
        private static readonly ulong SideToMoveKey;

        static ZobristHasher() {
            // Use a fixed seed for determinism across all engines and runs
            var rng = new Random(42);
            for (int p = 0; p < 3; p++) {
                for (int y = 0; y < AttaxConstants.BaseConst.BoardSize; y++) {
                    for (int x = 0; x < AttaxConstants.BaseConst.BoardSize; x++) {
                        byte[] buffer = new byte[8];
                        rng.NextBytes(buffer);
                        Table[x, y, p] = BitConverter.ToUInt64(buffer, 0);
                    }
                }
            }

            byte[] sideBytes = new byte[8];
            rng.NextBytes(sideBytes);
            SideToMoveKey = BitConverter.ToUInt64(sideBytes, 0);
        }

        public static ulong GetPieceKey(int x, int y, int pieceTypeIndex) {
            return Table[x, y, pieceTypeIndex];
        }

        public static ulong GetSideToMoveKey() {
            return SideToMoveKey;
        }

        public static ulong ComputeHash(BitboardState board, AtaxxAIEngine.PlayerColor sideToMove) {
            ulong hash = 0;
            
            // Red pieces
            ulong temp = board.RedPieces;
            while (temp > 0) {
                int index = BitboardOps.TrailingZeroCount(temp);
                hash ^= Table[index % AttaxConstants.BaseConst.BoardSize, index / AttaxConstants.BaseConst.BoardSize, 0];
                temp &= temp - 1;
            }

            // Blue pieces
            temp = board.BluePieces;
            while (temp > 0) {
                int index = BitboardOps.TrailingZeroCount(temp);
                hash ^= Table[index % AttaxConstants.BaseConst.BoardSize, index / AttaxConstants.BaseConst.BoardSize, 1];
                temp &= temp - 1;
            }

            // Blocked squares
            temp = board.BlockedSquares;
            while (temp > 0) {
                int index = BitboardOps.TrailingZeroCount(temp);
                hash ^= Table[index % AttaxConstants.BaseConst.BoardSize, index / AttaxConstants.BaseConst.BoardSize, 2];
                temp &= temp - 1;
            }

            // Side to move: XOR if it's Blue's turn (matching existing convention)
            if (sideToMove == AtaxxAIEngine.PlayerColor.Blue) {
                hash ^= SideToMoveKey;
            }

            return hash;
        }
    }
}
