using System.Diagnostics;
using System.Globalization;
using Attax.Core;
using Attax.Data;
using Attax.Play;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Console {
    partial class Program {
        /// <summary>
        /// Same positions, two players, no games. A parameter that changes the move in 0.0% of positions cannot change strength;
        /// one that changes it in 2% is worth a match. Both players get a FRESH engine for every position (no transposition-table
        /// carry-over), so two identical specs always agree on 100% of positions, whatever the thread scheduling.
        /// </summary>
        static void RunCompareMoves(CompareMovesOptions o) {
            if (o.Count <= 0) throw new ArgumentException("--count must be > 0.");
            var a = PlayerSpec.Parse(o.A); var b = PlayerSpec.Parse(o.B);
            if (!a.IsSearch || !b.IsSearch) throw new ArgumentException("compare-moves needs search players (classic or value); policy and random players have no search to compare.");
            a.ResolveParams(); b.ResolveParams();     // fail early on a bad file or override
            int parallel = o.Parallel > 0 ? o.Parallel : Environment.ProcessorCount;
            using var models = CreateModelSource(parallel, a, b);

            // Reservoir sample (algorithm R) of distinct positions with at least two legal moves.
            var referee = NewReferee();
            var rng = new Random(o.Seed);
            var seen = new HashSet<(ulong, ulong, ulong, byte)>();
            var picked = new List<(BitboardState b, PlayerColor side)>(o.Count);
            long eligible = 0;
            foreach (var g in new LogV3Reader().ReadGames(o.Positions, semanticChecks: false)) {
                foreach (var s in g.Samples) {
                    if (!seen.Add((s.Red, s.Blue, s.Blocked, s.Side))) continue;
                    var board = LogV3.ToBoard(s); var side = LogV3.SideOf(s);
                    if (referee.GetAllValidMoves(board, side).Count < 2) continue;
                    eligible++;
                    if (picked.Count < o.Count) picked.Add((board, side));
                    else { long j = rng.NextInt64(eligible); if (j < o.Count) picked[(int)j] = (board, side); }
                }
            }
            if (picked.Count == 0) throw new InvalidDataException("No usable positions in " + o.Positions);
            System.Console.WriteLine($"[compare] {picked.Count} positions sampled from {eligible} distinct eligible ones in {o.Positions}");
            System.Console.WriteLine($"[compare] A: {PlayerFactory.Describe(a, false)}");
            System.Console.WriteLine($"[compare] B: {PlayerFactory.Describe(b, false)}");
            var diffs = EngineParamsFile.Diff(b.ResolveParams(), a.ResolveParams());   // both are search players here
            foreach (var d in diffs) System.Console.WriteLine("[compare]   params B -> A: " + d);

            var moveA = new int[picked.Count]; var moveB = new int[picked.Count];
            long nodesA = 0, nodesB = 0, ticksA = 0, ticksB = 0;
            var wall = Stopwatch.StartNew();
            Parallel.For(0, picked.Count, new ParallelOptions { MaxDegreeOfParallelism = parallel }, i => {
                var (bd, side) = picked[i];
                using var pa = PlayerFactory.Create(a, models, 11, defaultTrain: false);
                using var pb = PlayerFactory.Create(b, models, 11, defaultTrain: false);
                var da = pa.Choose(bd, side); var db = pb.Choose(bd, side);
                moveA[i] = da.Action; moveB[i] = db.Action;
                Interlocked.Add(ref nodesA, da.Nodes); Interlocked.Add(ref nodesB, db.Nodes);
                Interlocked.Add(ref ticksA, (long)(da.Milliseconds / 1000.0 * Stopwatch.Frequency));
                Interlocked.Add(ref ticksB, (long)(db.Milliseconds / 1000.0 * Stopwatch.Frequency));
            });
            wall.Stop();

            string[] names = { "opening (<=14 pieces)", "midgame (15-35)", "late (>35)" };
            var total = new int[3]; var differ = new int[3];
            var examples = new List<int>();
            for (int i = 0; i < picked.Count; i++) {
                int pieces = PopCount(picked[i].b.RedPieces | picked[i].b.BluePieces);
                int ph = pieces <= 14 ? 0 : pieces <= 35 ? 1 : 2;
                total[ph]++;
                if (moveA[i] != moveB[i]) { differ[ph]++; if (examples.Count < o.Show) examples.Add(i); }
            }
            int dAll = differ.Sum(), nAll = picked.Count;
            var inv = CultureInfo.InvariantCulture;
            System.Console.WriteLine("[compare] ---- result ----");
            System.Console.WriteLine($"[compare] different move in {dAll} of {nAll} positions = {100.0 * dAll / nAll:0.00}%");
            for (int k = 0; k < 3; k++) if (total[k] > 0) System.Console.WriteLine($"[compare]   {names[k],-22} {differ[k],5} of {total[k],5} = {100.0 * differ[k] / total[k]:0.00}%");
            System.Console.WriteLine($"[compare] nodes/move A={nodesA / (double)nAll:0} B={nodesB / (double)nAll:0} (B/A = {(nodesA == 0 ? 0 : nodesB / (double)nodesA):0.000}); ms/move A={Ms(ticksA) / nAll:0.0} B={Ms(ticksB) / nAll:0.0}; wall {wall.Elapsed.TotalSeconds:0.0}s");
            foreach (int i in examples) {
                var (bd, side) = picked[i];
                System.Console.WriteLine($"[compare] example {i}: {side} to move, A plays {DescribeAction(moveA[i], bd, side)}, B plays {DescribeAction(moveB[i], bd, side)}");
                for (int y = 0; y < 7; y++) {
                    var row = new char[7];
                    for (int x = 0; x < 7; x++) { ulong bit = 1UL << (y * 7 + x); row[x] = (bd.RedPieces & bit) != 0 ? 'R' : (bd.BluePieces & bit) != 0 ? 'B' : (bd.BlockedSquares & bit) != 0 ? 'X' : '.'; }
                    System.Console.WriteLine("[compare]     " + new string(row));
                }
            }
            System.Console.WriteLine($"RESULT compare n={nAll} differ={dAll} pct={(100.0 * dAll / nAll).ToString("0.00", inv)} opening={Pct(differ[0], total[0])} mid={Pct(differ[1], total[1])} late={Pct(differ[2], total[2])} nodes_ratio={(nodesA == 0 ? 0 : nodesB / (double)nodesA).ToString("0.000", inv)}");
        }

        private static string Pct(int d, int n) => n == 0 ? "na" : (100.0 * d / n).ToString("0.00", CultureInfo.InvariantCulture);

        private static string DescribeAction(int action, BitboardState b, PlayerColor side) {
            if (!ActionCodec.TryDecode(action, b, side, out var m)) return "?";
            return $"({m.FromX},{m.FromY})->({m.ToX},{m.ToY}) {(ActionCodec.IsClone(action) ? "clone" : "jump")}";
        }
    }
}
