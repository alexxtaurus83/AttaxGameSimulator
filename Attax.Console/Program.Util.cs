using System;
using System.Collections.Generic;
using System.Linq;
using Attax.Core;
using Attax.Play;

namespace Attax.Console {
    // Small shared helpers used by the verbs.
    partial class Program {
        internal static int CombineSeed(int baseSeed, int salt) {
            unchecked {
                int mixed = baseSeed;
                mixed ^= salt + (int)0x9E3779B9 + (mixed << 6) + (mixed >> 2);
                return mixed;
            }
        }

        /// <summary>Stable 64-bit game identity from (generation, seed, index); splitmix64 mixing.</summary>
        internal static ulong MakeGameUid(int generation, int seed, int index) {
            unchecked {
                ulong z = ((ulong)(uint)generation << 40) ^ ((ulong)(uint)seed << 8) ^ ((ulong)(uint)index * 0x9E3779B97F4A7C15UL);
                z += 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        internal static double GetScheduledEpsilon(int ply, int ply1, int ply2, double start, double mid, double late) {
            if (ply <= ply1) return Clamp01(start);
            if (ply <= ply2) return Clamp01(mid);
            return Clamp01(late);
        }

        internal static double Clamp01(double v) => v < 0.0 ? 0.0 : v > 1.0 ? 1.0 : v;

        internal static void SetBoardBit(ref ulong bits, int x, int y) {
            bits |= 1UL << (y * AttaxConstants.BaseConst.BoardSize + x);
        }

        internal static BitboardState CreateStandardInitialBoard() {
            var board = new BitboardState();
            int n = AttaxConstants.BaseConst.BoardSize;
            SetBoardBit(ref board.RedPieces, 0, 0);
            SetBoardBit(ref board.RedPieces, n - 1, n - 1);
            SetBoardBit(ref board.BluePieces, 0, n - 1);
            SetBoardBit(ref board.BluePieces, n - 1, 0);
            return board;
        }

        internal static BitboardState ApplySymmetryToBoard(BitboardState board, int symmetryIndex) {
            ulong red = board.RedPieces, blue = board.BluePieces, blocked = board.BlockedSquares;
            BitboardOps.TransformState(ref red, ref blue, ref blocked, symmetryIndex);
            return new BitboardState { RedPieces = red, BluePieces = blue, BlockedSquares = blocked, ZobristHash = 0UL };
        }

        internal static Attax.Data.LogV3.PlayerMode ToLogMode(Attax.Play.PlayerKind kind) {
            switch (kind) {
                case Attax.Play.PlayerKind.Classic: return Attax.Data.LogV3.PlayerMode.Classic;
                case Attax.Play.PlayerKind.Value: return Attax.Data.LogV3.PlayerMode.Value;
                case Attax.Play.PlayerKind.Policy: return Attax.Data.LogV3.PlayerMode.Policy;
                default: return Attax.Data.LogV3.PlayerMode.Random;
            }
        }

        internal static int GetPercentile(double[] sorted, double p) {
            if (sorted.Length == 0) return 0;
            int index = (int)Math.Round(Math.Clamp(p, 0.0, 1.0) * (sorted.Length - 1), MidpointRounding.AwayFromZero);
            return (int)sorted[index];
        }

        static void RunValidateHash(ValidateHashOptions opts) {
            int games = opts.Games, seed = opts.Seed, maxMoves = opts.Moves;
            bool strict = opts.Strict;
            var rng = new Random(seed);
            var engine = new AtaxxAIEngine(EngineParamsFile.LoadBase(), new AtaxxAIEngine.AIEngineConfig {
                UseTimeManagement = false, AiDepth = 1, TrainingMode = false, DisableParallelRootSearch = true
            });   // hashing and move generation only

            int mismatches = 0, positionsChecked = 0;
            void Check(string kind, int g, int ply, AtaxxAIEngine.PlayerColor side, BitboardState board, ulong expected) {
                positionsChecked++;
                if (board.ZobristHash == expected) return;
                mismatches++;
                string msg = $"{kind} mismatch at game={g}, ply={ply}, side={side}: board={board.ZobristHash}, expected={expected}";
                if (strict) throw new InvalidOperationException(msg);
                System.Console.WriteLine($"WARN: {msg}");
                board.ZobristHash = expected;
            }

            try {
                for (int g = 0; g < games; g++) {
                    var board = CreateStandardInitialBoard();
                    var side = AtaxxAIEngine.PlayerColor.Red;
                    board.ZobristHash = engine.ComputeZobristHash(board, side);

                    for (int ply = 0; ply < maxMoves; ply++) {
                        Check("Hash", g, ply, side, board, engine.ComputeZobristHash(board, side));

                        var moves = engine.GetAllValidMoves(board, side);
                        if (moves.Count == 0) break;   // the game is over; there are no passes

                        var move = moves[rng.Next(moves.Count)];
                        var undo = engine.MakeMoveFast(board, move, side);
                        Check("Post-move", g, ply, side, board, engine.ComputeZobristHash(board, AtaxxAIEngine.SwitchPlayer(side)));

                        engine.UnmakeMove(board, move, side, undo);
                        Check("Unmake", g, ply, side, board, undo.PreviousZobristHash);

                        engine.MakeMoveFast(board, move, side);
                        side = AtaxxAIEngine.SwitchPlayer(side);
                        if (engine.IsGameOver(board)) break;
                    }
                }
                System.Console.WriteLine($"Hash validation complete. Checked={positionsChecked}, mismatches={mismatches}, strict={(strict ? 1 : 0)}");
            } finally {
                engine.Dispose();
            }
        }
    }

    /// <summary>
    /// Chooses which positions of a game to keep. samplesPerGame == 0 keeps every position; otherwise a phase-aware
    /// reservoir (20% early / 60% mid / 20% late by piece count) with a mobility bias toward positions with real choices.
    /// </summary>
    internal sealed class PositionSampler {
        internal sealed class Observed {
            public ushort Ply;
            public BitboardState Board;
            public AtaxxAIEngine.PlayerColor Side;
            public int PlayedAction;
            public bool PlayedRandom;
            /// <summary>Action a teacher search would return for this exact position, known for free; -1 if unknown.</summary>
            public int ReusableTeacherAction = -1;
            /// <summary>Search value that comes with <see cref="ReusableTeacherAction"/>; only meaningful when HasReusableScore.</summary>
            public bool HasReusableScore;
            public int ReusableTeacherScore;
        }

        private const int EarlyMaxPieces = 14, MidMaxPieces = 35;
        private readonly int target;
        private readonly Random rng;
        private readonly List<Observed> all = new List<Observed>();
        private readonly List<Observed>[] buckets = { new List<Observed>(), new List<Observed>(), new List<Observed>() };
        private readonly int[] quota = new int[3];
        private readonly int[] seen = new int[3];

        public PositionSampler(int samplesPerGame, Random rng) {
            target = Math.Max(0, samplesPerGame);
            this.rng = rng;
            int early = (int)Math.Round(target * 0.20, MidpointRounding.AwayFromZero);
            int late = (int)Math.Round(target * 0.20, MidpointRounding.AwayFromZero);
            quota[0] = early; quota[2] = late; quota[1] = Math.Max(0, target - early - late);
        }

        public void Observe(Observed o, int legalMoveCount) {
            if (target == 0) { all.Add(o); return; }
            if (!AcceptByMobility(legalMoveCount)) return;
            all.Add(o);
            int pieces = AtaxxAIEngine.PopCount(o.Board.RedPieces) + AtaxxAIEngine.PopCount(o.Board.BluePieces);
            int b = pieces <= EarlyMaxPieces ? 0 : pieces <= MidMaxPieces ? 1 : 2;
            if (quota[b] <= 0) return;
            seen[b]++;
            if (buckets[b].Count < quota[b]) { buckets[b].Add(o); return; }
            int pick = rng.Next(seen[b]);
            if (pick < quota[b]) buckets[b][pick] = o;
        }

        public List<Observed> GetFinal() {
            if (target == 0) return all;
            var selected = new Dictionary<ushort, Observed>();
            foreach (var bucket in buckets) foreach (var o in bucket) selected[o.Ply] = o;
            // Top up from the remaining accepted positions so short games still yield samples.
            foreach (var o in all.OrderBy(p => p.Ply)) {
                if (selected.Count >= target) break;
                if (!selected.ContainsKey(o.Ply)) selected[o.Ply] = o;
            }
            return selected.Values.OrderBy(p => p.Ply).Take(target).ToList();
        }

        private bool AcceptByMobility(int moves) {
            const int low = 6, high = 16;
            const double lowAccept = 0.25;
            double p = moves >= high ? 1.0 : moves <= low ? lowAccept : lowAccept + (1.0 - lowAccept) * (moves - low) / (high - low);
            return rng.NextDouble() <= p;
        }
    }
}
