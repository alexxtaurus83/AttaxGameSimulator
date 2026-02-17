
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

        public AtaxxThreadHelper(int boardSize, int maxDepth) {
            boardForThread = new BitboardState();
            killerMovesForThread = new Move[maxDepth, 2];
            historyHeuristic = new int[64, 64];
            transpositionTable = new TTEntry[TTSize];
        }
    }
}
