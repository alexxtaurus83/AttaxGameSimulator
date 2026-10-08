using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Attax.Core;
using Attax.Play;
using Newtonsoft.Json;
using Xunit;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core.Tests {
    public class EngineParamsFileTests : IDisposable {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "attax_params_" + Guid.NewGuid().ToString("N"));
        public EngineParamsFileTests() { Directory.CreateDirectory(dir); }
        public void Dispose() { try { Directory.Delete(dir, true); } catch { } }
        private string Write(string name, string json) { var p = Path.Combine(dir, name); File.WriteAllText(p, json); return p; }

        [Fact]
        public void SaveLoad_RoundTrip_IsLossless_AndFingerprintMatches() {
            var p = Prm.Base(); p.root.riskPoints = 1.3; p.eval.centerControlTable[24] = 9; p.root.aggressionFactor[1] = 1.75;
            string path = Path.Combine(dir, "x.json");
            EngineParamsFile.Save(p, path);
            var back = EngineParamsFile.Load(path);
            Assert.Empty(EngineParamsFile.Diff(p, back));
            Assert.Equal(EngineParamsFile.Fingerprint(p), EngineParamsFile.Fingerprint(back));
            Assert.Equal(EngineParamsFile.FingerprintUInt64(p), EngineParamsFile.FingerprintUInt64(back));
            Assert.NotEqual(EngineParamsFile.Fingerprint(Prm.Base()), EngineParamsFile.Fingerprint(p));
            Assert.Equal(EngineParamsFile.Fingerprint(Prm.Base()), EngineParamsFile.Fingerprint(Prm.Base()));   // stable
            Assert.Throws<ArgumentNullException>(() => EngineParamsFile.Fingerprint(null));
        }

        [Fact]
        public void Default_File_HasEveryValue_AndTablesStayOnOneLine() {
            string json = EngineParamsFile.ToJson(Prm.Base());
            Assert.Contains("\"centerControlTable\": [", json);
            Assert.DoesNotContain("\n    0,\n", json);   // tables are compact
            var flat = EngineParamsFile.Flatten(Prm.Base());
            Assert.Contains(flat, kv => kv.Key == "root.riskPoints" && kv.Value == "0.8");
            Assert.Contains(flat, kv => kv.Key == "eval.centerControlTable[24]");
            Assert.Equal(flat.Count, flat.Select(kv => kv.Key).Distinct().Count());   // every leaf has a unique path
            Assert.Equal(49, flat.Count(kv => kv.Key.StartsWith("root.positionalTable[")));
        }

        [Fact]
        public void Strict_UnknownNames_WrongTypes_AndBadValues_AreErrors_WithLocation() {
            var typo = Write("typo.json", "{ \"root\": { \"riskPoint\": 1 } }");
            var ex = Assert.Throws<InvalidDataException>(() => EngineParamsFile.Load(typo));
            Assert.Contains("riskPoint", ex.Message); Assert.Contains("typo.json", ex.Message);

            Assert.Throws<InvalidDataException>(() => EngineParamsFile.Load(Write("type.json", "{ \"root\": { \"riskPoints\": \"high\" } }")));
            Assert.Throws<InvalidDataException>(() => EngineParamsFile.Load(Write("top.json", "{ \"evall\": {} }")));
            Assert.Throws<InvalidDataException>(() => EngineParamsFile.Load(Write("trunc.json", "{ \"root\": { ")));
            Assert.Throws<InvalidDataException>(() => EngineParamsFile.Load(Write("empty.json", "")));
            var bad = Assert.Throws<InvalidDataException>(() => EngineParamsFile.Load(Write("range.json", "{ \"eval\": { \"materialEarly\": 0, \"centerControlTable\": [1,2] } }")));
            Assert.Contains("materialEarly", bad.Message); Assert.Contains("centerControlTable", bad.Message);   // all problems reported
            Assert.Throws<FileNotFoundException>(() => EngineParamsFile.Load(Path.Combine(dir, "missing.json")));
        }

        [Fact]
        public void PartialFile_KeepsDefaultsForEverythingElse() {
            var p = EngineParamsFile.Load(Write("part.json", "{ \"root\": { \"riskPoints\": 2.5 } }"));
            Assert.Equal(2.5, p.root.riskPoints);
            Assert.Equal(new[] { "root.riskPoints: 0.8 -> 2.5" }, EngineParamsFile.Diff(null, p));
        }

        [Fact]
        public void Load_Caches_ButNoticesEdits_AndNeverSharesTheObject() {
            string path = Write("c.json", "{ \"root\": { \"riskPoints\": 2.0 } }");
            var a = EngineParamsFile.Load(path); a.root.riskPoints = 99;                     // mutate the returned copy
            Assert.Equal(2.0, EngineParamsFile.Load(path).root.riskPoints);
            File.WriteAllText(path, "{ \"root\": { \"riskPoints\": 3.0, \"cloneMid\": 4.0 } }");   // different size => cache miss
            Assert.Equal(3.0, EngineParamsFile.Load(path).root.riskPoints);
        }

        [Fact]
        public void Overrides_Canonicalise_SetValues_AndRejectUnknownNames() {
            var p = Prm.Base();
            var c = EngineParamsFile.Canonicalize("root.riskpoints", "1.25", "spec");
            Assert.Equal("root.riskPoints", c.Key); Assert.Equal("1.25", c.Value);
            EngineParamsFile.Apply(p, c.Key, c.Value);
            Assert.Equal(1.25, p.root.riskPoints);
            var t = EngineParamsFile.Canonicalize("eval.centerControlTable.24", "7", "spec"); EngineParamsFile.Apply(p, t.Key, t.Value);
            Assert.Equal(7, p.eval.centerControlTable[24]);
            var t2 = EngineParamsFile.Canonicalize("eval.centerControlTable[25]", "8", "spec"); Assert.Equal("eval.centerControlTable.25", t2.Key);
            var a = EngineParamsFile.Canonicalize("root.aggressionFactor.1", "1.9", "spec"); EngineParamsFile.Apply(p, a.Key, a.Value);
            Assert.Equal(1.9, p.root.aggressionFactor[1]);
            var b = EngineParamsFile.Canonicalize("search.nullMoveReduction", "3", "spec"); EngineParamsFile.Apply(p, b.Key, b.Value);
            Assert.Equal(3, p.search.nullMoveReduction);

            foreach (var bad in new[] { "root.riskPoint", "evl.materialEarly", "root", "eval.centerControlTable", "eval.centerControlTable.49", "root.riskPoints.1", "schemaVersion" })
                Assert.Throws<FormatException>(() => EngineParamsFile.Canonicalize(bad, "1", "spec"));
            Assert.Throws<FormatException>(() => EngineParamsFile.Canonicalize("root.riskPoints", "abc", "spec"));
            Assert.Throws<FormatException>(() => EngineParamsFile.Canonicalize("eval.materialEarly", "1.5", "spec"));      // int field
            Assert.Throws<FormatException>(() => EngineParamsFile.Canonicalize("root.riskPoints", "NaN", "spec"));
        }

        [Fact]
        public void Diff_ListsOnlyChangedValues() {
            var a = Prm.Base(); var b = Prm.Base();
            Assert.Empty(EngineParamsFile.Diff(a, b));
            b.eval.cornerWeight = 9; b.root.positionalTable[3] = 1;
            var d = EngineParamsFile.Diff(a, b);
            Assert.Equal(2, d.Count);
            Assert.Contains("eval.cornerWeight: 5 -> 9", d);
            Assert.Contains(d, x => x.StartsWith("root.positionalTable[3]:"));
        }
    }

    public class PlayerSpecParamsTests : IDisposable {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "attax_spec_" + Guid.NewGuid().ToString("N"));
        public PlayerSpecParamsTests() { Directory.CreateDirectory(dir); }
        public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

        [Fact]
        public void NewKeys_Parse_RoundTrip_AndAreRejectedWhereMeaningless() {
            var s = PlayerSpec.Parse("classic:depth=4,quiescence=false,p.root.riskpoints=1.2,p.eval.centerControlTable.24=4");
            Assert.False(s.Quiescence.Value);
            Assert.Equal(2, s.Overrides.Count);
            Assert.Equal(s, PlayerSpec.Parse(s.ToString()));
            Assert.Contains("p.root.riskPoints=1.2", s.ToString());      // canonical spelling

            foreach (var bad in new[] { "policy:model=m.onnx,quiescence=true", "policy:model=m.onnx,p.root.riskPoints=1", "random:params=x.json",
                                        "classic:p.root.riskPoints=1,p.root.riskPoints=2", "classic:p.nope.x=1", "classic:p.root.riskPoints=abc" })
                Assert.Throws<FormatException>(() => PlayerSpec.Parse(bad));
        }

        [Fact]
        public void ResolveParams_NullWhenUnused_FileThenOverrides_AndValidates() {
            // Always complete and equal to the base file when nothing is overridden; policy/random players have none.
            Assert.Empty(EngineParamsFile.Diff(Prm.Base(), PlayerSpec.Parse("classic:depth=3").ResolveParams()));
            Assert.Null(PlayerSpec.Parse("random").ResolveParams());
            string f = Path.Combine(dir, "p.json"); File.WriteAllText(f, "{ \"root\": { \"riskPoints\": 2.0, \"cloneMid\": 4.0 } }");
            var p = PlayerSpec.Parse($"classic:params={f},p.root.riskPoints=3.0").ResolveParams();
            Assert.Equal(3.0, p.root.riskPoints);       // override wins over the file
            Assert.Equal(4.0, p.root.cloneMid);          // file value kept
            Assert.Equal(6.0, p.root.cloneOpening);      // default kept
            var ex = Assert.Throws<FormatException>(() => PlayerSpec.Parse("classic:p.eval.materialEarly=0").ResolveParams());
            Assert.Contains("materialEarly", ex.Message);
            Assert.Throws<FileNotFoundException>(() => PlayerSpec.Parse("classic:params=" + Path.Combine(dir, "none.json")).ResolveParams());
        }

        [Fact]
        public void SearchKey_DiffersWhenParamsDiffer_SoFreeTeacherLabelsAreNotReusedAcrossParameterSets() {
            // The console teacher carries ties=false; a mover on the default (random ties) is a different search and must not donate its move as a label.
            Assert.NotEqual(PlayerSpec.Parse("classic:depth=3").SearchKey(), PlayerSpec.Parse("classic:depth=3,ties=false").SearchKey());
            var a = PlayerSpec.Parse("classic:depth=3");
            var b = PlayerSpec.Parse("classic:depth=3,p.root.riskPoints=1.5");
            var c = PlayerSpec.Parse("classic:depth=3,quiescence=false");
            Assert.NotEqual(a.SearchKey(), b.SearchKey());
            Assert.NotEqual(a.SearchKey(), c.SearchKey());
            Assert.Equal(a.SearchKey(), PlayerSpec.Parse("classic:depth=3,temp=1,topk=3").SearchKey());
        }

        [Fact]
        public void Player_UsesItsOwnParams_AndTwoPlayersDoNotInterfere() {
            var start = TestBoards.Parse("R.....B", ".......", ".......", ".......", ".......", ".......", "B.....R");
            using var plain = PlayerFactory.Create(PlayerSpec.Parse("classic:depth=3,train=true"), null, 1, true);
            using var penalised = PlayerFactory.Create(PlayerSpec.Parse("classic:depth=3,train=true,p.root.nonCapturingJumpPenalty=500"), null, 1, true);
            var m1 = plain.Choose(start, PlayerColor.Red); var m2 = penalised.Choose(start, PlayerColor.Red);
            Assert.False(m1.Action == m2.Action, "the penalty must flip the opening jump to a clone");
            Assert.True(ActionCodecIsClone(m2.Action));
            Assert.Equal(m1.Action, plain.Choose(start, PlayerColor.Red).Action);   // the other player's params did not leak in
        }
        private static bool ActionCodecIsClone(int action) => ActionCodec.IsClone(action);

        [Fact]
        public void Describe_ShowsEffectiveModeQuiescenceAndParams() {
            Assert.Contains("normal", PlayerFactory.Describe(PlayerSpec.Parse("classic:depth=3"), defaultTrain: false));
            Assert.Contains("quiescence=on", PlayerFactory.Describe(PlayerSpec.Parse("classic:depth=3"), defaultTrain: false));
            Assert.Contains("quiescence=off", PlayerFactory.Describe(PlayerSpec.Parse("classic:depth=3,quiescence=false"), defaultTrain: false));
            Assert.Contains("training", PlayerFactory.Describe(PlayerSpec.Parse("classic:depth=3"), defaultTrain: true));
            Assert.Contains("(= engine-params.json)", PlayerFactory.Describe(PlayerSpec.Parse("classic:depth=3"), false));
            Assert.DoesNotContain("(= engine-params.json)", PlayerFactory.Describe(PlayerSpec.Parse("classic:depth=3,p.root.riskPoints=1"), false));
            Assert.Equal("random", PlayerFactory.Describe(PlayerSpec.Parse("random"), false));
        }

        [Fact]
        public void ArenaClassic_QuiescenceDefault_MatchesWhatTheGameShips() {
            // Normal mode + quiescence on is the game's default engine; the arena must test that, not a stripped-down engine.
            var start = TestBoards.Parse("R.....B", ".......", ".......", ".......", ".......", ".......", "B.....R");
            var spec = PlayerSpec.Parse("classic:depth=3");
            using var viaPlayer = PlayerFactory.Create(spec, null, 5, defaultTrain: false);
            var engine = new AtaxxAIEngine(Prm.Base(), new AIEngineConfig { AiDepth = 3, DisableParallelRootSearch = true, Seed = 5 });   // the player uses the engine default for ties too
            var b = start.Clone(); b.ZobristHash = engine.ComputeZobristHash(b, PlayerColor.Red); engine.Board = b; engine.AIPlayerColor = PlayerColor.Red;
            var expected = engine.GetBestMove(PlayerColor.Red);
            var got = viaPlayer.Choose(start, PlayerColor.Red);
            Assert.True(expected.Equals(got.Move));
            Assert.Equal(engine.LastSearchStats.Nodes + engine.LastSearchStats.QNodes, got.Nodes);
        }
    }

    public class PairStatsTests {
        private static double[] Sample(Random rng, int n, double d) {
            // Pair score distribution with sd ~0.29 (measured in the arena) and mean 0.5 + d.
            double x = 0.168 + d, y = 0.168 - d;
            return Enumerable.Range(0, n).Select(_ => { double r = rng.NextDouble(); return r < x ? 1.0 : r < x + y ? 0.0 : 0.5; }).ToArray();
        }

        [Fact]
        public void EloScoreConversion_IsInverse_AndKnownPoints() {
            Assert.Equal(0.5, PairStats.EloToScore(0), 12);
            Assert.Equal(0.0, PairStats.ScoreToElo(0.5), 9);
            Assert.Equal(400 * Math.Log10(3), PairStats.ScoreToElo(0.75), 9);
            foreach (double e in new[] { -300.0, -20, 5, 100, 450 }) Assert.Equal(e, PairStats.ScoreToElo(PairStats.EloToScore(e)), 8);
            Assert.True(double.IsNegativeInfinity(PairStats.ScoreToElo(0))); Assert.True(double.IsPositiveInfinity(PairStats.ScoreToElo(1)));
        }

        [Fact]
        public void Summarize_MatchesHandComputedValues() {
            var s = PairStats.Summarize(new[] { 1.0, 0.5, 0.0, 0.5 });
            Assert.Equal(0.5, s.Mean, 12);
            Assert.Equal(Math.Sqrt(0.5 / 3), s.StdDev, 12);
            Assert.Equal(0.5 - 1.96 * s.StdDev / 2, s.Lo, 12); Assert.Equal(0.5 + 1.96 * s.StdDev / 2, s.Hi, 12);
            Assert.Equal(0, PairStats.Summarize(new double[0]).N);
        }

        [Fact]
        public void Sprt_Validation_AndShortSequencesNeverDecide() {
            Assert.Throws<ArgumentException>(() => PairStats.Sprt(new[] { 0.5, 0.5 }, 5, 0, 0.05, 0.05));
            Assert.Throws<ArgumentException>(() => PairStats.Sprt(new[] { 0.5, 0.5 }, 0, 5, 0.0, 0.05));
            Assert.Equal(SprtDecision.Continue, PairStats.Sprt(new double[0], 0, 5, 0.05, 0.05).decision);
            Assert.Equal(SprtDecision.Continue, PairStats.Sprt(new[] { 1.0 }, 0, 5, 0.05, 0.05).decision);
            var (a, b) = PairStats.ParseSprtBounds("0,5"); Assert.Equal((0.0, 5.0), (a, b));
            Assert.Throws<FormatException>(() => PairStats.ParseSprtBounds("5,0"));
            Assert.Throws<FormatException>(() => PairStats.ParseSprtBounds("x"));
        }

        [Fact]
        public void Sprt_ClearWinnerAndClearLoser_AreDecidedTheRightWay() {
            var win = Enumerable.Repeat(new[] { 1.0, 0.5, 1.0, 0.5, 0.0 }, 100).SelectMany(x => x).ToArray();   // mean 0.6
            Assert.Equal(SprtDecision.AcceptH1, PairStats.Sprt(win, 0, 20, 0.05, 0.05).decision);
            var lose = Enumerable.Repeat(new[] { 0.0, 0.5, 0.0, 0.5, 1.0 }, 100).SelectMany(x => x).ToArray();  // mean 0.4
            Assert.Equal(SprtDecision.AcceptH0, PairStats.Sprt(lose, 0, 20, 0.05, 0.05).decision);
        }

        [Fact]
        public void Sprt_ErrorRates_AreNearTheirNominalLevels() {
            // Simulation, fixed seed. Under H0 (no difference) a wrongly accepted H1 must be rare (nominal alpha = 5%; allow 2x for the
            // normal approximation); under H1 (+20 Elo) the improvement must be found most of the time within 3,000 pairs.
            var rng = new Random(2024);
            const int runs = 300, max = 3000, minPairs = 40;
            int falseAccept = 0, found = 0, undecidedH1 = 0;
            for (int r = 0; r < runs; r++) {
                var h0 = Sample(rng, max, 0.0);
                for (int n = minPairs; n <= max; n += 8)
                    if (PairStats.Sprt(h0.Take(n).ToArray(), 0, 20, 0.05, 0.05).decision == SprtDecision.AcceptH1) { falseAccept++; break; }
                var h1 = Sample(rng, max, PairStats.EloToScore(20) - 0.5);
                bool decided = false;
                for (int n = minPairs; n <= max; n += 8) {
                    var dec = PairStats.Sprt(h1.Take(n).ToArray(), 0, 20, 0.05, 0.05).decision;
                    if (dec == SprtDecision.AcceptH1) { found++; decided = true; break; }
                    if (dec == SprtDecision.AcceptH0) { decided = true; break; }
                }
                if (!decided) undecidedH1++;
            }
            Assert.True(falseAccept / (double)runs <= 0.10, $"false-accept rate {falseAccept}/{runs}");
            Assert.True(found / (double)runs >= 0.75, $"power {found}/{runs} (undecided {undecidedH1})");
        }
    }
}
