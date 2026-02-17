using System;

namespace Attax.Core {
    public static class BoardLookup {
        public static readonly ulong[] SingleStepMoves;
        public static readonly ulong[] TwoStepMoves;
        public static readonly ulong[] OrthogonalStepMoves;
        public static readonly int[] CenterControlWeights;
        public static readonly ulong CornerMask;
        public static readonly ulong EdgeMask;

        static BoardLookup() {
            int boardSize = AttaxConstants.BaseConst.BoardSize;
            int totalSquares = boardSize * boardSize;
            SingleStepMoves = new ulong[totalSquares];
            TwoStepMoves = new ulong[totalSquares];
            OrthogonalStepMoves = new ulong[totalSquares];
            CenterControlWeights = new int[totalSquares];

            for (int y = 0; y < boardSize; y++) {
                for (int x = 0; x < boardSize; x++) {
                    int fromIndex = y * boardSize + x;
                    ulong singleStepMask = 0UL;
                    ulong twoStepMask = 0UL;

                    // Check all squares in a 5x5 box around the current square
                    for (int dy = -2; dy <= 2; dy++) {
                        for (int dx = -2; dx <= 2; dx++) {
                            if (dx == 0 && dy == 0) continue;

                            int toX = x + dx;
                            int toY = y + dy;

                            if (toX >= 0 && toX < boardSize && toY >= 0 && toY < boardSize) {
                                int toIndex = toY * boardSize + toX;
                                // The distance is the larger of the x or y change
                                int distance = Math.Max(Math.Abs(dx), Math.Abs(dy));

                                if (distance == 1) // Clone move distance
                                {
                                    singleStepMask |= (1UL << toIndex);
                                } else if (distance == 2) // Jump move distance
                                  {
                                    twoStepMask |= (1UL << toIndex);
                                }
                            }
                        }
                    }
                    SingleStepMoves[fromIndex] = singleStepMask;
                    TwoStepMoves[fromIndex] = twoStepMask;
                }
            }

            ulong cornerMask = 0UL;
            ulong edgeMask = 0UL;

            // Define Corners
            cornerMask |= (1UL << GetBitIndex(0, 0));
            cornerMask |= (1UL << GetBitIndex(boardSize - 1, 0));
            cornerMask |= (1UL << GetBitIndex(0, boardSize - 1));
            cornerMask |= (1UL << GetBitIndex(boardSize - 1, boardSize - 1));
            CornerMask = cornerMask;

            // Define Edges (excluding corners)
            for (int i = 0; i < boardSize; i++) {
                edgeMask |= (1UL << GetBitIndex(i, 0));             // Top row
                edgeMask |= (1UL << GetBitIndex(i, boardSize - 1)); // Bottom row
                edgeMask |= (1UL << GetBitIndex(0, i));             // Left column
                edgeMask |= (1UL << GetBitIndex(boardSize - 1, i)); // Right column
            }
            EdgeMask = edgeMask & ~CornerMask; // Exclude corners from the edge mask       

            for (int i = 0; i < totalSquares; i++) {
                ulong mask = 0UL;
                int x = i % boardSize;
                int y = i / boardSize;

                // Check the 4 orthogonal neighbors
                if (x > 0) mask |= (1UL << (i - 1));             // Left
                if (x < boardSize - 1) mask |= (1UL << (i + 1)); // Right
                if (y > 0) mask |= (1UL << (i - boardSize));     // Up
                if (y < boardSize - 1) mask |= (1UL << (i + boardSize)); // Down

                OrthogonalStepMoves[i] = mask;
            }

            int centerAreaStart = boardSize / 3; // For 7x7, this is 2. Loop from 2 to 4.
            int exactCenter = boardSize / 2;     // For 7x7, this is 3.

            for (int y = centerAreaStart; y < boardSize - centerAreaStart; y++) {
                for (int x = centerAreaStart; x < boardSize - centerAreaStart; x++) {
                    int dx = Math.Abs(x - exactCenter);
                    int dy = Math.Abs(y - exactCenter);
                    int weight = (exactCenter - dx) + (exactCenter - dy);

                    int index = GetBitIndex(x, y);
                    CenterControlWeights[index] = weight;
                }
            }
        }

        public static int GetBitIndex(int x, int y) {
            return y * AttaxConstants.BaseConst.BoardSize + x;
        }
    }
}
