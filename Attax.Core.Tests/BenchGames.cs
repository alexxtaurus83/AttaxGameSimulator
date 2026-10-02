using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Attax.Core;
using Xunit;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core.Tests {
    // Benchmark, only runs when ATTAX_BENCH is set. Mirrors self-play: depth 3, training mode, serial root,
    // one engine pair per game, games in parallel, fixed seeds.
    public class BenchGames {
        [Fact]
        public void Bench() {
            string path = Environment.GetEnvironmentVariable("ATTAX_BENCH");
            if (string.IsNullOrEmpty(path)) return;
            int games = 16;
            long totalMoves = 0, totalNodes = 0;
            long gc0 = GC.CollectionCount(0), gc2 = GC.CollectionCount(2);
            long alloc0 = GC.GetTotalAllocatedBytes(true);
            var sw = Stopwatch.StartNew();
            Parallel.For(0, games, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, g => {
                var rng = new Random(500 + g);
                var red = Make(g * 2 + 1);
                var blue = Make(g * 2 + 2);
                var board = new BitboardState { RedPieces = (1UL << 0) | (1UL << 48), BluePieces = (1UL << 6) | (1UL << 42) };
                var side = PlayerColor.Red;
                for (int ply = 0; ply < 70; ply++) {
                    var e = side == PlayerColor.Red ? red : blue;
                    e.AIPlayerColor = side;
                    TestBoards.SetBoard(e, board.Clone(), side);
                    var legal = e.GetAllValidMoves(board, side);
                    if (legal.Count == 0) break;
                    Move m = ply < 4 ? legal[rng.Next(legal.Count)] : e.GetBestMove(side);
                    if (m.Equals(default(Move))) break;
                    if (ply >= 4) {
                        System.Threading.Interlocked.Increment(ref totalMoves);
                        System.Threading.Interlocked.Add(ref totalNodes, e.LastSearchStats.Nodes + e.LastSearchStats.QNodes);
                    }
                    e.MakeMove(board, m, side);
                    side = AtaxxAIEngine.SwitchPlayer(side);
                    if (red.IsGameOver(board)) break;
                }
            });
            sw.Stop();
            long allocMb = (GC.GetTotalAllocatedBytes(true) - alloc0) / (1024 * 1024);
            File.WriteAllText(path,
                $"games={games} moves={totalMoves} nodes={totalNodes} seconds={sw.Elapsed.TotalSeconds:F2} " +
                $"moves/s={totalMoves / sw.Elapsed.TotalSeconds:F1} allocMB={allocMb} gen0={GC.CollectionCount(0) - gc0} gen2={GC.CollectionCount(2) - gc2}\n");
        }

        private static AtaxxAIEngine Make(int seed) {
            var cfg = new AIEngineConfig { DisableRandomRootTies = true, AiDepth = 3, TrainingMode = true, DisableParallelRootSearch = true, DisableQuiescenceSearch = true, Seed = seed };
            return new AtaxxAIEngine(Prm.Base(), cfg);
        }
    }
}
