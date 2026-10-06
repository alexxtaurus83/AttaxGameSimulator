
using System;
using System.Collections.Generic;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core {
    /// <summary>Thread-safe node allowance for one move search.</summary>
    public sealed class SearchBudget {
        private long _remaining;
        private int _exhausted;

        public SearchBudget(long nodes) { _remaining = nodes < 0 ? 0 : nodes; if (_remaining == 0) _exhausted = 1; }

        public long Remaining => System.Threading.Interlocked.Read(ref _remaining);
        public bool Exhausted => System.Threading.Volatile.Read(ref _exhausted) != 0;

        /// <summary>Reserves one node. Returns false (and marks the budget exhausted) when none is left.</summary>
        public bool TryTake() {
            if (System.Threading.Interlocked.Decrement(ref _remaining) >= 0) return true;
            System.Threading.Interlocked.Exchange(ref _remaining, 0);
            System.Threading.Volatile.Write(ref _exhausted, 1);
            return false;
        }
    }

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

        // Per-move node budget shared by every helper/iteration/retry of one GetBestMove call.
        // null means unlimited. Once exhausted it stays exhausted (it can never turn into "unlimited").
        public SearchBudget Budget;

        // Set when the search was cut short (budget exhausted or time limit). Every node that sees it
        // returns immediately; results produced while it is set must not be trusted or cached.
        public bool Interrupted;

        // Cooperative cancellation for hint searches (AtaxxAIEngine.Search). Default token = never cancelled, so GetBestMove is unaffected.
        // A cancelled token behaves like Interrupted: every node unwinds immediately and the result must not be trusted.
        public System.Threading.CancellationToken Cancellation;

        /// <summary>
        /// Clears the transposition table, killer and history tables so a reused helper searches exactly like a
        /// freshly constructed one. (Clearing still touches the memory; it only avoids reallocating it.)
        /// </summary>
        public void ClearSearchMemory() {
            Array.Clear(transpositionTable, 0, transpositionTable.Length);
            Array.Clear(killerMovesForThread, 0, killerMovesForThread.Length);
            Array.Clear(historyHeuristic, 0, historyHeuristic.Length);
        }

        /// <summary>Clears per-search counters and state so a reused helper behaves like a fresh one.</summary>
        public void ResetForSearch() {
            Nodes = 0; QNodes = 0; Evals = 0; GetAllValidMovesCalls = 0; TTProbes = 0; TTHits = 0;
            Interrupted = false;
        }

        // Zero-alloc move buffers — eliminates all List<Move> allocations inside the search tree.
        // Each ply level writes its moves into a separate slot so recursive calls don't overwrite
        // the parent's moves while the parent loop is still iterating.
        // Proven bound for 7x7: with P own pieces and E empties (P + E <= 49), jumps <= 16*min(P,E) and
        // de-duplicated clone destinations <= E, so moves <= 409 (P=24, E=25). 512 leaves headroom.
        public const int MaxMovesBuffer = 512;
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
