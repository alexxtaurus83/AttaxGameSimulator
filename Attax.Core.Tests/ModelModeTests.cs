using System;
using System.Collections.Generic;
using System.Linq;
using Attax.Core;
using Attax.Model;
using Xunit;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core.Tests {
    internal sealed class FakeModel : IPolicyValueModel {
        public Func<BitboardState, PlayerColor, float> Value = (b, s) => {
            int f = AtaxxAIEngine.PopCount(s == PlayerColor.Red ? b.RedPieces : b.BluePieces);
            int e = AtaxxAIEngine.PopCount(s == PlayerColor.Red ? b.BluePieces : b.RedPieces);
            return Math.Clamp((f - e) / 20f, -1f, 1f);
        };
        public Func<BitboardState, PlayerColor, int, float> Logit = (b, s, a) => ((a * 2654435761u) % 1000u) / 100f;
        public int Calls;
        public int Positions;
        public PlayerColor? LastSide;

        public PolicyValueOutput Evaluate(IReadOnlyList<BitboardState> boards, PlayerColor side) {
            Calls++;
            Positions += boards.Count;
            LastSide = side;
            var v = new float[boards.Count];
            var l = new float[boards.Count * ActionCodec.ActionCount];
            for (int i = 0; i < boards.Count; i++) {
                v[i] = Value(boards[i], side);
                for (int a = 0; a < ActionCodec.ActionCount; a++) l[i * ActionCodec.ActionCount + a] = Logit(boards[i], side, a);
            }
            return new PolicyValueOutput(boards.Count, v, l);
        }
    }

    public class ModelModeTests {
        private static readonly BitboardState Start = TestBoards.Parse(
            "R.....B", ".......", ".......", ".......", ".......", ".......", "B.....R");

        [Fact]
        public void BoardEncoder_UsesSideToMovePerspective() {
            var b = TestBoards.Parse("RB.....", "X......", ".......", ".......", ".......", ".......", ".......");
            var red = new float[BoardEncoder.FloatsPerBoard];
            var blue = new float[BoardEncoder.FloatsPerBoard];
            BoardEncoder.Encode(b, PlayerColor.Red, red, 0);
            BoardEncoder.Encode(b, PlayerColor.Blue, blue, 0);
            // channel 0 = friendly, 1 = enemy, 2 = blocked, 3 = constant
            Assert.Equal(1f, red[0]); Assert.Equal(1f, red[49 + 1]);
            Assert.Equal(1f, blue[1]); Assert.Equal(1f, blue[49 + 0]);
            Assert.Equal(1f, red[98 + 7]); Assert.Equal(1f, blue[98 + 7]);
            Assert.All(red.Skip(147), v => Assert.Equal(1f, v));
            Assert.Equal(2f, red.Take(49).Sum() + red.Skip(49).Take(49).Sum());
        }

        [Fact]
        public void BoardEncoder_RejectsBadInput() {
            var buf = new float[BoardEncoder.FloatsPerBoard];
            Assert.Throws<ArgumentException>(() => BoardEncoder.Encode(Start, PlayerColor.None, buf, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => BoardEncoder.Encode(Start, PlayerColor.Red, new float[10], 0));
        }

        [Fact]
        public void Policy_NeverReturnsIllegalMove_EvenWhenIllegalLogitsAreHuge() {
            var model = new FakeModel();
            var mask = new bool[ActionCodec.ActionCount];
            ActionCodec.FillLegalMask(Start, PlayerColor.Red, mask);
            model.Logit = (b, s, a) => mask[a] ? 0f : 1e6f; // illegal actions look irresistible
            var e = TestBoards.NewEngine();
            var choice = PolicyMoveSelector.Select(model, Start, PlayerColor.Red, 0f, 1, new Random(1));
            Assert.True(choice.HasMove);
            Assert.True(mask[choice.Action]);
            Assert.Contains(choice.Move, e.GetAllValidMoves(Start, PlayerColor.Red));
        }

        [Fact]
        public void Policy_Greedy_PicksBestLegalLogit() {
            var model = new FakeModel();
            var mask = new bool[ActionCodec.ActionCount];
            ActionCodec.FillLegalMask(Start, PlayerColor.Red, mask);
            int target = Enumerable.Range(0, ActionCodec.ActionCount).Last(a => mask[a]);
            model.Logit = (b, s, a) => a == target ? 5f : 0f;
            var c = PolicyMoveSelector.Select(model, Start, PlayerColor.Red, 0f, 1, new Random(1));
            Assert.Equal(target, c.Action);
            Assert.Equal(1f, c.Probability);
        }

        [Fact]
        public void Policy_NoLegalMove_ReportsNoMoveAndDoesNotCallModel() {
            var b = TestBoards.Parse("RRXX...", "RRXX...", "XXXX...", "XXXX...", ".......", ".......", "......B");
            var model = new FakeModel();
            var c = PolicyMoveSelector.Select(model, b, PlayerColor.Red, 0f, 1, new Random(1));
            Assert.False(c.HasMove);
            Assert.Equal(0, model.Calls);
        }

        [Fact]
        public void Policy_TopKSampling_StaysInsideTopK_AndProbabilitiesAreValid() {
            var model = new FakeModel();
            var mask = new bool[ActionCodec.ActionCount];
            ActionCodec.FillLegalMask(Start, PlayerColor.Red, mask);
            var legal = Enumerable.Range(0, ActionCodec.ActionCount).Where(a => mask[a]).ToList();
            model.Logit = (b, s, a) => a; // larger index = larger logit
            var top3 = legal.OrderByDescending(a => a).Take(3).ToHashSet();
            var rng = new Random(5);
            var seen = new HashSet<int>();
            for (int i = 0; i < 300; i++) {
                var c = PolicyMoveSelector.Select(model, Start, PlayerColor.Red, 50f, 3, rng);
                Assert.Contains(c.Action, top3);
                Assert.InRange(c.Probability, 0f, 1f);
                seen.Add(c.Action);
            }
            Assert.True(seen.Count > 1, "high temperature should sample more than one action");
        }

        [Fact]
        public void Policy_InvalidModelOutput_Throws() {
            var model = new FakeModel { Logit = (b, s, a) => a == 3 ? float.NaN : 0f };
            Assert.Throws<InvalidOperationException>(() => PolicyMoveSelector.Select(model, Start, PlayerColor.Red, 0f, 1, new Random(1)));
            var model2 = new FakeModel { Value = (b, s) => 7f };
            Assert.Throws<InvalidOperationException>(() => PolicyMoveSelector.Select(model2, Start, PlayerColor.Red, 0f, 1, new Random(1)));
        }

        [Fact]
        public void PolicyValueOutput_RejectsWrongShapes() {
            Assert.Throws<InvalidOperationException>(() => new PolicyValueOutput(2, new float[1], new float[2 * ActionCodec.ActionCount]));
            Assert.Throws<InvalidOperationException>(() => new PolicyValueOutput(1, new float[1], new float[5]));
        }

        [Fact]
        public void ValueEvaluator_ExposesOnlyTheValueHead_AndValidates() {
            var model = new FakeModel();
            var ev = new ModelValueEvaluator(model);
            Assert.Equal(model.Value(Start, PlayerColor.Red), ev.Evaluate(Start, PlayerColor.Red));
            var batch = ev.EvaluateBatch(new[] { Start, Start }, PlayerColor.Blue);
            Assert.Equal(2, batch.Length);
            Assert.Equal(PlayerColor.Blue, model.LastSide);

            var bad = new ModelValueEvaluator(new FakeModel { Value = (b, s) => float.NaN });
            Assert.Throws<InvalidOperationException>(() => bad.Evaluate(Start, PlayerColor.Red));
        }

        [Fact]
        public void ValueSearch_PlaysOnlyLegalMoves_ThroughAWholeGame() {
            var model = new FakeModel();
            var cfg = new AIEngineConfig {
                AiDepth = 2, TrainingMode = true, DisableParallelRootSearch = true, DisableQuiescenceSearch = true, Seed = 3,
                RootBonusScale = 0f, ThrowOnSearchError = true
            };
            var red = new AtaxxAIEngine(Prm.Base(), cfg, new ModelValueEvaluator(model)) { MaxNodes = 3000 };
            var blue = new AtaxxAIEngine(Prm.Base(), cfg, new ModelValueEvaluator(model)) { MaxNodes = 3000 };
            var board = Start.Clone();
            var side = PlayerColor.Red;
            for (int ply = 0; ply < 30; ply++) {
                var e = side == PlayerColor.Red ? red : blue;
                e.AIPlayerColor = side;
                TestBoards.SetBoard(e, board.Clone(), side);
                var legal = e.GetAllValidMoves(board, side);
                if (legal.Count == 0) break;
                var m = e.GetBestMove(side);
                Assert.Contains(m, legal);
                red.MakeMove(board, m, side);
                side = AtaxxAIEngine.SwitchPlayer(side);
                if (red.IsGameOver(board)) break;
            }
            Assert.True(model.Positions > 0);
        }

        private sealed class ThrowingModel : IPolicyValueModel {
            public PolicyValueOutput Evaluate(IReadOnlyList<BitboardState> boards, PlayerColor side) => throw new InvalidOperationException("model exploded");
        }

        [Fact]
        public void SearchError_IsSwallowedByDefault_AndRethrownWhenRequested() {
            var cfg = new AIEngineConfig { AiDepth = 2, TrainingMode = true, DisableParallelRootSearch = true, Seed = 1 };
            var lenient = new AtaxxAIEngine(Prm.Base(), cfg, new ModelValueEvaluator(new ThrowingModel())) { MaxNodes = 2000 };
            TestBoards.SetBoard(lenient, Start.Clone(), PlayerColor.Red);
            lenient.AIPlayerColor = PlayerColor.Red;
            Assert.True(lenient.GetBestMove(PlayerColor.Red).Equals(default(Move))); // legacy: error becomes "no move"

            cfg.ThrowOnSearchError = true;
            var strict = new AtaxxAIEngine(Prm.Base(), cfg, new ModelValueEvaluator(new ThrowingModel())) { MaxNodes = 2000 };
            TestBoards.SetBoard(strict, Start.Clone(), PlayerColor.Red);
            strict.AIPlayerColor = PlayerColor.Red;
            var ex = Assert.Throws<InvalidOperationException>(() => strict.GetBestMove(PlayerColor.Red));
            Assert.Contains("exploded", ex.Message);
        }

        [Fact]
        public void RootScores_AreOptIn_AndConsistent() {
            var e = TestBoards.NewEngine(depth: 2);
            TestBoards.SetBoard(e, Start.Clone(), PlayerColor.Red);
            e.AIPlayerColor = PlayerColor.Red;
            e.GetBestMove(PlayerColor.Red);
            Assert.Null(e.LastRootScores);

            e.CollectRootScores = true;
            var best = e.GetBestMove(PlayerColor.Red);
            var scores = e.LastRootScores;
            Assert.NotNull(scores);
            Assert.Equal(e.GetAllValidMoves(Start, PlayerColor.Red).Count, scores.Count);
            Assert.True(scores[0].Move.Equals(best));
            for (int i = 1; i < scores.Count; i++) Assert.True(scores[i - 1].Final >= scores[i].Final);
            foreach (var s in scores) Assert.Equal((long)s.Strategic + s.Bonus, (long)s.Final);
        }

        [Fact]
        public void RootBonusScale_ZeroRemovesBonus_AndValidation() {
            Func<float?, AtaxxAIEngine> make = scale => {
                var cfg = new AIEngineConfig { AiDepth = 2, TrainingMode = true, DisableParallelRootSearch = true, DisableQuiescenceSearch = true, Seed = 1, RootBonusScale = scale };
                var e = new AtaxxAIEngine(Prm.Base(), cfg) { CollectRootScores = true };
                TestBoards.SetBoard(e, Start.Clone(), PlayerColor.Red);
                e.AIPlayerColor = PlayerColor.Red;
                e.GetBestMove(PlayerColor.Red);
                return e;
            };
            Assert.All(make(0f).LastRootScores, s => Assert.Equal(0, s.Bonus));
            var legacy = make(null).LastRootScores;
            var one = make(1f).LastRootScores;
            Assert.Contains(legacy, s => s.Bonus != 0);
            Assert.Equal(legacy.Select(s => (s.Move, s.Final)), one.Select(s => (s.Move, s.Final)));
            Assert.Throws<ArgumentException>(() => make(-1f));
            var badTemp = new AIEngineConfig { SelectionTemperatureUnit = 0f };
            Assert.Throws<ArgumentException>(() => new AtaxxAIEngine(Prm.Base(), badTemp));
            var badWin = new AIEngineConfig { AspirationWindow = 0 };
            Assert.Throws<ArgumentException>(() => new AtaxxAIEngine(Prm.Base(), badWin));
        }

        [Fact]
        public void ModelDepth1_ScoresTerminalChildrenByTheRule_NotByTheModel() {
            // One empty square (0,0). Red's clone from (1,0) fills the board with Blue far ahead on count: a terminal
            // LOSS for Red. Red's jump from (2,0) leaves the game open. A model that likes every position must not be
            // able to make the losing clone look good.
            var b = TestBoards.Parse(
                ".RRBBBB", "BBBBBBB", "BBBBBBB", "BBBBBBB", "BBBBBBB", "BBBBBBB", "BBBBBBB");
            var model = new FakeModel { Value = (bd, s) => s == PlayerColor.Blue ? -1f : 1f }; // every child looks great for Red
            var cfg = new AIEngineConfig { AiDepth = 1, TrainingMode = true, DisableParallelRootSearch = true, DisableQuiescenceSearch = true, Seed = 1, RootBonusScale = 0f };
            var e = new AtaxxAIEngine(Prm.Base(), cfg, new ModelValueEvaluator(model)) { CollectRootScores = true };
            TestBoards.SetBoard(e, b.Clone(), PlayerColor.Red);
            e.AIPlayerColor = PlayerColor.Red;

            var moves = e.GetAllValidMoves(b, PlayerColor.Red);
            Assert.True(moves.Count > 1, "needs the batch path (more than one root move)");
            var best = e.GetBestMove(PlayerColor.Red);
            var scores = e.LastRootScores;

            bool sawTerminalLoss = false;
            foreach (var s in scores) {
                var after = b.Clone(); e.MakeMove(after, s.Move, PlayerColor.Red);
                if (e.IsGameOver(after) && e.GetTerminalOutcomeFor(after, PlayerColor.Red) < 0) {
                    sawTerminalLoss = true;
                    Assert.True(s.Strategic < -900_000_000, $"terminal loss scored {s.Strategic}, expected a decisive loss score");
                    Assert.False(best.Equals(s.Move));
                }
            }
            Assert.True(sawTerminalLoss, "fixture must contain a terminal-loss root move");
        }

        [Fact]
        public void Metered_CountsCallsAndPositions() {
            var metered = new MeteredPolicyValueModel(new FakeModel());
            metered.Evaluate(new[] { Start, Start, Start }, PlayerColor.Red);
            metered.Evaluate(new[] { Start }, PlayerColor.Red);
            Assert.Equal(2, metered.Calls);
            Assert.Equal(4, metered.Positions);
            Assert.True(metered.TotalMilliseconds >= 0);
        }
    }
}
