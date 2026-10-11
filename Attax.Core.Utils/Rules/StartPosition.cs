using System;
using System.Collections.Generic;
using ZLinq;

namespace Attax.Core.Utils {
    public static class StartPosition {
        public const int MaxRandomBlockAttempts = 1000;

        /// <summary>
        /// Standard four-piece start. Explicitly computes ZobristHash for firstPlayer (default Red).
        /// </summary>
        public static BitboardState Standard(PlayerColor firstPlayer = PlayerColor.Red) {
            var b = new BitboardState {
                RedPieces = (1UL << 0) | (1UL << 48),
                BluePieces = (1UL << 6) | (1UL << 42),
                BlockedSquares = 0UL
            };
            b.ZobristHash = ZobristHasher.ComputeHash(b, firstPlayer);
            return b;
        }

        /// <summary>
        /// Standard start plus blocked cells generated deterministically from seed.
        /// </summary>
        public static BitboardState Create(int blockedCells, int seed, PlayerColor firstPlayer = PlayerColor.Red) {
            var board = Standard(firstPlayer);
            if (blockedCells <= 0) return board;

            var blocks = GenerateBlockedCellPositions(blockedCells, board, seed);
            foreach (var (x, y) in blocks) {
                board.BlockedSquares |= 1UL << BoardLookup.GetBitIndex(x, y);
            }
            board.ZobristHash = ZobristHasher.ComputeHash(board, firstPlayer);
            return board;
        }

        public static List<(int x, int y)> GenerateBlockedCellPositions(int count, BitboardState board, int seed) {
            return GenerateBlockedCellPositions(count, board, new Random(seed));
        }

        public static List<(int x, int y)> GenerateBlockedCellPositions(int count, BitboardState board, Random rnd) {
            if (count <= 0) {
                return new List<(int, int)>();
            }

            var blockedPositions = new List<(int, int)>();
            int maxAttempts = MaxRandomBlockAttempts;
            int attempts = 0;

            ulong emptySquares = board.EmptySquares();

            int zoneCount = (int)Math.Ceiling(Math.Sqrt(count));
            int zoneSize = AttaxConstants.BaseConst.BoardSize / zoneCount;

            var zones = new List<(int zx, int zy)>();
            for (int zx = 0; zx < zoneCount; zx++) {
                for (int zy = 0; zy < zoneCount; zy++) {
                    zones.Add((zx, zy));
                }
            }
            zones = zones.AsValueEnumerable().OrderBy(_ => rnd.Next()).ToList();

            while (blockedPositions.Count < count && attempts < maxAttempts) {
                foreach (var zone in zones) {
                    if (blockedPositions.Count >= count) break;

                    int minX = zone.zx * zoneSize;
                    int maxX = Math.Min((zone.zx + 1) * zoneSize, AttaxConstants.BaseConst.BoardSize);
                    int minY = zone.zy * zoneSize;
                    int maxY = Math.Min((zone.zy + 1) * zoneSize, AttaxConstants.BaseConst.BoardSize);

                    int x = rnd.Next(minX, maxX);
                    int y = rnd.Next(minY, maxY);
                    var pos = (x, y);

                    ulong posBit = 1UL << BoardLookup.GetBitIndex(x, y);
                    if ((emptySquares & posBit) != 0 && !blockedPositions.Contains(pos)) {
                        blockedPositions.Add(pos);
                    }
                }
                attempts++;
            }

            return blockedPositions;
        }
    }
}
