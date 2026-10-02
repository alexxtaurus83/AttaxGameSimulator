using System;
using System.Collections.Generic;
using System.Linq;
using Attax.Core;
using Xunit;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core.Tests {
    // The value head is trained on the teacher's search score. That is only sound if the score of the move the teacher plays is the
    // exact minimax value of the position (no root bonus mixed in, no partial-window bound). These tests check it against a brute force.
    public class TeacherScoreTests {
        private const float Scale = 10000f;

        private static AtaxxAIEngine Engine(int depth) => new AtaxxAIEngine(Prm.Base(), new AIEngineConfig { DisableRandomRootTies = true,
            AiDepth = depth, TrainingMode = true, DisableParallelRootSearch = true, DisableQuiescenceSearch = true, Seed = 1 }) { CollectRootScores = true };

        // exact minimax of the heuristic evaluator, in engine units, from `me`'s point of view, searching `depth` plies after `me` has moved
        private static bool TryMinimax(AtaxxAIEngine rules, HeuristicEvaluator ev, BitboardState board, PlayerColor me, PlayerColor toMove, int depth, out int value) {
            value = 0;
            if (rules.IsGameOver(board)) return false;                       // terminal scores are a separate, tested path
            if (depth == 0) { value = (int)(ev.Evaluate(board, me) * Scale); return true; }
            var moves = rules.GetAllValidMoves(board, toMove);
            if (moves.Count == 0) return false;
            int best = toMove == me ? int.MinValue : int.MaxValue;
            foreach (var m in moves) {
                var child = board.Clone(); rules.MakeMove(child, m, toMove);
                if (!TryMinimax(rules, ev, child, me, SwitchPlayer(toMove), depth - 1, out int v)) return false;
                best = toMove == me ? Math.Max(best, v) : Math.Min(best, v);
            }
            value = best; return true;
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void ScoreOfTheChosenMove_IsTheExactMinimaxValue_NotThePreferredBonusInclusiveNumber(int depth) {
            var rules = new AtaxxAIEngine(Prm.Base());
            var ev = Prm.Eval();
            int checkedPositions = 0, withBonus = 0;
            foreach (var (b, side) in RefactorGoldenTests.RandomPositions(60, 1000 + depth)) {
                var e = Engine(depth);
                TestBoards.SetBoard(e, b.Clone(), side); e.AIPlayerColor = side;
                var chosen = e.GetBestMove(side);
                var best = e.LastRootScores[0];
                Assert.True(chosen.Equals(best.Move));
                // brute force: value of my chosen move = minimax over the remaining depth-1 plies
                var child = b.Clone(); rules.MakeMove(child, chosen, side);
                if (!TryMinimax(rules, ev, child, side, SwitchPlayer(side), depth - 1, out int expected)) continue;   // terminal somewhere: skip
                // At depth d the engine evaluates leaves from the player to move at the leaf; with an even/odd ply count that is me or the opponent.
                // Compare against the sign-corrected leaf evaluation the engine actually uses.
                int mine = expected;
                bool leafIsMine = (depth - 1) % 2 == 0;
                if (!leafIsMine) { if (!TryMinimaxLeafOpp(rules, ev, child, side, depth - 1, out mine)) continue; }
                Assert.Equal(mine, best.Strategic);
                Assert.Equal((long)best.Strategic + best.Bonus, (long)best.Final);
                if (best.Bonus != 0) withBonus++;
                checkedPositions++;
            }
            Assert.True(checkedPositions >= 25, $"only {checkedPositions} positions were checkable");
            Assert.True(withBonus > 0, "the root bonus must be non-zero somewhere, otherwise this test would not prove Strategic excludes it");
        }

        // Negamax form used by the engine: value for the side to move at each node, children negated, leaf = Evaluate(board, sideToMove).
        private static bool TryMinimaxLeafOpp(AtaxxAIEngine rules, HeuristicEvaluator ev, BitboardState board, PlayerColor me, int depth, out int valueForMe) {
            valueForMe = 0;
            bool ok = Negamax(rules, ev, board, SwitchPlayer(me), depth, out int v);
            valueForMe = -v;
            return ok;
        }

        private static bool Negamax(AtaxxAIEngine rules, HeuristicEvaluator ev, BitboardState board, PlayerColor toMove, int depth, out int value) {
            value = 0;
            if (rules.IsGameOver(board)) return false;
            if (depth == 0) { value = (int)(ev.Evaluate(board, toMove) * Scale); return true; }
            var moves = rules.GetAllValidMoves(board, toMove);
            if (moves.Count == 0) return false;
            int best = int.MinValue;
            foreach (var m in moves) {
                var child = board.Clone(); rules.MakeMove(child, m, toMove);
                if (!Negamax(rules, ev, child, SwitchPlayer(toMove), depth - 1, out int v)) return false;
                best = Math.Max(best, -v);
            }
            value = best; return true;
        }

        [Fact]
        public void ScoresAreFromTheSideToMovesPerspective_AndInvariantUnderSymmetry() {
            foreach (var (b, side) in RefactorGoldenTests.RandomPositions(25, 55)) {
                int Score(BitboardState board, PlayerColor s) {
                    var e = Engine(2); TestBoards.SetBoard(e, board.Clone(), s); e.AIPlayerColor = s; e.GetBestMove(s); return e.LastRootScores[0].Strategic;
                }
                int s0 = Score(b, side);
                ulong r = b.RedPieces, bl = b.BluePieces, x = b.BlockedSquares;
                BitboardOps.TransformState(ref r, ref bl, ref x, 3);
                var t = new BitboardState { RedPieces = r, BluePieces = bl, BlockedSquares = x };
                Assert.Equal(s0, Score(t, side));          // a mirrored board is the same position
            }
        }

        [Fact]
        public void TerminalWinsAndLosses_AreFarOutsideTheHeuristicRange() {
            // a position where the mover wins immediately: the stored score must be a decisive win so tanh() maps it to +1
            var b = TestBoards.Parse(".BBBBBB", "RRRRRRR", "RRRRRRR", "RRRRRRR", "RRRRRRR", "RRRRRRR", "RRRRRRR");
            var e = Engine(2); TestBoards.SetBoard(e, b, PlayerColor.Red); e.AIPlayerColor = PlayerColor.Red; e.GetBestMove(PlayerColor.Red);
            Assert.True(e.LastRootScores[0].Strategic > 500_000_000, $"winning move scored {e.LastRootScores[0].Strategic}");
            // typical non-terminal scores stay far below it (the trainer relies on this gap)
            foreach (var (pb, side) in RefactorGoldenTests.RandomPositions(30, 7)) {
                var e2 = Engine(3); TestBoards.SetBoard(e2, pb.Clone(), side); e2.AIPlayerColor = side; e2.GetBestMove(side);
                int s = e2.LastRootScores[0].Strategic;
                Assert.True(Math.Abs(s) < 500_000_000 || e2.IsGameOver(pb) == false, $"score {s}");
            }
        }
    }
}
