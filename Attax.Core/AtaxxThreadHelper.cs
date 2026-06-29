
using System;
using System.Collections.Generic;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core {
    public class AtaxxThreadHelper {

        public BitboardState boardForThread { get; set; }
        public Move[,] killerMovesForThread { get; set; }
        public int[,] historyHeuristic { get; set; }
        public TTEntry[] transpositionTable { get; set; }
        public const int TTSize = 1 << 20;

        // Performance Counters
        public long Nodes;
        public long QNodes;
        public long Evals;
        public long GetAllValidMovesCalls;
        public long TTProbes;
        public long TTHits;

        // Per-search node budget. -1 means unlimited.
        public long NodesRemaining = -1;

        // Zero-alloc move buffers — eliminates all List<Move> allocations inside the search tree.
        // Each ply level writes its moves into a separate slot so recursive calls don't overwrite
        // the parent's moves while the parent loop is still iterating.
        public const int MaxMovesBuffer = 256; // safe upper bound for 7x7 Ataxx
        public const int MaxPly = 32;          // covers aiDepth + null moves + quiescence headroom
        public readonly Move[,] perPlyMoveBuffer = new Move[MaxPly, MaxMovesBuffer];
        public readonly int[] perPlyMoveCount = new int[MaxPly];
        // Staging area for raw (unordered) moves before classification; safe across ply levels
        // because FillMoves always finishes before results are committed to perPlyMoveBuffer.
        public readonly Move[] rawMoveBuffer = new Move[MaxMovesBuffer];
        // Scratch space for capture sorting: stores (flipCount, rawBufferIndex).
        public readonly (int flips, int moveIdx)[] sortScratch = new (int, int)[MaxMovesBuffer];

        public AtaxxThreadHelper(int boardSize, int maxDepth) {
            boardForThread = new BitboardState();
            killerMovesForThread = new Move[maxDepth, 2];
            historyHeuristic = new int[64, 64];
            transpositionTable = new TTEntry[TTSize];
        }
    }
}
