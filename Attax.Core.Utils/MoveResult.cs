using System.Collections.Generic;

namespace Attax.Core.Utils {
    public struct MoveResult {
        public Move MoveMade;
        public bool WasClone;
        public List<(int x, int y)> FlippedPieces;
        public UndoMoveInfo UndoInfo;
    }

    public struct UndoMoveInfo {
        public ulong PreviousZobristHash;
        public ulong FlippedPiecesMask;
    }
}
