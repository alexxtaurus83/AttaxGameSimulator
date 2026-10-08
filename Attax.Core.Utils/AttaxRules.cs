using System;
using System.Collections.Generic;

namespace Attax.Core.Utils {
    /// <summary>
    /// Pure, authoritative game rules implementation for Attax (7x7 board).
    /// Handles move validation, execution, piece flips, chip conversions, and terminal state evaluation
    /// without any dependency on AI search, transposition tables, or machine learning models.
    /// </summary>
    public static class AttaxRules {
        public static int GetBitIndex(int x, int y) => BoardLookup.GetBitIndex(x, y);

        public static PlayerColor SwitchPlayer(PlayerColor current) =>
            current == PlayerColor.Red ? PlayerColor.Blue :
            current == PlayerColor.Blue ? PlayerColor.Red :
            PlayerColor.None;

        public static int GetPieceTypeIndex(PlayerColor color) =>
            color == PlayerColor.Red ? 0 :
            color == PlayerColor.Blue ? 1 : 2;

        public static bool IsCloneMove(Move move) =>
            Math.Max(Math.Abs(move.ToX - move.FromX), Math.Abs(move.ToY - move.FromY)) == 1;

        public static bool IsJumpMove(Move move) =>
            Math.Max(Math.Abs(move.ToX - move.FromX), Math.Abs(move.ToY - move.FromY)) == 2;

        public static bool IsSuperJump(Move move) =>
            Math.Max(Math.Abs(move.ToX - move.FromX), Math.Abs(move.ToY - move.FromY)) >= 3;

        /// <summary>
        /// Validates whether a move is legal from geometry, source ownership, and destination availability.
        /// </summary>
        public static bool IsLegalMove(BitboardState board, PlayerColor player, Move move, bool allowSuperJump = false) {
            int size = AttaxConstants.BaseConst.BoardSize;
            if (move.FromX < 0 || move.FromY < 0 || move.FromX >= size || move.FromY >= size) return false;
            if (move.ToX < 0 || move.ToY < 0 || move.ToX >= size || move.ToY >= size) return false;

            int fromIndex = GetBitIndex(move.FromX, move.FromY);
            int toIndex = GetBitIndex(move.ToX, move.ToY);
            if (fromIndex == toIndex) return false;

            ulong playerPieces = (player == PlayerColor.Red) ? board.RedPieces : board.BluePieces;
            if ((playerPieces & (1UL << fromIndex)) == 0) return false;

            if ((board.EmptySquares() & (1UL << toIndex)) == 0) return false;

            int d = Math.Max(Math.Abs(move.ToX - move.FromX), Math.Abs(move.ToY - move.FromY));
            return d <= 2 || allowSuperJump;
        }

