using System;
using System.Collections.Generic;
using Attax.Core;
using Xunit;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core.Tests {
    // Guards the accepted Stage 1 classic (non-model) behaviour. The digests were recorded on the Stage 1 commit
    // (3d2c730) before any Stage 2 engine change. Any new engine behaviour must be opt-in; if one of these digests
    // changes, classic play changed, which is only acceptable for a demonstrated bug fix.
    public class ClassicRegressionTests {
        private static ulong Fnv(ulong h, long v) {
            unchecked {
                for (int i = 0; i < 8; i++) {
                    h ^= (ulong)((v >> (i * 8)) & 0xFF);
                    h *= 1099511628211UL;
                }
            }
            return h;
        }

        // Plays fixed-seed games (random first plies, then engine moves) and digests every move and node count.
        private static ulong PlayDigest(bool training, int depth, long? maxNodes, int games, out int moves) {
            ulong h = 14695981039346656037UL;
            moves = 0;
            for (int g = 0; g < games; g++) {
                var rng = new Random(900 + g);
                var cfg = new AIEngineConfig { DisableRandomRootTies = true, AiDepth = depth, TrainingMode = training, DisableParallelRootSearch = true, DisableQuiescenceSearch = true, Seed = 77 + g };
                var red = new AtaxxAIEngine(Prm.Base(), cfg) { MaxNodes = maxNodes };
                var blue = new AtaxxAIEngine(Prm.Base(), cfg) { MaxNodes = maxNodes };
                var board = new BitboardState { RedPieces = (1UL << 0) | (1UL << 48), BluePieces = (1UL << 6) | (1UL << 42) };
                var side = PlayerColor.Red;
                for (int ply = 0; ply < 40; ply++) {
                    var e = side == PlayerColor.Red ? red : blue;
                    e.AIPlayerColor = side;
                    var legal = e.GetAllValidMoves(board, side);
                    if (legal.Count == 0) break;
                    Move m;
                    if (ply < 4) {
                        m = legal[rng.Next(legal.Count)];
                    } else {
                        TestBoards.SetBoard(e, board.Clone(), side);
                        m = e.GetBestMove(side);
                        h = Fnv(h, e.LastSearchStats.Nodes + e.LastSearchStats.QNodes);
                        moves++;
                    }
                    h = Fnv(h, m.FromX * 1000 + m.FromY * 100 + m.ToX * 10 + m.ToY);
                    red.MakeMove(board, m, side);
                    side = AtaxxAIEngine.SwitchPlayer(side);
                    if (red.IsGameOver(board)) break;
                }
            }
            return h;
        }

        [Fact]
        public void Depth3_TrainingMode_IsUnchanged() {
            ulong d = PlayDigest(training: true, depth: 3, maxNodes: null, games: 3, out int moves);
            Assert.Equal(GoldenTrainingMoves, moves);
            Assert.Equal(GoldenTraining, d);
        }

        [Fact]
        public void Depth3_NormalMode_IsUnchanged() {
            ulong d = PlayDigest(training: false, depth: 3, maxNodes: null, games: 3, out int moves);
            Assert.Equal(GoldenNormalMoves, moves);
            Assert.Equal(GoldenNormal, d);
        }

        [Fact]
        public void Depth3_Budgeted_IsUnchanged() {
            ulong d = PlayDigest(training: true, depth: 3, maxNodes: 4000, games: 3, out int moves);
            Assert.Equal(GoldenBudgetMoves, moves);
            Assert.Equal(GoldenBudget, d);
        }

        private const int GoldenTrainingMoves = 77;
        private const ulong GoldenTraining = 10639527719178567635UL;
        private const int GoldenNormalMoves = 77;
        private const ulong GoldenNormal = 10909242927728478358UL;
        private const int GoldenBudgetMoves = 77;
        private const ulong GoldenBudget = 3648585795227170382UL;
    }
}
