using System;
using System.Collections.Generic;
using Attax.Core;
using Xunit;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core.Tests {
    // Goldens recorded on the engine BEFORE the EngineParams refactor (Stage 2 branch, constants still compiled in).
    // They cover what ClassicRegressionTests does not: raw evaluator output, the per-root-move score breakdown (strategic,
    // bonus, final, which exercises the aggression table and every root bonus), and NORMAL mode with quiescence at the
    // depths the game ships with. Default EngineParams must reproduce every number bit for bit.
    public class RefactorGoldenTests {
        internal static ulong Fnv(ulong h, long v) {
            unchecked {
                for (int i = 0; i < 8; i++) { h ^= (ulong)((v >> (i * 8)) & 0xFF); h *= 1099511628211UL; }
            }
            return h;
        }

        internal static List<(BitboardState b, PlayerColor side)> RandomPositions(int count, int seed) {
            var rng = new Random(seed);
            var list = new List<(BitboardState, PlayerColor)>();
            var referee = TestBoards.NewEngine();
            while (list.Count < count) {
                var b = new BitboardState { RedPieces = (1UL << 0) | (1UL << 48), BluePieces = (1UL << 6) | (1UL << 42) };
                int blocks = rng.Next(0, 4);
                for (int k = 0; k < blocks; k++) {
                    int sq = rng.Next(49);
                    ulong bit = 1UL << sq;
                    if (((b.RedPieces | b.BluePieces | b.BlockedSquares) & bit) == 0) b.BlockedSquares |= bit;
                }
                var side = PlayerColor.Red;
                int plies = rng.Next(0, 90);
                for (int p = 0; p < plies && !referee.IsGameOver(b); p++) {
                    var legal = referee.GetAllValidMoves(b, side);
                    if (legal.Count == 0) break;
                    referee.MakeMove(b, legal[rng.Next(legal.Count)], side);
                    side = SwitchPlayer(side);
                }
                if (referee.IsGameOver(b)) continue;
                list.Add((b, side));
            }
            return list;
        }

        [Fact]
        public void Evaluator_OutputIsUnchanged() {
            var ev = Prm.Eval();
            ulong h = 14695981039346656037UL;
            foreach (var (b, side) in RandomPositions(4000, 31337)) {
                h = Fnv(h, BitConverter.SingleToInt32Bits(ev.Evaluate(b, side)));
                h = Fnv(h, BitConverter.SingleToInt32Bits(ev.Evaluate(b, SwitchPlayer(side))));
            }
            Assert.Equal(GoldenEvaluator, h);
        }

        // Plays fixed-seed games and digests the chosen move, node counts and every root move's score breakdown.
        internal static ulong PlayDigest(bool training, bool quiescence, int depth, int games, int searchedMoves, out int moves, Func<AIEngineConfig, AIEngineConfig> tweak = null) {
            ulong h = 14695981039346656037UL;
            moves = 0;
            for (int g = 0; g < games; g++) {
                var rng = new Random(4200 + g);
                var cfg = new AIEngineConfig { DisableRandomRootTies = true, AiDepth = depth, TrainingMode = training, DisableParallelRootSearch = true, DisableQuiescenceSearch = !quiescence, Seed = 5 + g };
                if (tweak != null) cfg = tweak(cfg);
                var red = new AtaxxAIEngine(Prm.Base(), cfg) { CollectRootScores = true };
                var blue = new AtaxxAIEngine(Prm.Base(), cfg) { CollectRootScores = true };
                var board = new BitboardState { RedPieces = (1UL << 0) | (1UL << 48), BluePieces = (1UL << 6) | (1UL << 42) };
                var side = PlayerColor.Red;
                int searched = 0;
                for (int ply = 0; ply < 400 && searched < searchedMoves; ply++) {
                    var e = side == PlayerColor.Red ? red : blue;
                    e.AIPlayerColor = side;
                    var legal = e.GetAllValidMoves(board, side);
                    if (legal.Count == 0) break;
                    Move m;
                    if (ply < 6) {
                        m = legal[rng.Next(legal.Count)];
                    } else {
                        TestBoards.SetBoard(e, board.Clone(), side);
                        m = e.GetBestMove(side);
                        h = Fnv(h, e.LastSearchStats.Nodes + e.LastSearchStats.QNodes);
                        foreach (var s in e.LastRootScores ?? new List<RootScore>()) {
                            h = Fnv(h, s.Move.FromX * 1000 + s.Move.FromY * 100 + s.Move.ToX * 10 + s.Move.ToY);
                            h = Fnv(h, s.Strategic); h = Fnv(h, s.Bonus); h = Fnv(h, s.Final);
                        }
                        searched++; moves++;
                    }
                    h = Fnv(h, m.FromX * 1000 + m.FromY * 100 + m.ToX * 10 + m.ToY);
                    red.MakeMove(board, m, side);
                    side = SwitchPlayer(side);
                    if (red.IsGameOver(board)) break;
                }
                red.Dispose(); blue.Dispose();
            }
            return h;
        }

        [Fact]
        public void Depth3_NormalMode_WithQuiescence_IsUnchanged() {
            ulong d = PlayDigest(training: false, quiescence: true, depth: 3, games: 3, searchedMoves: 22, out int moves);
            Assert.Equal(GoldenQ3Moves, moves);
            Assert.Equal(GoldenQ3, d);
        }

        [Fact]
        public void Depth3_NormalMode_NoQuiescence_RootScores_AreUnchanged() {
            ulong d = PlayDigest(training: false, quiescence: false, depth: 3, games: 3, searchedMoves: 22, out int moves);
            Assert.Equal(GoldenN3Moves, moves);
            Assert.Equal(GoldenN3, d);
        }

        [Fact]
        public void Depth4_NormalMode_WithQuiescence_IsUnchanged() {
            ulong d = PlayDigest(training: false, quiescence: true, depth: 4, games: 2, searchedMoves: 10, out int moves);
            Assert.Equal(GoldenQ4Moves, moves);
            Assert.Equal(GoldenQ4, d);
        }

        [Fact]
        public void Depth2_TrainingMode_RootScores_AreUnchanged() {
            ulong d = PlayDigest(training: true, quiescence: false, depth: 2, games: 4, searchedMoves: 30, out int moves);
            Assert.Equal(GoldenT2Moves, moves);
            Assert.Equal(GoldenT2, d);
        }

        // Recorded on the pre-refactor engine; each value was produced twice by separate runs (move counts first, then digests).
        private const ulong GoldenEvaluator = 7784564918201625077UL;
        private const int GoldenQ3Moves = 46;   private const ulong GoldenQ3 = 6257277099749903707UL;
        private const int GoldenN3Moves = 48;   private const ulong GoldenN3 = 7691778144363880046UL;
        private const int GoldenQ4Moves = 20;   private const ulong GoldenQ4 = 8929830059009135482UL;
        private const int GoldenT2Moves = 120;   private const ulong GoldenT2 = 2489444682808646792UL;
    }
}
