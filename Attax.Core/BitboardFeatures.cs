using System;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core {
    public static class BitboardFeatures {
        /// <summary>
        /// Returns a bitboard of all squares a player can move to (clone or jump) from their current pieces.
        /// </summary>
        public static ulong GetMoveDestinationsBitboard(BitboardState board, PlayerColor player) {
            ulong playerPieces = (player == PlayerColor.Red) ? board.RedPieces : board.BluePieces;
            ulong emptySquares = board.EmptySquares();
            ulong destinations = 0UL;

            ulong remainingPieces = playerPieces;
            while (remainingPieces > 0) {
                int fromIndex = BitboardOps.TrailingZeroCount(remainingPieces);
                destinations |= (BoardLookup.SingleStepMoves[fromIndex] | BoardLookup.TwoStepMoves[fromIndex]);
                remainingPieces &= remainingPieces - 1;
            }

            return destinations & emptySquares;
        }

        /// <summary>
        /// Calculates potential mobility from a bitboard of destination squares.
        /// </summary>
        public static int GetPotentialMobilityInternal(BitboardState boardState, ulong destinationSquares) {
            ulong emptySquares = boardState.EmptySquares();
            ulong potentialMobilitySquares = 0UL;

            ulong remainingDestinations = destinationSquares;
            while (remainingDestinations > 0) {
                int destIndex = BitboardOps.TrailingZeroCount(remainingDestinations);
                potentialMobilitySquares |= BoardLookup.SingleStepMoves[destIndex];
                remainingDestinations &= remainingDestinations - 1;
            }

            ulong finalMobilityArea = potentialMobilitySquares & emptySquares;
            return BitboardOps.PopCount(finalMobilityArea);
        }

        /// <summary>
        /// Calculates a weighted score for center control using pre‑computed weights.
        /// </summary>
        public static int GetCenterControl(BitboardState boardState, PlayerColor player) {
            int controlScore = 0;
            ulong playerPieces = (player == PlayerColor.Red) ? boardState.RedPieces : boardState.BluePieces;

            ulong remainingPieces = playerPieces;
            while (remainingPieces > 0) {
                int pieceIndex = BitboardOps.TrailingZeroCount(remainingPieces);
                controlScore += BoardLookup.CenterControlWeights[pieceIndex];
                remainingPieces &= remainingPieces - 1;
            }

            return controlScore;
        }

        /// <summary>
        /// Calculates the stability bonus for a player.
        /// </summary>
        public static int GetStabilityBonus(BitboardState boardState, PlayerColor player) {
            int bonus = 0;
            ulong playerPieces = (player == PlayerColor.Red) ? boardState.RedPieces : boardState.BluePieces;

            ulong remainingPieces = playerPieces;
            while (remainingPieces > 0) {
                int pieceIndex = BitboardOps.TrailingZeroCount(remainingPieces);
                int x = pieceIndex % AttaxConstants.BaseConst.BoardSize;
                int y = pieceIndex / AttaxConstants.BaseConst.BoardSize;

                if (IsStable(boardState, x, y, player)) {
                    bonus++;
                }

                remainingPieces &= remainingPieces - 1;
            }

            return bonus;
        }

        /// <summary>
        /// Checks if a piece at a given coordinate is stable.
        /// </summary>
        public static bool IsStable(BitboardState boardState, int x, int y, PlayerColor player) {
            bool isCorner = (x == 0 || x == AttaxConstants.BaseConst.BoardSize - 1) &&
                            (y == 0 || y == AttaxConstants.BaseConst.BoardSize - 1);
            if (isCorner) return true;

            int pieceIndex = BoardLookup.GetBitIndex(x, y);
            ulong pieceNeighborsMask = BoardLookup.SingleStepMoves[pieceIndex];

            PlayerColor opponentColor = SwitchPlayer(player);
            ulong opponentPieces = (opponentColor == PlayerColor.Red) ? boardState.RedPieces : boardState.BluePieces;
            ulong adjacentOpponents = pieceNeighborsMask & opponentPieces;

            if (adjacentOpponents == 0) return true;

            ulong remainingOpponents = adjacentOpponents;
            while (remainingOpponents > 0) {
                int opponentIndex = BitboardOps.TrailingZeroCount(remainingOpponents);
                int opponentX = opponentIndex % AttaxConstants.BaseConst.BoardSize;
                int opponentY = opponentIndex / AttaxConstants.BaseConst.BoardSize;

                if (HasAdjacentEmpty(boardState, opponentX, opponentY)) {
                    return false;
                }

                remainingOpponents &= remainingOpponents - 1;
            }

            return true;
        }

        /// <summary>
        /// Checks if there is at least one empty square within a 2‑tile radius of a given coordinate.
        /// </summary>
        public static bool HasAdjacentEmpty(BitboardState boardState, int x, int y) {
            ulong emptySquares = boardState.EmptySquares();
            int checkIndex = BoardLookup.GetBitIndex(x, y);
            ulong allNeighborsMask = BoardLookup.SingleStepMoves[checkIndex] | BoardLookup.TwoStepMoves[checkIndex];
            return (allNeighborsMask & emptySquares) != 0;
        }

        /// <summary>
        /// Gets piece counts for a player and their opponent.
        /// </summary>
        public static (int p1Count, int p2Count) GetRedAndBlueCounts(BitboardState boardState, PlayerColor p1Color) {
            int p1Count;
            int p2Count;

            if (p1Color == PlayerColor.Red) {
                p1Count = BitboardOps.PopCount(boardState.RedPieces);
                p2Count = BitboardOps.PopCount(boardState.BluePieces);
            } else // p1Color is Blue
              {
                p1Count = BitboardOps.PopCount(boardState.BluePieces);
                p2Count = BitboardOps.PopCount(boardState.RedPieces);
            }

            return (p1Count, p2Count);
        }

        private static PlayerColor SwitchPlayer(PlayerColor current) {
            return current == PlayerColor.Red ? PlayerColor.Blue : current == PlayerColor.Blue ? PlayerColor.Red : PlayerColor.None;
        }
    }
}