        /// <summary>
        /// Executes a validated move on the bitboard state, flips adjacent enemy pieces, and updates Zobrist hash.
        /// </summary>
        public static MoveResult MakeMove(BitboardState boardState, Move move, PlayerColor player) {
            var moveResult = new MoveResult {
                MoveMade = move,
                FlippedPieces = new List<(int, int)>(),
            };
            var undoInfo = new UndoMoveInfo { PreviousZobristHash = boardState.ZobristHash };

            int fromIndex = GetBitIndex(move.FromX, move.FromY);
            int toIndex = GetBitIndex(move.ToX, move.ToY);
            ulong fromMask = 1UL << fromIndex;
            ulong toMask = 1UL << toIndex;

            if ((boardState.BlockedSquares & toMask) != 0)
                throw new InvalidOperationException("Moving to blocked square");

            ref ulong playerPieces = ref (player == PlayerColor.Red ? ref boardState.RedPieces : ref boardState.BluePieces);
            ref ulong opponentPieces = ref (player == PlayerColor.Red ? ref boardState.BluePieces : ref boardState.RedPieces);

            boardState.ZobristHash ^= ZobristHasher.GetSideToMoveKey();

            bool isClone = IsCloneMove(move);
            moveResult.WasClone = isClone;
            if (!isClone) {
                playerPieces &= ~fromMask;
                boardState.ZobristHash ^= ZobristHasher.GetPieceKey(move.FromX, move.FromY, GetPieceTypeIndex(player));
            }

            playerPieces |= toMask;
            boardState.ZobristHash ^= ZobristHasher.GetPieceKey(move.ToX, move.ToY, GetPieceTypeIndex(player));

            ulong attackMask = BoardLookup.SingleStepMoves[toIndex];
            ulong flippedPieces = opponentPieces & attackMask;
            undoInfo.FlippedPiecesMask = flippedPieces;

            if (flippedPieces > 0) {
                playerPieces |= flippedPieces;
                opponentPieces &= ~flippedPieces;

                ulong remainingFlipped = flippedPieces;
                while (remainingFlipped > 0) {
                    int flippedIndex = BitboardOps.TrailingZeroCount(remainingFlipped);
                    int flippedX = flippedIndex % AttaxConstants.BaseConst.BoardSize;
                    int flippedY = flippedIndex / AttaxConstants.BaseConst.BoardSize;
                    moveResult.FlippedPieces.Add((flippedX, flippedY));

                    boardState.ZobristHash ^= ZobristHasher.GetPieceKey(flippedX, flippedY, GetPieceTypeIndex(SwitchPlayer(player)));
                    boardState.ZobristHash ^= ZobristHasher.GetPieceKey(flippedX, flippedY, GetPieceTypeIndex(player));

                    remainingFlipped &= remainingFlipped - 1;
                }
            }
            moveResult.UndoInfo = undoInfo;
            return moveResult;
        }

        /// <summary>
        /// Converts targeted enemy chip to own chip or blocked square. Does not advance turn.
        /// </summary>
        public static bool TryConvertEnemyChip(BitboardState board, PlayerColor player, int x, int y, ChipConversion conversion = ChipConversion.ToPlayer) {
            int size = AttaxConstants.BaseConst.BoardSize;
            if (player != PlayerColor.Red && player != PlayerColor.Blue) return false;
            if (x < 0 || y < 0 || x >= size || y >= size) return false;

            ulong mask = 1UL << GetBitIndex(x, y);
            ref ulong playerPieces = ref (player == PlayerColor.Red ? ref board.RedPieces : ref board.BluePieces);
            ref ulong enemyPieces = ref (player == PlayerColor.Red ? ref board.BluePieces : ref board.RedPieces);
            if ((enemyPieces & mask) == 0) return false;

            PlayerColor enemy = SwitchPlayer(player);
            enemyPieces &= ~mask;
            board.ZobristHash ^= ZobristHasher.GetPieceKey(x, y, GetPieceTypeIndex(enemy));
            if (conversion == ChipConversion.ToBlocked) {
                board.BlockedSquares |= mask;
                board.ZobristHash ^= ZobristHasher.GetPieceKey(x, y, GetPieceTypeIndex(PlayerColor.Blocked));
            } else {
                playerPieces |= mask;
                board.ZobristHash ^= ZobristHasher.GetPieceKey(x, y, GetPieceTypeIndex(player));
            }
            return true;
        }

        public static List<(int X, int Y)> GetConvertibleChips(BitboardState board, PlayerColor player) {
            var cells = new List<(int X, int Y)>();
            int size = AttaxConstants.BaseConst.BoardSize;
            ulong enemy = (player == PlayerColor.Red) ? board.BluePieces : board.RedPieces;
            while (enemy > 0) {
                int index = BitboardOps.TrailingZeroCount(enemy);
                cells.Add((index % size, index / size));
                enemy &= enemy - 1;
            }
            return cells;
        }

