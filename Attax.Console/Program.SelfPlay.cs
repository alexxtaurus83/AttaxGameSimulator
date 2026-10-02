using System.Diagnostics;
using Attax.Core;
using Attax.Model;
using Attax.Data;
using Attax.Play;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Console {
    partial class Program {
        private const byte InlineTeacherId = 1;

        static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        static void RunSelfPlay(SelfPlayOptions o) {
            if (o.Games <= 0) throw new ArgumentException("--games must be > 0.");
            if (o.EpsilonPly1 > o.EpsilonPly2) throw new ArgumentException("--epsilonPly1 must be <= --epsilonPly2.");
            if (o.TeacherFraction < 0 || o.TeacherFraction > 1) throw new ArgumentException("--teacherFraction must be in [0,1].");
            if (o.MaxPlies < 2) throw new ArgumentException("--maxPlies must be >= 2.");
            if (o.Samples < 0) throw new ArgumentException("--samples must be >= 0.");
            var (blockMin, blockMax) = StartBoard.ParseRange(o.Blocked);

            var redSpec = PlayerSpec.Parse(o.Red);
            var blueSpec = PlayerSpec.Parse(o.Blue ?? o.Red);
            var teacherSpec = o.Teacher == null ? null : ParseTeacher(o.Teacher, o.TeacherRandomTies);
            int parallel = o.Parallel > 0 ? o.Parallel : Environment.ProcessorCount;
            // Self-play search defaults to training mode (no null-move / quiescence), as the classic generator always did.
            using var models = CreateModelSource(parallel, redSpec, blueSpec, teacherSpec);

            System.Console.WriteLine($"[selfplay] games={o.Games} seed={o.Seed} generation={o.Generation} parallel={parallel} out={o.Out}");
            System.Console.WriteLine($"[selfplay] red  : {redSpec}");
            System.Console.WriteLine($"[selfplay] blue : {blueSpec}");
            System.Console.WriteLine($"[selfplay] teacher: {(teacherSpec == null ? "none" : teacherSpec.ToString())} fraction={o.TeacherFraction:0.###}");
            System.Console.WriteLine($"[selfplay] samples/game={(o.Samples == 0 ? "all" : o.Samples.ToString())} symmetry={o.Symmetry} epsilon=[{o.EpsilonStart:0.###},{o.EpsilonMid:0.###},{o.EpsilonLate:0.###}]@ply[{o.EpsilonPly1},{o.EpsilonPly2}] maxPlies={o.MaxPlies} blocked={blockMin}..{blockMax}");

            using var writer = new LogV3Writer(o.Out);
            if (teacherSpec != null) writer.DeclareTeacher(MakeTeacherConfig(InlineTeacherId, teacherSpec));

            long rolloutTicks = 0, teacherTicks = 0, writeTicks = 0;
            long plies = 0, positions = 0, labels = 0, freeLabels = 0, searchedLabels = 0, randomMoves = 0, plyCapGames = 0;
            long redWins = 0, blueWins = 0, drawGames = 0;
            long nodes = 0, searchMoves = 0, fallbacks = 0, teacherNodes = 0;
            int done = 0;
            var wall = Stopwatch.StartNew();
            object progressLock = new();

            Parallel.For(0, o.Games, new ParallelOptions { MaxDegreeOfParallelism = parallel }, i => {
                int gameSeed = CombineSeed(o.Seed, i);
                ulong uid = MakeGameUid(o.Generation, o.Seed, i);
                var rng = new Random(gameSeed);
                using var referee = NewReferee();
                // Movers keep their root scores only when a heuristic teacher exists: a free label then carries the mover's own search value.
                bool keepScores = teacherSpec != null && teacherSpec.Kind == PlayerKind.Classic;
                using var red = PlayerFactory.Create(redSpec, models, CombineSeed(gameSeed, 0x13579BDF), defaultTrain: true, collectScore: keepScores);
                using var blue = PlayerFactory.Create(blueSpec, models, CombineSeed(gameSeed, 0x2468ACE0), defaultTrain: true, collectScore: keepScores);
                using var teacher = teacherSpec == null ? null : PlayerFactory.Create(teacherSpec, models, CombineSeed(gameSeed, 0x5A5A5A5A), defaultTrain: true, collectScore: keepScores);
                var sampler = new PositionSampler(o.Samples, new Random(CombineSeed(gameSeed, 0x5BD1E995)));

                long gRolloutStart = Stopwatch.GetTimestamp();
                long gTeacherTicks = 0, gNodes = 0, gSearchMoves = 0, gFallbacks = 0, gRandom = 0, gTeacherNodes = 0;

                // Blocks as in the game; own rng consulted only when blocks are requested, so 0,0 keeps every other stream unchanged.
                var board = StartBoard.Create(referee, blockMin, blockMax, new Random(CombineSeed(gameSeed, 0xB10C)));
                board.ZobristHash = referee.ComputeZobristHash(board, PlayerColor.Red);

                var end = RunGame(referee, board, 0, o.MaxPlies,
                    chooser: (ply, side, b) => {
                        double eps = GetScheduledEpsilon(ply, o.EpsilonPly1, o.EpsilonPly2, o.EpsilonStart, o.EpsilonMid, o.EpsilonLate);
                        if (rng.NextDouble() < eps) { gRandom++; return RandomDecision(referee, b, side, rng); }
                        var d = (side == PlayerColor.Red ? red : blue).Choose(b, side);
                        if (d.IsExploration) gRandom++;
                        if (d.Nodes > 0) { gNodes += d.Nodes; gSearchMoves++; if (d.UsedFallback) gFallbacks++; }
                        return d;
                    },
                    onMove: (ply, side, b, d) => {
                        var obs = new PositionSampler.Observed { Ply = (ushort)ply, Board = b.Clone(), Side = side, PlayedAction = d.Action, PlayedRandom = d.IsExploration };
                        // The mover already ran exactly the teacher's greedy search on exactly this position: reuse it.
                        var mover = side == PlayerColor.Red ? redSpec : blueSpec;
                        if (teacherSpec != null && !d.IsExploration && mover.IsSearch && mover.Temp <= 0f && mover.TopK <= 1
                            && mover.SearchKey() == teacherSpec.SearchKey())
                        {
                            obs.ReusableTeacherAction = d.Action;
                            // The score is only trusted from a heuristic search that finished its real depth (a budget fallback is a depth-1 value).
                            obs.HasReusableScore = d.HasScore && !d.UsedFallback && teacherSpec.Kind == PlayerKind.Classic;
                            obs.ReusableTeacherScore = d.Score;
                        }
                        sampler.Observe(obs, d.LegalCount);
                    });

                long rolloutDone = Stopwatch.GetTimestamp();
                var kept = sampler.GetFinal();
                var samples = new List<LogV3.Sample>(kept.Count * (o.Symmetry == SymmetryModeOption.All ? 8 : 1));
                long gLabels = 0, gFree = 0, gSearched = 0;

                foreach (var k in kept) {
                    int teacherAction = LogV3.NoAction;
                    bool hasScore = false; int teacherScore = 0;
                    if (teacher != null && rng.NextDouble() < o.TeacherFraction) {
                        if (k.ReusableTeacherAction >= 0) { teacherAction = k.ReusableTeacherAction; hasScore = k.HasReusableScore; teacherScore = k.ReusableTeacherScore; gFree++; }
                        else {
                            long t0 = Stopwatch.GetTimestamp();
                            var td = teacher.Choose(k.Board, k.Side);
                            gTeacherTicks += Stopwatch.GetTimestamp() - t0;
                            if (!td.HasMove) throw new InvalidOperationException("Teacher found no move for a position the mover could play.");
                            teacherAction = td.Action;
                            hasScore = td.HasScore && !td.UsedFallback && teacherSpec!.Kind == PlayerKind.Classic;
                            teacherScore = td.Score;
                            gTeacherNodes += td.Nodes;
                            gSearched++;
                        }
                        gLabels++;
                    }

                    IEnumerable<int> syms = o.Symmetry == SymmetryModeOption.None ? new[] { 0 }
                        : o.Symmetry == SymmetryModeOption.Random ? new[] { rng.Next(8) } : Enumerable.Range(0, 8);
                    foreach (int sym in syms) {
                        var tb = sym == 0 ? k.Board : ApplySymmetryToBoard(k.Board, sym);
                        byte flags = 0;
                        if (k.PlayedRandom) flags |= LogV3.FlagPlayedRandom;
                        if (teacherAction >= 0) flags |= LogV3.FlagTeacherValid;
                        if (teacherAction >= 0 && hasScore) flags |= LogV3.FlagTeacherScoreValid;
                        samples.Add(new LogV3.Sample {
                            GameUid = uid, Ply = k.Ply, Red = tb.RedPieces, Blue = tb.BluePieces, Blocked = tb.BlockedSquares,
                            Side = (byte)(k.Side == PlayerColor.Red ? 0 : 1),
                            PlayedAction = (short)ActionCodec.TransformAction(k.PlayedAction, sym),
                            TeacherAction = (short)(teacherAction >= 0 ? ActionCodec.TransformAction(teacherAction, sym) : LogV3.NoAction),
                            Flags = flags, Symmetry = (byte)sym, TeacherId = (byte)(teacherAction >= 0 ? InlineTeacherId : 0),
                            TeacherScore = teacherAction >= 0 && hasScore ? teacherScore : 0,   // a position value: identical for every symmetry copy
                        });
                    }
                }

                long tw0 = Stopwatch.GetTimestamp();
                writer.WriteGame(
                    new LogV3.GameHeader { GameUid = uid, Seed = (ulong)(uint)gameSeed, Board = LogV3.BoardSize, Generation = (ushort)o.Generation, RedMode = ToLogMode(redSpec.Kind), BlueMode = ToLogMode(blueSpec.Kind) },
                    samples,
                    new LogV3.GameResult { GameUid = uid, ResultRedPov = (sbyte)end.ResultRedPov, TotalPlies = (ushort)end.Plies, Termination = end.Terminal ? LogV3.Termination.Terminal : LogV3.Termination.PlyCap });
                long tw1 = Stopwatch.GetTimestamp();

                // Teacher labelling happens after rolloutDone, so rollout and teacher time are disjoint.
                Interlocked.Add(ref rolloutTicks, rolloutDone - gRolloutStart);
                Interlocked.Add(ref teacherTicks, gTeacherTicks);
                Interlocked.Add(ref writeTicks, tw1 - tw0);
                Interlocked.Add(ref plies, end.Plies);
                Interlocked.Add(ref positions, samples.Count);
                Interlocked.Add(ref labels, gLabels);
                Interlocked.Add(ref freeLabels, gFree);
                Interlocked.Add(ref searchedLabels, gSearched);
                Interlocked.Add(ref randomMoves, gRandom);
                Interlocked.Add(ref nodes, gNodes);
                Interlocked.Add(ref searchMoves, gSearchMoves);
                Interlocked.Add(ref fallbacks, gFallbacks);
                Interlocked.Add(ref teacherNodes, gTeacherNodes);
                if (!end.Terminal) Interlocked.Increment(ref plyCapGames);
                if (end.ResultRedPov > 0) Interlocked.Increment(ref redWins);
                else if (end.ResultRedPov < 0) Interlocked.Increment(ref blueWins);
                else Interlocked.Increment(ref drawGames);

                int n = Interlocked.Increment(ref done);
                if (n % Math.Max(1, Math.Min(25, o.Games / 4)) == 0 || n == o.Games) {
                    lock (progressLock)
                        System.Console.WriteLine($"[selfplay] {n}/{o.Games} games, {n / wall.Elapsed.TotalSeconds:0.0} games/s, elapsed {wall.Elapsed.TotalSeconds:0.0}s");
                }
            });
            wall.Stop();

            double secs = wall.Elapsed.TotalSeconds;
            double workers = Math.Min(parallel, o.Games);
            System.Console.WriteLine("[selfplay] ---- summary ----");
            System.Console.WriteLine($"[selfplay] games={o.Games} plies={plies} (avg {plies / (double)o.Games:0.#}) positions={positions} labels={labels} (free={freeLabels}, searched={searchedLabels})");
            System.Console.WriteLine($"[selfplay] results red/blue/draw={redWins}/{blueWins}/{drawGames}, ply-cap games={plyCapGames}, random-played moves={randomMoves} ({100.0 * randomMoves / Math.Max(1, plies):0.0}% of plies)");
            System.Console.WriteLine($"[selfplay] wall={secs:0.00}s  games/h={o.Games / secs * 3600:0}  positions/h={positions / secs * 3600:0}  labeled/h={labels / secs * 3600:0}");
            System.Console.WriteLine($"[selfplay] thread-time (sum over workers): rollout={Ms(rolloutTicks) / 1000:0.00}s  teacher={Ms(teacherTicks) / 1000:0.00}s  write={Ms(writeTicks) / 1000:0.00}s  (workers={workers:0})");
            System.Console.WriteLine($"[selfplay] search: moves={searchMoves} avg nodes/move={(searchMoves == 0 ? 0 : nodes / (double)searchMoves):0.#} budget-fallbacks={fallbacks}; teacher nodes={teacherNodes} ({(searchedLabels == 0 ? 0 : teacherNodes / (double)searchedLabels):0.#}/label)");
            if (models != null)
                foreach (var (path, m) in models.All)
                    System.Console.WriteLine($"[selfplay] model {Path.GetFileName(path)}: calls={m.Calls} positions={m.Positions} inference={m.TotalMilliseconds / 1000:0.00}s thread-time, {m.MeanMillisecondsPerCall:0.000} ms/call, {(m.Positions == 0 ? 0 : m.TotalMilliseconds / m.Positions):0.000} ms/position");
        }

        // ------------------------------------------------------------------------------------------------
        // Offline teacher review of an existing log
        // ------------------------------------------------------------------------------------------------

        static void RunRelabel(RelabelOptions o) {
            if (o.Fraction < 0 || o.Fraction > 1) throw new ArgumentException("--fraction must be in [0,1].");
            if (string.Equals(Path.GetFullPath(o.In), Path.GetFullPath(o.Out), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("--out must differ from --in (relabeling never rewrites a log in place).");
            var teacherSpec = ParseTeacher(o.Teacher, o.TeacherRandomTies);
            int parallel = o.Parallel > 0 ? o.Parallel : Environment.ProcessorCount;
            using var models = CreateModelSource(parallel, teacherSpec);

            // Pass 1: learn the teachers already declared so the new one gets a free id (and an identical one is reused).
            var scan = new LogV3Reader();
            foreach (var _ in scan.ReadGames(o.In, semanticChecks: false)) { }
            var newCfg = MakeTeacherConfig(0, teacherSpec);
            byte teacherId = 0;
            foreach (var t in scan.Teachers.Values)
                if (t.Kind == newCfg.Kind && t.Depth == newCfg.Depth && t.NodeBudget == newCfg.NodeBudget && t.RootBonusScale == newCfg.RootBonusScale && t.Flags == newCfg.Flags && t.ModelHash == newCfg.ModelHash)
                    teacherId = t.Id;
            if (teacherId == 0) {
                int next = scan.Teachers.Count == 0 ? 1 : scan.Teachers.Keys.Max() + 1;
                if (next > 255) throw new InvalidOperationException("No free teacher id left (max 255).");
                teacherId = (byte)next;
            }
            newCfg.Id = teacherId;

            System.Console.WriteLine($"[relabel] in={o.In} out={o.Out} teacher={teacherSpec} id={teacherId} fraction={o.Fraction:0.###} parallel={parallel}");

            var reader = new LogV3Reader();
            using var writer = new LogV3Writer(o.Out);
            writer.DeclareTeacherIfMissing(newCfg);

            long gamesDone = 0, reviewed = 0, alreadyLabeled = 0, newLabels = 0, teacherTicks = 0, teacherNodes = 0;
            var wall = Stopwatch.StartNew();
            var batch = new List<LogV3.Game>();

            void Flush() {
                if (batch.Count == 0) return;
                var results = new (LogV3.Game game, LogV3.Sample[] samples)[batch.Count];
                Parallel.For(0, batch.Count, new ParallelOptions { MaxDegreeOfParallelism = parallel }, bi => {
                    var g = batch[bi];
                    var rng = new Random(CombineSeed(o.Seed, unchecked((int)(g.Header.GameUid ^ (g.Header.GameUid >> 32)))));
                    using var teacher = PlayerFactory.Create(teacherSpec, models, CombineSeed(o.Seed, bi), defaultTrain: true, collectScore: teacherSpec.Kind == PlayerKind.Classic);
                    var outSamples = (LogV3.Sample[])g.Samples.Clone();
                    long ticks = 0, nodes = 0, made = 0, had = 0, seen = 0;
                    for (int si = 0; si < outSamples.Length; si++) {
                        if (outSamples[si].TeacherValid) { had++; continue; }
                        if (rng.NextDouble() >= o.Fraction) continue;
                        seen++;
                        var s = outSamples[si];
                        long t0 = Stopwatch.GetTimestamp();
                        var d = teacher.Choose(LogV3.ToBoard(s), LogV3.SideOf(s));
                        ticks += Stopwatch.GetTimestamp() - t0;
                        if (!d.HasMove) throw new InvalidOperationException($"Teacher found no move for game {g.Header.GameUid} sample {si}.");
                        nodes += d.Nodes;
                        s.TeacherAction = (short)d.Action;
                        s.TeacherId = teacherId;
                        s.Flags |= LogV3.FlagTeacherValid;
                        if (d.HasScore && !d.UsedFallback && teacherSpec.Kind == PlayerKind.Classic) { s.Flags |= LogV3.FlagTeacherScoreValid; s.TeacherScore = d.Score; }
                        outSamples[si] = s;
                        made++;
                    }
                    Interlocked.Add(ref teacherTicks, ticks);
                    Interlocked.Add(ref teacherNodes, nodes);
                    Interlocked.Add(ref newLabels, made);
                    Interlocked.Add(ref alreadyLabeled, had);
                    Interlocked.Add(ref reviewed, seen);
                    results[bi] = (g, outSamples);
                });
                foreach (var (g, samples) in results) {
                    foreach (var t in reader.Teachers.Values) writer.DeclareTeacherIfMissing(t);
                    writer.WriteGame(g.Header, samples, g.Result);
                    gamesDone++;
                }
                batch.Clear();
                System.Console.WriteLine($"[relabel] {gamesDone} games, {newLabels} new labels, {wall.Elapsed.TotalSeconds:0.0}s");
            }

            foreach (var g in reader.ReadGames(o.In)) {
                batch.Add(g);
                if (batch.Count >= Math.Max(4, parallel * 4)) Flush();
            }
            Flush();
            wall.Stop();

            System.Console.WriteLine("[relabel] ---- summary ----");
            System.Console.WriteLine($"[relabel] games={gamesDone} kept-existing-labels={alreadyLabeled} reviewed={reviewed} new-labels={newLabels}");
            System.Console.WriteLine($"[relabel] wall={wall.Elapsed.TotalSeconds:0.00}s labels/h={newLabels / Math.Max(1e-9, wall.Elapsed.TotalSeconds) * 3600:0} teacher thread-time={Ms(teacherTicks) / 1000:0.00}s avg nodes/label={(newLabels == 0 ? 0 : teacherNodes / (double)newLabels):0.#}");
            if (models != null)
                foreach (var (path, m) in models.All)
                    System.Console.WriteLine($"[relabel] model {Path.GetFileName(path)}: calls={m.Calls} positions={m.Positions} {m.MeanMillisecondsPerCall:0.000} ms/call");
        }
    }
}
