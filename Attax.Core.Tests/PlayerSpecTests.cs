using System;
using System.Collections.Generic;
using System.Linq;
using Attax.Core;
using Attax.Model;
using Attax.Play;
using Xunit;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core.Tests {
    public class PlayerSpecTests {
        [Fact]
        public void Parses_AllKinds_WithDefaults() {
            var c = PlayerSpec.Parse("classic");
            Assert.Equal(PlayerKind.Classic, c.Kind);
            Assert.Equal(3, c.Depth);
            Assert.Equal(1f, c.EffectiveBonus);
            Assert.False(c.UsesModel);

            var v = PlayerSpec.Parse("value:model=m.onnx,depth=2,nodes=5000");
            Assert.Equal(PlayerKind.Value, v.Kind);
            Assert.Equal(2, v.Depth);
            Assert.Equal(5000, v.Nodes);
            Assert.Equal(0f, v.EffectiveBonus);   // model search drops the heuristic-point bonuses by default

            var p = PlayerSpec.Parse("policy:model=m.onnx,temp=0.5,topk=3");
            Assert.Equal(PlayerKind.Policy, p.Kind);
            Assert.Equal(0.5f, p.Temp);
            Assert.Equal(3, p.TopK);
            Assert.False(p.IsSearch);

            Assert.Equal(PlayerKind.Random, PlayerSpec.Parse("random").Kind);
        }

        [Theory]
        [InlineData("")]
        [InlineData("neural")]
        [InlineData("value")]                               // model required
        [InlineData("policy:temp=1")]                       // model required
        [InlineData("classic:model=m.onnx")]                // model is not valid for classic
        [InlineData("policy:depth=3,model=m.onnx")]         // search options are not valid for policy
        [InlineData("random:depth=3")]
        [InlineData("classic:depth=0")]
        [InlineData("classic:depth=13")]
        [InlineData("classic:depth=abc")]
        [InlineData("classic:nodes=-5")]
        [InlineData("classic:depth=3,depth=4")]             // duplicate
        [InlineData("classic:depth")]                        // missing value
        [InlineData("classic:bonus=-1")]
        [InlineData("classic:temp=NaN")]
        [InlineData("classic:train=maybe")]
        [InlineData("classic:bogus=1")]
        public void Rejects_BadSpecs(string text) {
            Assert.Throws<FormatException>(() => PlayerSpec.Parse(text));
        }

        [Theory]
        [InlineData("classic:depth=3,nodes=1000")]
        [InlineData("classic:depth=2,time=200,bonus=0.5,train=false,id=true,temp=1,topk=4")]
        [InlineData("value:model=a.onnx,depth=2,nodes=800,bonus=0.25")]
        [InlineData("policy:model=a.onnx,temp=0.7,topk=2")]
        [InlineData("random")]
        public void ToString_RoundTrips(string text) {
            var a = PlayerSpec.Parse(text);
            var b = PlayerSpec.Parse(a.ToString());
            Assert.Equal(a, b);
            Assert.Equal(a.ToString(), b.ToString());
        }

        [Fact]
        public void Greedy_DropsSamplingButKeepsSearchIdentity() {
            var s = PlayerSpec.Parse("classic:depth=3,nodes=1000,temp=2,topk=5");
            var g = s.Greedy();
            Assert.Equal(0f, g.Temp);
            Assert.Equal(1, g.TopK);
            Assert.Equal(s.SearchKey(), g.SearchKey());
            Assert.Equal(2f, s.Temp);   // original untouched
        }

        [Fact]
        public void SearchKey_IgnoresSampling_ButNotSearchSettings() {
            Assert.Equal(PlayerSpec.Parse("classic:depth=3,temp=1,topk=3").SearchKey(), PlayerSpec.Parse("classic:depth=3").SearchKey());
            Assert.NotEqual(PlayerSpec.Parse("classic:depth=3").SearchKey(), PlayerSpec.Parse("classic:depth=4").SearchKey());
            Assert.NotEqual(PlayerSpec.Parse("classic:depth=3").SearchKey(), PlayerSpec.Parse("classic:depth=3,nodes=10").SearchKey());
            Assert.NotEqual(PlayerSpec.Parse("classic:depth=3").SearchKey(), PlayerSpec.Parse("classic:depth=3,bonus=0").SearchKey());
        }

        private sealed class FakeSource : IModelSource {
            public readonly FakeModel Model = new FakeModel();
            public IPolicyValueModel Get(string path) => Model;
        }

        private static readonly BitboardState Start = TestBoards.Parse("R.....B", ".......", ".......", ".......", ".......", ".......", "B.....R");

        [Fact]
        public void EveryPlayerKind_ReturnsOnlyLegalMoves_AndRandomIsMarkedExploration() {
            var src = new FakeSource();
            var referee = TestBoards.NewEngine();
            foreach (var text in new[] { "classic:depth=2,nodes=2000", "value:model=x,depth=2,nodes=1500", "policy:model=x", "policy:model=x,temp=1,topk=3", "random" }) {
                using var player = PlayerFactory.Create(PlayerSpec.Parse(text), src, 1, defaultTrain: true);
                var board = Start.Clone();
                var side = PlayerColor.Red;
                for (int ply = 0; ply < 20 && !referee.IsGameOver(board); ply++) {
                    var d = player.Choose(board, side);
                    Assert.True(d.HasMove, text);
                    Assert.True(ActionCodec.IsLegal(d.Action, board, side), $"{text} ply {ply}");
                    Assert.Equal(text == "random", d.IsExploration);
                    referee.MakeMove(board, d.Move, side);
                    side = AtaxxAIEngine.SwitchPlayer(side);
                }
            }
        }

        [Fact]
        public void Player_OnTerminalPosition_ReportsNoMove() {
            var stuck = TestBoards.Parse("RRXX...", "RRXX...", "XXXX...", "XXXX...", ".......", ".......", "......B");
            var src = new FakeSource();
            foreach (var text in new[] { "classic:depth=2", "value:model=x,depth=2", "policy:model=x", "random" }) {
                using var player = PlayerFactory.Create(PlayerSpec.Parse(text), src, 1, true);
                Assert.False(player.Choose(stuck, PlayerColor.Red).HasMove, text);
            }
            Assert.Equal(0, src.Model.Calls);   // no model call when there is nothing to choose
        }

        [Fact]
        public void ModelPlayers_NeedAModelSource_AndClassicDoesNot() {
            Assert.Throws<InvalidOperationException>(() => PlayerFactory.Create(PlayerSpec.Parse("policy:model=x"), null, 1, true));
            Assert.Throws<InvalidOperationException>(() => PlayerFactory.Create(PlayerSpec.Parse("value:model=x"), null, 1, true));
            using var classic = PlayerFactory.Create(PlayerSpec.Parse("classic:depth=1"), null, 1, true);   // must not touch models
            Assert.True(classic.Choose(Start, PlayerColor.Red).HasMove);
        }

        private sealed class BrokenSource : IModelSource {
            private sealed class Broken : IPolicyValueModel {
                public PolicyValueOutput Evaluate(IReadOnlyList<BitboardState> boards, PlayerColor side) => throw new InvalidOperationException("broken model");
            }
            public IPolicyValueModel Get(string path) => new Broken();
        }

        [Fact]
        public void BrokenModel_FailsLoudly_InEveryModelMode() {
            foreach (var text in new[] { "value:model=x,depth=2", "policy:model=x" }) {
                using var player = PlayerFactory.Create(PlayerSpec.Parse(text), new BrokenSource(), 1, true);
                var ex = Assert.Throws<InvalidOperationException>(() => player.Choose(Start, PlayerColor.Red));
                Assert.Contains("broken model", ex.Message);
            }
        }

        [Theory]
        [InlineData("classic:depth=3,nodes=3000,ties=false")]   // budgeted: node counting must be deterministic too
        [InlineData("classic:depth=3,ties=false")]
        public void FreeTeacherLabel_EqualsAFreshTeacherSearch(string text) {
            // Self-play reuses the mover's own greedy move as the teacher label when the specs match. That is only valid if a
            // brand-new engine returns the identical move for the same position, including after the mover has played many moves.
            var spec = PlayerSpec.Parse(text);
            var referee = TestBoards.NewEngine();
            int compared = 0;
            // Games can end early (a colour wiped out), so keep playing new openings until enough positions are compared.
            for (int game = 0; game < 12 && compared < 40; game++) {
                var rng = new Random(100 + game);
                using var mover = PlayerFactory.Create(spec, null, 11 + game, defaultTrain: true);
                var board = Start.Clone();
                var side = PlayerColor.Red;
                for (int ply = 0; ply < 60 && !referee.IsGameOver(board); ply++) {
                    MoveDecision d;
                    if (ply < 4) {
                        var legal = referee.GetAllValidMoves(board, side);
                        var m = legal[rng.Next(legal.Count)];
                        d = new MoveDecision { HasMove = true, Move = m, Action = ActionCodec.Encode(m) };
                    } else {
                        d = mover.Choose(board, side);
                        using var fresh = PlayerFactory.Create(spec, null, 999, defaultTrain: true);
                        var teacher = fresh.Choose(board, side);
                        Assert.True(d.Action == teacher.Action, $"game {game} ply {ply}: mover chose {d.Action}, a fresh teacher chose {teacher.Action}");
                        compared++;
                    }
                    referee.MakeMove(board, d.Move, side);
                    side = AtaxxAIEngine.SwitchPlayer(side);
                }
            }
            Assert.True(compared >= 30, $"only {compared} positions compared");
        }

        [Fact]
        public void FreeTeacherLabels_AreOnlyReused_WhenTheMoverUsesTheTeachersExactTieRule() {
            // Self-play reuses the mover's move as the teacher label only when mover.SearchKey() == teacher.SearchKey().
            var mover = PlayerSpec.Parse("classic:depth=3,train=false");                     // engine default: random ties
            Assert.Equal(mover.SearchKey(), PlayerSpec.Parse("classic:depth=3,train=false,ties=true").SearchKey());   // explicit true == default
            var fixedTeacher = PlayerSpec.Parse("classic:depth=3,train=false,ties=false");
            Assert.NotEqual(mover.SearchKey(), fixedTeacher.SearchKey());                    // default teacher (first tied move) vs random mover: no reuse
            Assert.Equal(PlayerSpec.Parse("classic:depth=3,train=false,ties=false").SearchKey(), fixedTeacher.SearchKey());   // deterministic mover == deterministic teacher
            Assert.Equal(fixedTeacher, PlayerSpec.Parse(fixedTeacher.ToString()));          // round trip
            Assert.Equal(mover.WithRandomTies(false).SearchKey(), fixedTeacher.SearchKey());
            Assert.Throws<FormatException>(() => PlayerSpec.Parse("policy:model=m.onnx,ties=false"));
            Assert.Throws<FormatException>(() => PlayerSpec.Parse("classic:ties=maybe"));
        }
        [Theory]
        [InlineData("classic:depth=3", true, true, false)]                       // self-play default: training mode, no quiescence
        [InlineData("classic:depth=3", false, false, true)]                      // arena default: the shipped engine
        [InlineData("classic:depth=3,train=false", true, false, true)]           // explicit shipped engine inside self-play / relabel
        [InlineData("classic:depth=3,train=false,quiescence=false", true, false, false)]
        [InlineData("classic:depth=3,train=true", false, true, false)]
        [InlineData("value:model=x", false, true, false)]                        // model search is always training-style unless asked
        public void EffectiveMode_ReportsWhatTheEngineReallyRuns(string text, bool defaultTrain, bool expectTrain, bool expectQuiescence) {
            var (train, q) = PlayerFactory.EffectiveMode(PlayerSpec.Parse(text), defaultTrain);
            Assert.Equal(expectTrain, train); Assert.Equal(expectQuiescence, q);
        }

        [Fact]
        public void SearchScore_IsReported_OnlyOnRequest_IsIndependentOfSampling_AndStable() {
            var start = TestBoards.Parse("R.....B", ".......", ".......", ".......", ".......", ".......", "B.....R");
            using var off = PlayerFactory.Create(PlayerSpec.Parse("classic:depth=3,train=true"), null, 1, true);
            Assert.False(off.Choose(start, PlayerColor.Red).HasScore);
            using var on = PlayerFactory.Create(PlayerSpec.Parse("classic:depth=3,train=true"), null, 1, true, collectScore: true);
            var d = on.Choose(start, PlayerColor.Red);
            Assert.True(d.HasScore);
            Assert.False(d.UsedFallback);
            // asking for the score does not change the move or the work done
            var d0 = off.Choose(start, PlayerColor.Red);
            Assert.Equal(d0.Action, d.Action); Assert.Equal(d0.Nodes, d.Nodes);
            // a sampling player (temp/topk) plays differently but reports the same position value
            using var sampler = PlayerFactory.Create(PlayerSpec.Parse("classic:depth=3,train=true,temp=50,topk=6"), null, 3, true, collectScore: true);
            var seen = new HashSet<int>(); for (int i = 0; i < 12; i++) { using var p = PlayerFactory.Create(PlayerSpec.Parse("classic:depth=3,train=true,temp=50,topk=6"), null, 100 + i, true, collectScore: true); var s = p.Choose(start, PlayerColor.Red); seen.Add(s.Action); Assert.Equal(d.Score, s.Score); }
            Assert.True(seen.Count > 1, "the sampler should have played more than one move");
        }

        [Fact]
        public void ClassicPlayer_MatchesTheEngineDirectly() {
            // The player wrapper must not alter classic behaviour: same move as a bare engine with the same config.
            var cfg = new AIEngineConfig { AiDepth = 3, TrainingMode = true, DisableParallelRootSearch = true, DisableQuiescenceSearch = true, Seed = 5 };
            var engine = new AtaxxAIEngine(Prm.Base(), cfg);   // same parameters and the same (default, random) tie rule as the player
            var b = Start.Clone();
            b.ZobristHash = engine.ComputeZobristHash(b, PlayerColor.Red);
            engine.Board = b; engine.AIPlayerColor = PlayerColor.Red;
            var expected = engine.GetBestMove(PlayerColor.Red);

            using var player = PlayerFactory.Create(PlayerSpec.Parse("classic:depth=3,train=true"), null, 5, defaultTrain: false);
            var d = player.Choose(Start, PlayerColor.Red);
            Assert.True(expected.Equals(d.Move));
        }
    }
}
