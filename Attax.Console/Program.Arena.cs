using System.Diagnostics;
using System.Globalization;
using System.Text;
using Attax.Core;
using Attax.Data;
using Attax.Model;
using Attax.Play;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Console {
    partial class Program {
        private sealed class PlayerStats {
            public long Moves, Nodes, Fallbacks, Ticks, MaxTicks, DepthSum, SearchTicks;
            public void Add(MoveDecision d) {
                Moves++;
                Nodes += d.Nodes;
                if (d.UsedFallback) Fallbacks++;
                long t = (long)(d.Milliseconds / 1000.0 * Stopwatch.Frequency);
                Ticks += t;
                if (d.Nodes > 0) SearchTicks += t;
                if (t > MaxTicks) MaxTicks = t;
                DepthSum += d.Depth;
            }
        }

        private sealed class PairOutcome {
            // Score for player 1 in [0, 2]: win = 1, draw = 0.5 per game, both colours of the same opening.
            public double P1Score;
            public int GameA, GameB;    // +1 p1 win, 0 draw, -1 p1 loss (A: p1 is Red; B: p1 is Blue)
            public int PlyCapGames;
            public int PliesA, PliesB;
            public long NodesP1, NodesP2;
            public int OpeningPlies, Blocked;
        }

        static void RunArena(ArenaOptions o) {
            if (o.Pairs <= 0) throw new ArgumentException("--pairs must be > 0.");
            if (o.OpeningPlies < 0) throw new ArgumentException("--openingPlies must be >= 0.");
            if (o.MaxPlies < 2) throw new ArgumentException("--maxPlies must be >= 2.");
            if (o.MinPairs < 1) throw new ArgumentException("--minPairs must be >= 1.");
            var p1Spec = PlayerSpec.Parse(o.P1);
            var p2Spec = PlayerSpec.Parse(o.P2);
            // Resolve parameter files/overrides now: a bad file or value must fail before any game is played.
            var p1Params = p1Spec.ResolveParams();
            var p2Params = p2Spec.ResolveParams();
            (double elo0, double elo1) sprt = default;
            bool useSprt = o.Sprt != null;
            if (useSprt) sprt = PairStats.ParseSprtBounds(o.Sprt!);
            var (blockMin, blockMax) = StartBoard.ParseRange(o.Blocked);
            int parallel = o.Parallel > 0 ? o.Parallel : Environment.ProcessorCount;
            using var models = CreateModelSource(parallel, p1Spec, p2Spec);

            System.Console.WriteLine($"[arena] pairs<={o.Pairs} (games<={o.Pairs * 2}) openingPlies={o.OpeningPlies} blocked={blockMin}..{blockMax} seed={o.Seed} parallel={parallel} maxPlies={o.MaxPlies}");
            System.Console.WriteLine($"[arena] P1: {PlayerFactory.Describe(p1Spec, false)}");
            System.Console.WriteLine($"[arena] P2: {PlayerFactory.Describe(p2Spec, false)}");
            // What differs between the two players' parameters (nothing prints when both use the same values).
            var diff = p1Params != null && p2Params != null ? EngineParamsFile.Diff(p2Params, p1Params) : new List<string>();   // both complete: base + file + overrides
            if (diff.Count > 0) {
                System.Console.WriteLine($"[arena] engine parameters, P2 -> P1 ({diff.Count} differ):");
                foreach (var d in diff) System.Console.WriteLine("[arena]   " + d);
            } else if (p1Spec.IsSearch && p2Spec.IsSearch) System.Console.WriteLine("[arena] engine parameters: identical for both players.");
            if (useSprt) System.Console.WriteLine($"[arena] sequential test: H0 = Elo {sprt.elo0:0.##}, H1 = Elo {sprt.elo1:0.##}, alpha={o.SprtAlpha}, beta={o.SprtBeta}, no stop before {o.MinPairs} pairs");
            if (o.OpeningPlies % 2 != 0) System.Console.WriteLine("[arena] note: odd --openingPlies gives one colour an extra random move (the same in both games of a pair).");
            if (parallel > 1 && (p1Spec.TimeMs > 0 || p2Spec.TimeMs > 0))
                System.Console.WriteLine("[arena] WARNING: time-limited players run with parallel games: CPU contention makes their effective search depth noisy. Use --parallel 1 for equal-wall-clock comparisons.");

            var pairs = new PairOutcome[o.Pairs];
            var s1 = new PlayerStats(); var s2 = new PlayerStats();
            object statsLock = new();
            var wall = Stopwatch.StartNew();
            int done = 0;
            int finished = 0;                 // pairs [0, finished) are complete and scored
            SprtDecision decision = SprtDecision.Continue;
            double llr = 0, llrLower = 0, llrUpper = 0;
            // Pairs run in index order, in batches. Pair i always uses the same opening and seeds, so a stopped run is exactly a
            // prefix of the full run (stopping never changes any pair's result, only how many pairs are played).
            int batch = useSprt ? Math.Max(parallel, 8) : o.Pairs;

            while (finished < o.Pairs && decision == SprtDecision.Continue) {
                int start = finished, end = Math.Min(o.Pairs, finished + batch);
                Parallel.For(start, end, new ParallelOptions { MaxDegreeOfParallelism = parallel }, pi => {
                    using var referee = NewReferee();
                    // The opening is generated once and played twice, swapping colours: luck of the opening cancels inside a pair.
                    var openRng = new Random(CombineSeed(o.Seed, pi * 7919 + 1));
                    // Blocks first (as in the game), then the random opening plies. Own rng, consulted only when blocks are requested.
                    var opening = StartBoard.Create(referee, blockMin, blockMax, new Random(CombineSeed(o.Seed, pi * 104729 + 7)));
                    opening.ZobristHash = referee.ComputeZobristHash(opening, PlayerColor.Red);
                    int played = 0;
                    for (; played < o.OpeningPlies; played++) {
                        if (referee.IsGameOver(opening)) break;
                        var side = played % 2 == 0 ? PlayerColor.Red : PlayerColor.Blue;
                        var d = RandomDecision(referee, opening, side, openRng);
                        if (!d.HasMove) break;
                        referee.MakeMove(opening, d.Move, side);
                    }

                    var po = new PairOutcome { OpeningPlies = played, Blocked = PopCount(opening.BlockedSquares) };
                    for (int g = 0; g < 2; g++) {
                        bool p1IsRed = g == 0;
                        int gameSeed = CombineSeed(o.Seed, pi * 2 + g);
                        // Each game gets fresh players, so no transposition table or RNG state leaks between the two halves of a pair.
                        using var p1 = PlayerFactory.Create(p1Spec, models, CombineSeed(gameSeed, 0x1111), defaultTrain: false);
                        using var p2 = PlayerFactory.Create(p2Spec, models, CombineSeed(gameSeed, 0x2222), defaultTrain: false);
                        var l1 = new PlayerStats(); var l2 = new PlayerStats();

                        var b = opening.Clone();
                        var end2 = RunGame(referee, b, played, o.MaxPlies,
                            chooser: (ply, side, board) => {
                                bool isP1 = (side == PlayerColor.Red) == p1IsRed;
                                var d = (isP1 ? p1 : p2).Choose(board, side);
                                (isP1 ? l1 : l2).Add(d);
                                return d;
                            });

                        int p1Result = end2.ResultRedPov * (p1IsRed ? 1 : -1);
                        po.P1Score += p1Result > 0 ? 1.0 : p1Result == 0 ? 0.5 : 0.0;
                        if (g == 0) { po.GameA = p1Result; po.PliesA = end2.Plies; } else { po.GameB = p1Result; po.PliesB = end2.Plies; }
                        if (!end2.Terminal) po.PlyCapGames++;
                        po.NodesP1 += l1.Nodes; po.NodesP2 += l2.Nodes;
                        lock (statsLock) { Merge(s1, l1); Merge(s2, l2); }
                    }
                    pairs[pi] = po;
                    int n = Interlocked.Increment(ref done);
                    if (!useSprt && (n % Math.Max(1, Math.Min(25, o.Pairs / 4)) == 0 || n == o.Pairs))
                        lock (statsLock) System.Console.WriteLine($"[arena] {n}/{o.Pairs} pairs, elapsed {wall.Elapsed.TotalSeconds:0.0}s");
                });
                finished = end;

                if (useSprt) {
                    var sofar = pairs.Take(finished).Select(p => p!.P1Score / 2.0).ToArray();
                    var r = PairStats.Sprt(sofar, sprt.elo0, sprt.elo1, o.SprtAlpha, o.SprtBeta);
                    llr = r.llr; llrLower = r.lower; llrUpper = r.upper;
                    var sm = PairStats.Summarize(sofar);
                    System.Console.WriteLine($"[arena] {finished}/{o.Pairs} pairs  score {100 * sm.Mean:0.0}%  LLR {llr:0.00} in ({llrLower:0.00}, {llrUpper:0.00})  elapsed {wall.Elapsed.TotalSeconds:0.0}s");
                    if (finished >= o.MinPairs) decision = r.decision;
                }
            }
            wall.Stop();

            // ---- results ----
            var played2 = pairs.Take(finished).ToArray();
            int games = finished * 2;
            int w1 = 0, w2 = 0, dr = 0, cap = 0;
            int pairWin = 0, pairLoss = 0, pairSplit = 0;
            int p1RedWins = 0, p1RedLoss = 0, p1BlueWins = 0, p1BlueLoss = 0;
            foreach (var p in played2) {
                foreach (int r in new[] { p.GameA, p.GameB }) { if (r > 0) w1++; else if (r < 0) w2++; else dr++; }
                cap += p.PlyCapGames;
                if (p.P1Score > 1.0) pairWin++; else if (p.P1Score < 1.0) pairLoss++; else pairSplit++;
                if (p.GameA > 0) p1RedWins++; else if (p.GameA < 0) p1RedLoss++;
                if (p.GameB > 0) p1BlueWins++; else if (p.GameB < 0) p1BlueLoss++;
            }

            // Pairs are the independent units (the two games of a pair share an opening), so the interval is computed over pairs.
            var scores = played2.Select(p => p.P1Score / 2.0).ToArray();
            var sum = PairStats.Summarize(scores);
            double mean = sum.Mean, lo = sum.Lo, hi = sum.Hi;

            System.Console.WriteLine("[arena] ---- results ----");
            System.Console.WriteLine($"[arena] P1 wins/losses/draws = {w1}/{w2}/{dr} over {games} games ({finished} pairs); score = {100 * mean:0.0}% (95% CI over pairs {100 * lo:0.0}..{100 * hi:0.0}), Elo ~ {sum.Elo:+0;-0;0} ({sum.EloLo:+0;-0;0}..{sum.EloHi:+0;-0;0}), per-pair sd {sum.StdDev:0.000}");
            System.Console.WriteLine($"[arena] pairs: P1 won both={pairWin}, lost both={pairLoss}, split={pairSplit}");
            System.Console.WriteLine($"[arena] by colour: P1 as Red W/L={p1RedWins}/{p1RedLoss}, P1 as Blue W/L={p1BlueWins}/{p1BlueLoss}");
            if (cap > 0) System.Console.WriteLine($"[arena] ply-cap games (decided by piece count, not a terminal result): {cap}");
            // Significant only if the whole interval is strictly on one side of 50%. (A zero-spread result, e.g. every pair split
            // 1-1, gives lo == hi == 50% and is "not significant", not a win for either side.)
            const double eps = 1e-9;
            string verdict;
            if (lo > 0.5 + eps) verdict = "P1 is stronger at 95% over these pairs.";
            else if (hi < 0.5 - eps) verdict = "P2 is stronger at 95% over these pairs.";
            else verdict = "difference is NOT significant at 95%.";
            System.Console.WriteLine("[arena] verdict: " + verdict);
            if (useSprt) {
                string s = decision == SprtDecision.AcceptH1 ? $"H1 accepted: the evidence favours a true difference of Elo {sprt.elo1:0.##} or more for P1 (alpha {o.SprtAlpha})."
                         : decision == SprtDecision.AcceptH0 ? $"H0 accepted: the evidence favours a true difference of Elo {sprt.elo0:0.##} or less for P1 (a difference of Elo {sprt.elo1:0.##} would have been detected, beta {o.SprtBeta})."
                         : $"no decision after {finished} pairs (LLR {llr:0.00} between {llrLower:0.00} and {llrUpper:0.00}); the true difference is too small to settle within --pairs.";
                System.Console.WriteLine("[arena] sequential test: " + s);
            }
            if (finished < 10) System.Console.WriteLine("[arena] note: fewer than 10 pairs; the interval is unreliable (it can collapse to zero width).");
            PrintStats("P1", p1Spec, s1);
            PrintStats("P2", p2Spec, s2);
            if (models != null)
                foreach (var (path, m) in models.All)
                    System.Console.WriteLine($"[arena] model {Path.GetFileName(path)}: calls={m.Calls} positions={m.Positions} {m.MeanMillisecondsPerCall:0.000} ms/call");
            System.Console.WriteLine($"[arena] wall={wall.Elapsed.TotalSeconds:0.0}s ({wall.Elapsed.TotalSeconds / Math.Max(1, finished):0.00}s/pair, {parallel} parallel)");

            if (o.PairsCsv != null) WritePairsCsv(o.PairsCsv, played2);

            // One line a script can parse. Key=value, space separated, stable order.
            var inv = CultureInfo.InvariantCulture;
            string dec = !useSprt ? "none" : decision == SprtDecision.AcceptH1 ? "H1" : decision == SprtDecision.AcceptH0 ? "H0" : "undecided";
            System.Console.WriteLine(string.Join(" ", new[] {
                "RESULT",
                $"pairs={finished}", $"games={games}",
                $"score={mean.ToString("0.0000", inv)}", $"lo={lo.ToString("0.0000", inv)}", $"hi={hi.ToString("0.0000", inv)}",
                $"elo={F(sum.Elo)}", $"elo_lo={F(sum.EloLo)}", $"elo_hi={F(sum.EloHi)}", $"sd={sum.StdDev.ToString("0.0000", inv)}",
                $"wins={w1}", $"losses={w2}", $"draws={dr}", $"plycap={cap}",
                $"sprt={dec}", $"llr={llr.ToString("0.000", inv)}",
                $"p1_nps={Nps(s1)}", $"p2_nps={Nps(s2)}", $"p1_ms={MsPerMove(s1)}", $"p2_ms={MsPerMove(s2)}",
                $"p1_params={(p1Params == null ? "none" : EngineParamsFile.Fingerprint(p1Params))}", $"p2_params={(p2Params == null ? "none" : EngineParamsFile.Fingerprint(p2Params))}",
                $"blocked={blockMin}-{blockMax}", $"seed={o.Seed}", $"wall_s={wall.Elapsed.TotalSeconds.ToString("0.0", inv)}",
            }));
        }

        private static string F(double v) => double.IsInfinity(v) ? (v > 0 ? "inf" : "-inf") : v.ToString("0.0", CultureInfo.InvariantCulture);
        private static string Nps(PlayerStats s) => s.SearchTicks == 0 ? "0" : ((long)(s.Nodes / (s.SearchTicks / (double)Stopwatch.Frequency))).ToString(CultureInfo.InvariantCulture);
        private static string MsPerMove(PlayerStats s) => s.Moves == 0 ? "0" : (Ms(s.Ticks) / s.Moves).ToString("0.00", CultureInfo.InvariantCulture);

        private static void WritePairsCsv(string path, PairOutcome[] pairs) {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var sb = new StringBuilder("pair,opening_plies,blocked,p1_score,game_a_p1_red,game_b_p1_blue,plies_a,plies_b,nodes_p1,nodes_p2\n");
            for (int i = 0; i < pairs.Length; i++) {
                var p = pairs[i];
                sb.Append(i).Append(',').Append(p.OpeningPlies).Append(',').Append(p.Blocked).Append(',').Append((p.P1Score / 2.0).ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
                  .Append(p.GameA).Append(',').Append(p.GameB).Append(',').Append(p.PliesA).Append(',').Append(p.PliesB).Append(',')
                  .Append(p.NodesP1).Append(',').Append(p.NodesP2).Append('\n');
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            System.Console.WriteLine($"[arena] wrote {pairs.Length} pair rows to {path}");
        }

        private static void Merge(PlayerStats into, PlayerStats from) {
            into.Moves += from.Moves; into.Nodes += from.Nodes; into.Fallbacks += from.Fallbacks; into.Ticks += from.Ticks;
            into.DepthSum += from.DepthSum; into.SearchTicks += from.SearchTicks;
            if (from.MaxTicks > into.MaxTicks) into.MaxTicks = from.MaxTicks;
        }

        private static void PrintStats(string label, PlayerSpec spec, PlayerStats s) {
            if (s.Moves == 0) { System.Console.WriteLine($"[arena] {label} ({spec}): no moves"); return; }
            double avgMs = Ms(s.Ticks) / s.Moves;
            string search = spec.IsSearch ? $", avg nodes/move={s.Nodes / (double)s.Moves:0.#}, {Nps(s)} nodes/s, avg completed depth={s.DepthSum / (double)s.Moves:0.00}, budget-fallbacks={s.Fallbacks}" : "";
            System.Console.WriteLine($"[arena] {label} ({spec}): moves={s.Moves}, avg {avgMs:0.000} ms/move, max {Ms(s.MaxTicks):0.0} ms{search}");
        }
    }
}