        public static List<Move> GetValidMovesFrom(BitboardState board, PlayerColor player, int x, int y, bool superJump = false) {
            var moves = new List<Move>();
            int size = AttaxConstants.BaseConst.BoardSize;
            if (x < 0 || y < 0 || x >= size || y >= size) return moves;

            int fromIndex = GetBitIndex(x, y);
            ulong playerPieces = (player == PlayerColor.Red) ? board.RedPieces : board.BluePieces;
            if ((playerPieces & (1UL << fromIndex)) == 0) return moves;
            ulong empty = board.EmptySquares();

            ulong clones = BoardLookup.SingleStepMoves[fromIndex] & empty;
            while (clones > 0) {
                int toIndex = BitboardOps.TrailingZeroCount(clones);
                moves.Add(new Move(x, y, toIndex % size, toIndex / size));
                clones &= clones - 1;
            }

            ulong jumps = BoardLookup.TwoStepMoves[fromIndex] & empty;
            while (jumps > 0) {
                int toIndex = BitboardOps.TrailingZeroCount(jumps);
                moves.Add(new Move(x, y, toIndex % size, toIndex / size));
                jumps &= jumps - 1;
            }

            if (superJump) {
                ulong far = empty
                    & ~(BoardLookup.SingleStepMoves[fromIndex] | BoardLookup.TwoStepMoves[fromIndex])
                    & ~(1UL << fromIndex);
                while (far > 0) {
                    int toIndex = BitboardOps.TrailingZeroCount(far);
                    moves.Add(new Move(x, y, toIndex % size, toIndex / size));
                    far &= far - 1;
                }
            }
            return moves;
        }

        public static bool HasAnyLegalMove(BitboardState board, PlayerColor player) {
            ulong playerPieces = (player == PlayerColor.Red) ? board.RedPieces : board.BluePieces;
            ulong emptySquares = board.EmptySquares();
            if (emptySquares == 0) return false;

            while (playerPieces > 0) {
                int fromIndex = BitboardOps.TrailingZeroCount(playerPieces);
                if (((BoardLookup.SingleStepMoves[fromIndex] | BoardLookup.TwoStepMoves[fromIndex]) & emptySquares) != 0) {
                    return true;
                }
                playerPieces &= playerPieces - 1;
            }
            return false;
        }

        public static bool IsGameOver(BitboardState boardState) {
            int redCount = BitboardOps.PopCount(boardState.RedPieces);
            int blueCount = BitboardOps.PopCount(boardState.BluePieces);
            int emptyCount = BitboardOps.PopCount(boardState.EmptySquares());

            if (redCount == 0 || blueCount == 0 || emptyCount == 0) {
                return true;
            }

            if (!HasAnyLegalMove(boardState, PlayerColor.Red) ||
                !HasAnyLegalMove(boardState, PlayerColor.Blue)) {
                return true;
            }

            return false;
        }

        public static TerminalResult GetTerminalResult(BitboardState boardState) {
            if (!IsGameOver(boardState)) return TerminalResult.NotOver;

            bool redCanMove = HasAnyLegalMove(boardState, PlayerColor.Red);
            bool blueCanMove = HasAnyLegalMove(boardState, PlayerColor.Blue);
            if (redCanMove && !blueCanMove) return TerminalResult.RedWins;
            if (blueCanMove && !redCanMove) return TerminalResult.BlueWins;

            int redCount = BitboardOps.PopCount(boardState.RedPieces);
            int blueCount = BitboardOps.PopCount(boardState.BluePieces);
            if (redCount > blueCount) return TerminalResult.RedWins;
            if (blueCount > redCount) return TerminalResult.BlueWins;
            return TerminalResult.Draw;
        }

        public static int GetTerminalOutcomeFor(BitboardState boardState, PlayerColor player) {
            switch (GetTerminalResult(boardState)) {
                case TerminalResult.RedWins: return player == PlayerColor.Red ? 1 : -1;
                case TerminalResult.BlueWins: return player == PlayerColor.Blue ? 1 : -1;
                default: return 0;
            }
        }

        public static int CountFlippedEnemies(BitboardState boardState, Move move, PlayerColor player) {
            ulong opponentPieces = (player == PlayerColor.Red)
                ? boardState.BluePieces
                : boardState.RedPieces;
            int toIndex = GetBitIndex(move.ToX, move.ToY);
            ulong attackMask = BoardLookup.SingleStepMoves[toIndex];
            return BitboardOps.PopCount(opponentPieces & attackMask);
        }
    }
}
