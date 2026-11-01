using System;
using System.Collections.Generic;
using System.Linq;
using Attax.Core;
using Newtonsoft.Json;
using Xunit;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core.Tests {
    public class EngineParamsTests {
        // The legacy formulas, copied from the pre-refactor AttaxConstants, as an independent oracle.
        private static int LegacyMaterial(int t) {
            if (t <= 4) return 30; if (t >= 49) return 40;
            float progress = (float)(t - 4) / (49 - 4);
            return (int)(30 + (40 - 30) * progress);
        }
        private static int LegacyMobility(int t) {
            if (t <= 4) return 3; if (t >= 49) return 4;
            float progress = (float)(t - 4) / (49 - 4);
            return (int)(3 - (3 - 4) * progress);
        }
        private static double LegacyClone(int t) => t <= 8 ? 6.0 : t <= 20 ? 3.0 : 1.0;
        private static double LegacyAggression(int d) => d >= -2 ? 1.0 : d >= -5 ? 1.5 : d >= -8 ? 2.0 : 2.5;
        private static readonly int[,] LegacyPositional = {
            {1,2,3,3,3,2,1},{2,3,4,4,4,3,2},{3,4,5,6,5,4,3},{3,4,6,7,6,4,3},{3,4,5,6,5,4,3},{2,3,4,4,4,3,2},{1,2,3,3,3,2,1} };

        [Fact]
        public void BaseFile_ReproducesEveryLegacyFormula_ForEveryPieceCount() {
            var c = Prm.Base().Compile();
            for (int t = 0; t <= 49; t++) {
                Assert.Equal(LegacyMaterial(t), c.MaterialWeight[t]);
                Assert.Equal(LegacyMobility(t), c.MobilityWeight[t]);
                Assert.Equal(t >= 35 ? 14 : 1, c.StabilityMult[t]);
                Assert.Equal(LegacyClone(t), c.CloneBonus(t));
            }
            for (int d = -60; d <= 60; d++) Assert.Equal(LegacyAggression(d), c.Aggression(d));
            for (int y = 0; y < 7; y++) for (int x = 0; x < 7; x++) Assert.Equal(LegacyPositional[y, x], c.PositionalValue(x, y));
            Assert.Equal(0, c.PositionalValue(-1, 0)); Assert.Equal(0, c.PositionalValue(0, 7));
            Assert.True(BoardLookup.CenterControlWeights.SequenceEqual(c.CenterControlTable));
        }

        [Fact]
        public void BaseFile_MatchesTheLegacyScalars() {
            var p = Prm.Base();
            Assert.Equal((30, 40, 3, 4, 3, 2, 5, 2, 14, 14, 35, 1), (p.eval.materialEarly, p.eval.materialLate, p.eval.mobilityEarly, p.eval.mobilityLate,
                p.eval.potentialMobilityWeight, p.eval.centerControlWeight, p.eval.cornerWeight, p.eval.edgeWeight, p.eval.endgameEmptyThreshold,
                p.eval.stabilityWeight, p.eval.stabilityFullMinPieces, p.eval.stabilityLowMult));
            Assert.Equal((6.0, 3.0, 1.0, 8, 20, 0.0, 0.8, 0.3, 35, 6.0), (p.root.cloneOpening, p.root.cloneMid, p.root.cloneLate, p.root.openingMax,
                p.root.midMax, p.root.flipPoints, p.root.riskPoints, p.root.positionalPoints, p.root.positionalMaxPieces, p.root.nonCapturingJumpPenalty));
            Assert.Equal((14, 3, 3, 2, 3, 2), (p.search.nullMoveMinEmpty, p.search.nullMoveMinPieces, p.search.nullMoveMinDepth, p.search.nullMoveReduction,
                p.search.quiescenceMinRootDepth, p.search.quiescenceDepth));
            Assert.Empty(p.Validate());
        }

        [Fact]
        public void EvaluatorFromTheBaseFile_IsTheSameWhicheverWayItIsBuilt() {
            var a = Prm.Eval();
            var b = new HeuristicEvaluator(Prm.Base());
            foreach (var (bd, side) in RefactorGoldenTests.RandomPositions(1500, 99)) Assert.Equal(a.Evaluate(bd, side), b.Evaluate(bd, side));
        }

        [Fact]
        public void Engine_PlaysTheSame_WhicheverCopyOfTheBaseParametersItIsGiven() {
            ulong a = RefactorGoldenTests.PlayDigest(false, true, 3, 2, 12, out int m1);
            ulong b = RefactorGoldenTests.PlayDigest(false, true, 3, 2, 12, out int m2);
            Assert.Equal(m1, m2); Assert.Equal(a, b);
        }

        // ---- there is nothing in code: parameters are required ----
        [Fact]
        public void ABlankObject_IsRejected_NotSilentlyZero() {
            var errors = new EngineParams().Validate();
            Assert.NotEmpty(errors);
            Assert.Contains(errors, m => m.Contains("schemaVersion"));
            Assert.Contains(errors, m => m.Contains("eval group is missing"));
            Assert.Throws<ArgumentException>(() => new HeuristicEvaluator(new EngineParams()));
            Assert.Throws<ArgumentNullException>(() => new HeuristicEvaluator((EngineParams)null));
            // a group present but with its tables missing is also caught, and says which
            var half = new EngineParams { schemaVersion = EngineParams.CurrentSchemaVersion, eval = new EvalParams(), root = new RootParams(), search = new SearchParams() };
            var e2 = half.Validate();
            Assert.Contains(e2, m => m.Contains("centerControlTable is missing"));
            Assert.Contains(e2, m => m.Contains("positionalTable is missing"));
            Assert.Contains(e2, m => m.Contains("eval.materialEarly"));   // zero weights are out of range
        }

        [Fact]
        public void ThereIsNoEngineWithoutParameters() {
            // Mandatory constructor argument: null and blank/invalid objects are rejected at construction, with every problem listed.
            var ex = Assert.Throws<ArgumentNullException>(() => new AtaxxAIEngine((EngineParams)null));
            Assert.Contains("engine-params.json", ex.Message);
            Assert.Throws<ArgumentException>(() => new AtaxxAIEngine(new EngineParams()));
            var bad = Prm.Base(); bad.eval.materialEarly = 0;
            var ex2 = Assert.Throws<ArgumentException>(() => new AtaxxAIEngine(bad, new AIEngineConfig { DisableRandomRootTies = true, AiDepth = 3 }, new MarkerEvaluator()));
            Assert.Contains("eval.materialEarly", ex2.Message);   // validated even when a model evaluator is used: root/search groups still apply
        }

        [Fact]
        public void EvaluatorArgument_NullMeansHeuristic_ModelReplacesIt_HeuristicObjectIsRejected() {
            var start = TestBoards.Parse("R.....B", ".......", ".......", ".......", ".......", ".......", "B.....R");
            var cfg = new AIEngineConfig { DisableRandomRootTies = true, AiDepth = 2, TrainingMode = true, DisableParallelRootSearch = true, Seed = 1 };
            var heuristic = new AtaxxAIEngine(Prm.Base(), cfg);                       // evaluator = null -> heuristic from the same parameters
            heuristic.Board = start.Clone(); heuristic.AIPlayerColor = PlayerColor.Red;
            Assert.NotEqual(default, heuristic.GetBestMove(PlayerColor.Red));
            var marker = new MarkerEvaluator();
            var withModel = new AtaxxAIEngine(Prm.Base(), cfg, marker);               // the model replaces the heuristic at every leaf
            withModel.Board = start.Clone(); withModel.AIPlayerColor = PlayerColor.Red;
            withModel.GetBestMove(PlayerColor.Red);
            Assert.True(marker.Calls > 0);
            // a HeuristicEvaluator object is rejected: it could disagree with the engine parameters
            Assert.Throws<ArgumentException>(() => new AtaxxAIEngine(Prm.Base(), cfg, Prm.Eval()));
        }

        [Fact]
        public void UseMLRootOnly_IsGone() {
            string core = FindCoreDir();
            foreach (var f in System.IO.Directory.GetFiles(core, "*.cs")) {
                string src = System.IO.File.ReadAllText(f);
                Assert.DoesNotContain("UseMLRootOnly", src);
                Assert.DoesNotContain("useMLRootOnly", src);
                Assert.DoesNotContain("searchEvaluator", src);
            }
            Assert.Null(typeof(AtaxxAIEngine.AIEngineConfig).GetField("UseMLRootOnly"));
            Assert.Null(typeof(AtaxxAIEngine.AIEngineConfig).GetField("Params"));   // parameters are a constructor argument, not an optional config field
        }

        [Fact]
        public void NoNumericDefaultsRemainInCoreSource() {
            // The legacy tunables must exist only in engine-params.json. Look for the old constants in the Core sources.
            string core = FindCoreDir();
            foreach (var f in System.IO.Directory.GetFiles(core, "*.cs")) {
                string src = System.IO.File.ReadAllText(f);
                Assert.DoesNotContain("GetScaledMaterialWeight", src);
                Assert.DoesNotContain("GetAggressionFactor", src);
                Assert.DoesNotContain("earlyMaterialWeight", src);
                Assert.DoesNotContain("CreateDefault", src);
                Assert.DoesNotContain("DefaultCompiled", src);
            }
            string ep = System.IO.File.ReadAllText(System.IO.Path.Combine(core, "EngineParams.cs"));
            Assert.DoesNotMatch(@"public (int|double) \w+ = -?\d", ep);     // no field initialisers with numbers
            Assert.DoesNotMatch(@"new\[\] \{ ?-?\d", ep);                   // no built-in tables
        }

        private static string FindCoreDir() {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Attax.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return System.IO.Path.Combine(dir.FullName, "Attax.Core");
        }

        [Fact]
        public void BaseFile_IsComplete_AndNextToTheCoreSources() {
            string path = System.IO.Path.Combine(FindCoreDir(), "engine-params.json");
            Assert.True(System.IO.File.Exists(path));
            var p = Attax.Play.EngineParamsFile.ParseComplete(System.IO.File.ReadAllText(path), path);   // throws if anything is missing
            Assert.Empty(p.Validate());
            // the copy the tests run against is byte-identical to the source of truth
            Assert.Equal(System.IO.File.ReadAllText(path), System.IO.File.ReadAllText(Attax.Play.EngineParamsFile.BasePath()));
            // a partial file is not a complete base file
            Assert.Throws<System.IO.InvalidDataException>(() => Attax.Play.EngineParamsFile.ParseComplete("{ \"root\": { \"riskPoints\": 2.0 } }"));
        }

        [Fact]
        public void JsonRoundTrip_Newtonsoft_IsLossless() {
            var p = Prm.Base();
            p.eval.materialLate = 37; p.root.riskPoints = 1.25; p.search.nullMoveReduction = 3;
            string json = JsonConvert.SerializeObject(p, Formatting.Indented);
            var back = JsonConvert.DeserializeObject<EngineParams>(json);
            Assert.Equal(37, back.eval.materialLate);
            Assert.Equal(1.25, back.root.riskPoints);
            Assert.Equal(3, back.search.nullMoveReduction);
            Assert.True(p.eval.centerControlTable.SequenceEqual(back.eval.centerControlTable));
            Assert.True(p.root.positionalTable.SequenceEqual(back.root.positionalTable));
            Assert.Equal(json, JsonConvert.SerializeObject(back, Formatting.Indented));

            // Deserialising a PARTIAL file directly gives zeros for what it omits and is rejected: hosts must load the whole file.
            var partial = JsonConvert.DeserializeObject<EngineParams>("{ \"root\": { \"riskPoints\": 2.0 } }");
            Assert.Equal(2.0, partial.root.riskPoints);
            Assert.Equal(0.0, partial.root.cloneOpening);
            Assert.Null(partial.root.positionalTable);
            Assert.NotEmpty(partial.Validate());
        }

        [Fact]
        public void LayeredLoading_BasePlusPartialFile_KeepsEverythingElse() {
            var layered = Attax.Play.EngineParamsFile.Parse("{ \"root\": { \"riskPoints\": 2.0 }, \"eval\": { \"centerControlTable\": " +
                "[" + string.Join(",", Enumerable.Repeat(1, 49)) + "] } }", Prm.Base(), "test");
            Assert.Equal(2.0, layered.root.riskPoints);
            Assert.Equal(6.0, layered.root.cloneOpening);                   // from the base
            Assert.Equal(30, layered.eval.materialEarly);                   // from the base
            Assert.True(layered.eval.centerControlTable.All(v => v == 1));  // a table that is present replaces the whole table
            Assert.True(layered.root.positionalTable.SequenceEqual(Prm.Base().root.positionalTable));
            Assert.Empty(layered.Validate());
            Assert.Throws<System.IO.InvalidDataException>(() => Attax.Play.EngineParamsFile.Parse("{ \"root\": { \"cloneOpening\": 1, \"cloneOpening\": 2 } }", Prm.Base(), "dup"));   // duplicate key
        }

        [Fact]
        public void Tables_MustBeComplete_AndAConstantAggressionFactorIsAllowed()
        {
            // An empty or short table is an error now (there is no built-in table to fall back to)...
            var p = Prm.Base();
            p.eval.centerControlTable = new int[0]; p.root.positionalTable = new int[0];
            Assert.Contains(p.Validate(), m => m.Contains("centerControlTable must have 49"));
            Assert.Contains(p.Validate(), m => m.Contains("positionalTable must have 49"));
            // ...while a constant factor with no thresholds is a legitimate explicit setting.
            var c = Prm.Base(); c.root.aggressionMinLead = new int[0]; c.root.aggressionFactor = new[] { 2.0 };
            Assert.Empty(c.Validate());
            Assert.Equal(2.0, c.Compile().Aggression(-30));
            Assert.Equal(2.0, c.Compile().Aggression(30));
            // A real mismatch is still an error.
            var bad = Prm.Base(); bad.root.aggressionFactor = new[] { 1.0 };
            Assert.NotEmpty(bad.Validate());
        }

        [Fact]
        public void Clone_IsADeepCopy()
        {
            var p = Prm.Base();
            var n = p.Clone();
            n.eval.centerControlTable[0] = 99; n.root.positionalTable[0] = 99; n.root.aggressionFactor[0] = 9;
            Assert.NotEqual(99, p.eval.centerControlTable[0]);
            Assert.NotEqual(99, p.root.positionalTable[0]);
            Assert.NotEqual(9.0, p.root.aggressionFactor[0]);
            Assert.NotSame(Prm.Base().eval, Prm.Base().eval);
        }

        [Fact]
        public void Engine_SnapshotsParams_LaterEditsDoNotLeakIn() {
            var p = Prm.Base();
            var e = TestBoards.NewEngineWith(p, depth: 2);
            var start = TestBoards.Parse("R.....B", ".......", ".......", ".......", ".......", ".......", "B.....R");
            TestBoards.SetBoard(e, start.Clone(), PlayerColor.Red); e.AIPlayerColor = PlayerColor.Red;
            var before = e.GetBestMove(PlayerColor.Red);
            p.root.cloneOpening = -500; p.eval.materialEarly = 1; p.root.positionalTable[0] = 100;   // mutate after construction
            TestBoards.SetBoard(e, start.Clone(), PlayerColor.Red);
            Assert.True(before.Equals(e.GetBestMove(PlayerColor.Red)));
        }

        [Fact]
        public void TwoEngines_WithDifferentParams_DoNotInterfere() {
            var a = Prm.Base(); var b = Prm.Base();
            // At the opening the depth-3 search prefers a jump by ~250,000 points, far more than the clone bonus (69,000), so only
            // a large jump penalty flips the move to a clone; that makes the two parameter sets visibly different.
            b.root.nonCapturingJumpPenalty = 500;
            var start = TestBoards.Parse("R.....B", ".......", ".......", ".......", ".......", ".......", "B.....R");

            Move Play(EngineParams p) {
                var e = TestBoards.NewEngineWith(p, depth: 3);
                TestBoards.SetBoard(e, start.Clone(), PlayerColor.Red); e.AIPlayerColor = PlayerColor.Red;
                return e.GetBestMove(PlayerColor.Red);
            }
            var soloA = Play(a); var soloB = Play(b);
            // Interleave: construct both first, then alternate calls (shared statics would show up here).
            var ea = TestBoards.NewEngineWith(a, depth: 3); var eb = TestBoards.NewEngineWith(b, depth: 3);
            for (int i = 0; i < 3; i++) {
                TestBoards.SetBoard(ea, start.Clone(), PlayerColor.Red); ea.AIPlayerColor = PlayerColor.Red;
                TestBoards.SetBoard(eb, start.Clone(), PlayerColor.Red); eb.AIPlayerColor = PlayerColor.Red;
                Assert.True(soloA.Equals(ea.GetBestMove(PlayerColor.Red)));
                Assert.True(soloB.Equals(eb.GetBestMove(PlayerColor.Red)));
            }
            Assert.False(soloA.Equals(soloB), "the two parameter sets should differ in the opening move");

            // And concurrently.
            var results = new Move[8];
            System.Threading.Tasks.Parallel.For(0, 8, i => {
                var e = TestBoards.NewEngineWith(i % 2 == 0 ? a : b, depth: 3);
                TestBoards.SetBoard(e, start.Clone(), PlayerColor.Red); e.AIPlayerColor = PlayerColor.Red;
                results[i] = e.GetBestMove(PlayerColor.Red);
            });
            for (int i = 0; i < 8; i++) Assert.True(results[i].Equals(i % 2 == 0 ? soloA : soloB));
        }

        [Fact]
        public void ModelEvaluator_KeepsItsOwnEvaluator_ButUsesParamsForRootAndSearch() {
            var p = Prm.Base(); p.root.cloneOpening = 77;
            var marker = new MarkerEvaluator();
            var e = new AtaxxAIEngine(p, new AIEngineConfig { DisableRandomRootTies = true, AiDepth = 1, TrainingMode = true, DisableParallelRootSearch = true, Seed = 1 }, marker) { CollectRootScores = true };
            var start = TestBoards.Parse("R.....B", ".......", ".......", ".......", ".......", ".......", "B.....R");
            TestBoards.SetBoard(e, start.Clone(), PlayerColor.Red); e.AIPlayerColor = PlayerColor.Red;
            e.GetBestMove(PlayerColor.Red);
            Assert.True(marker.Calls > 0, "a non-heuristic evaluator must not be replaced");
            Assert.Contains(e.LastRootScores, s => s.Bonus > 70 * 10000 - 1);   // clone bonus 77 pts * 10,000, minus risk/position nudges
        }

        private sealed class MarkerEvaluator : IValueEvaluator {
            public int Calls;
            public float Evaluate(BitboardState board, PlayerColor sideToMove) { Calls++; return 0f; }
        }

        [Fact]
        public void TheEngineParametersReachEveryGroup_EvalRootAndSearch() {
            var start = TestBoards.Parse("R.....B", ".......", ".......", ".......", ".......", ".......", "B.....R");
            List<RootScore> Scores(EngineParams p) {
                var e = new AtaxxAIEngine(p, new AIEngineConfig { DisableRandomRootTies = true, AiDepth = 2, TrainingMode = true, DisableParallelRootSearch = true, Seed = 1 }) { CollectRootScores = true };
                TestBoards.SetBoard(e, start.Clone(), PlayerColor.Red); e.AIPlayerColor = PlayerColor.Red; e.GetBestMove(PlayerColor.Red);
                return e.LastRootScores.ToList();
            }
            var baseline = Scores(Prm.Base());
            var rootChanged = Prm.Base(); rootChanged.root.cloneOpening = 50;
            var evalChanged = Prm.Base(); evalChanged.eval.materialEarly = 45;
            Assert.NotEqual(baseline.Select(s => s.Bonus), Scores(rootChanged).Select(s => s.Bonus));        // root group reaches the root bonus
            Assert.NotEqual(baseline.Select(s => s.Strategic), Scores(evalChanged).Select(s => s.Strategic)); // eval group reaches the leaf evaluator
            Assert.Equal(baseline.Select(s => (s.Move, s.Final)), Scores(Prm.Base()).Select(s => (s.Move, s.Final)));   // deterministic
        }

        // ---- validation ----
        public static IEnumerable<object[]> BadParams() {
            Action<EngineParams> none = _ => { };
            yield return new object[] { "schemaVersion", (Action<EngineParams>)(p => p.schemaVersion = 2), "schemaVersion" };
            yield return new object[] { "materialEarly 0", (Action<EngineParams>)(p => p.eval.materialEarly = 0), "eval.materialEarly" };
            yield return new object[] { "materialLate 1000", (Action<EngineParams>)(p => p.eval.materialLate = 1000), "eval.materialLate" };
            yield return new object[] { "scale start>=end", (Action<EngineParams>)(p => { p.eval.scaleStartPieces = 49; p.eval.scaleEndPieces = 49; }), "scaleStartPieces" };
            yield return new object[] { "center table length", (Action<EngineParams>)(p => p.eval.centerControlTable = new int[10]), "centerControlTable" };
            yield return new object[] { "center table value", (Action<EngineParams>)(p => { p.eval.centerControlTable = new int[49]; p.eval.centerControlTable[3] = 1000; }), "centerControlTable[3]" };
            yield return new object[] { "NaN risk", (Action<EngineParams>)(p => p.root.riskPoints = double.NaN), "root.riskPoints" };
            yield return new object[] { "Infinity clone", (Action<EngineParams>)(p => p.root.cloneOpening = double.PositiveInfinity), "root.cloneOpening" };
            yield return new object[] { "opening>mid", (Action<EngineParams>)(p => { p.root.openingMax = 30; p.root.midMax = 10; }), "openingMax" };
            yield return new object[] { "aggression length", (Action<EngineParams>)(p => p.root.aggressionFactor = new[] { 1.0, 2.0 }), "aggressionFactor" };
            yield return new object[] { "aggression order", (Action<EngineParams>)(p => p.root.aggressionMinLead = new[] { -8, -5, -2 }), "descending" };
            yield return new object[] { "aggression zero", (Action<EngineParams>)(p => p.root.aggressionFactor = new[] { 1.0, 1.5, 0.0, 2.5 }), "aggressionFactor[2]" };
            yield return new object[] { "positional length", (Action<EngineParams>)(p => p.root.positionalTable = new int[48]), "positionalTable" };
            yield return new object[] { "null reduction 0", (Action<EngineParams>)(p => p.search.nullMoveReduction = 0), "nullMoveReduction" };
            yield return new object[] { "quiescence depth -1", (Action<EngineParams>)(p => p.search.quiescenceDepth = -1), "quiescenceDepth" };
        }

        [Theory]
        [MemberData(nameof(BadParams))]
        public void Invalid_IsRejected_WithAnActionableMessage(string label, Action<EngineParams> mutate, string mustMention) {
            var p = Prm.Base(); mutate(p);
            var errors = p.Validate();
            Assert.NotEmpty(errors);
            Assert.Contains(errors, m => m.Contains(mustMention));
            var ex = Assert.Throws<ArgumentException>(() => new HeuristicEvaluator(p));
            Assert.Contains(mustMention, ex.Message);
            Assert.Throws<ArgumentException>(() => new AtaxxAIEngine(p, new AIEngineConfig()));
        }

        [Fact]
        public void Validation_ReportsAllProblemsAtOnce_AndNeverThrows() {
            var p = Prm.Base();
            p.eval.materialEarly = 0; p.root.riskPoints = double.NaN; p.search.nullMoveReduction = 0; p.eval.centerControlTable = new int[3];
            var errors = p.Validate();
            Assert.True(errors.Count >= 4, string.Join(" | ", errors));
        }

        // ---- liveness: every parameter must change something observable ----
        [Fact]
        public void EveryEvalParameter_ChangesTheEvaluation() {
            var positions = RefactorGoldenTests.RandomPositions(1200, 555);
            var baseEval = Prm.Eval();
            var variants = new Dictionary<string, Action<EngineParams>> {
                ["materialEarly"] = p => p.eval.materialEarly += 7, ["materialLate"] = p => p.eval.materialLate += 7,
                ["mobilityEarly"] = p => p.eval.mobilityEarly += 5, ["mobilityLate"] = p => p.eval.mobilityLate += 5,
                ["potentialMobilityWeight"] = p => p.eval.potentialMobilityWeight += 5, ["centerControlWeight"] = p => p.eval.centerControlWeight += 5,
                ["cornerWeight"] = p => p.eval.cornerWeight += 5, ["edgeWeight"] = p => p.eval.edgeWeight += 5,
                ["endgameEmptyThreshold"] = p => p.eval.endgameEmptyThreshold = 25,
                ["stabilityWeight"] = p => p.eval.stabilityWeight += 6, ["stabilityFullMinPieces"] = p => p.eval.stabilityFullMinPieces = 20,
                ["stabilityLowMult"] = p => p.eval.stabilityLowMult = 9, ["scaleStartPieces"] = p => p.eval.scaleStartPieces = 20,
                ["scaleEndPieces"] = p => p.eval.scaleEndPieces = 30, ["centerControlTable"] = p => p.eval.centerControlTable[24] += 5,
            };
            foreach (var (name, mutate) in variants) {
                var p = Prm.Base(); mutate(p);
                var ev = new HeuristicEvaluator(p);
                bool changed = positions.Any(x => baseEval.Evaluate(x.b, x.side) != ev.Evaluate(x.b, x.side));
                Assert.True(changed, $"eval.{name} has no effect on any of {positions.Count} positions (dead parameter?)");
            }
        }

        [Fact]
        public void EveryRootParameter_ChangesRootScores_OrIsDocumentedInert() {
            var positions = RefactorGoldenTests.RandomPositions(120, 777);
            List<RootScore> Scores(EngineParams p, BitboardState b, PlayerColor side) {
                var e = new AtaxxAIEngine(p ?? Prm.Base(), new AIEngineConfig { DisableRandomRootTies = true, AiDepth = 1, TrainingMode = true, DisableParallelRootSearch = true, Seed = 1 }) { CollectRootScores = true };
                TestBoards.SetBoard(e, b.Clone(), side); e.AIPlayerColor = side; e.GetBestMove(side);
                return e.LastRootScores?.ToList();
            }
            var variants = new Dictionary<string, Action<EngineParams>> {
                ["cloneOpening"] = p => p.root.cloneOpening += 9, ["cloneMid"] = p => p.root.cloneMid += 9, ["cloneLate"] = p => p.root.cloneLate += 9,
                ["openingMax"] = p => p.root.openingMax = 14, ["midMax"] = p => p.root.midMax = 30,
                ["flipPoints"] = p => p.root.flipPoints = 5, ["riskPoints"] = p => p.root.riskPoints += 3, ["positionalPoints"] = p => p.root.positionalPoints += 3,
                ["positionalMaxPieces"] = p => p.root.positionalMaxPieces = 10, ["nonCapturingJumpPenalty"] = p => p.root.nonCapturingJumpPenalty += 9,
                ["aggressionFactor"] = p => p.root.aggressionFactor = new[] { 1.0, 3.0, 4.0, 5.0 }, ["aggressionMinLead"] = p => p.root.aggressionMinLead = new[] { 3, 0, -3 },
                ["positionalTable"] = p => p.root.positionalTable[24] += 20,
            };
            foreach (var (name, mutate) in variants) {
                var p = Prm.Base(); mutate(p);
                bool changed = false;
                foreach (var (b, side) in positions) {
                    var s0 = Scores(null, b, side); var s1 = Scores(p, b, side);
                    if (s0 == null || s1 == null) continue;
                    if (!s0.Select(s => (s.Move, s.Final)).SequenceEqual(s1.Select(s => (s.Move, s.Final)))) { changed = true; break; }
                }
                Assert.True(changed, $"root.{name} has no effect on the root scores of {positions.Count} positions");
            }
        }

        [Fact]
        public void EverySearchParameter_ChangesTheSearch() {
            // Node counts are the sensitive probe: pruning gates change how much is searched.
            var positions = RefactorGoldenTests.RandomPositions(25, 4242).Where(x => PopCount(x.b.EmptySquares()) > 12).ToList();
            long Nodes(EngineParams p, bool quiescence, int depth) {
                long n = 0;
                foreach (var (b, side) in positions) {
                    var e = new AtaxxAIEngine(p ?? Prm.Base(), new AIEngineConfig { DisableRandomRootTies = true, AiDepth = depth, TrainingMode = false, DisableQuiescenceSearch = !quiescence, DisableParallelRootSearch = true, Seed = 1 });
                    TestBoards.SetBoard(e, b.Clone(), side); e.AIPlayerColor = side; e.GetBestMove(side);
                    n += e.LastSearchStats.Nodes + e.LastSearchStats.QNodes; e.Dispose();
                }
                return n;
            }
            long baseline4 = Nodes(null, true, 4);
            foreach (var (name, mutate, quiescence) in new (string, Action<EngineParams>, bool)[] {
                ("nullMoveMinEmpty", p => p.search.nullMoveMinEmpty = 40, true), ("nullMoveMinPieces", p => p.search.nullMoveMinPieces = 30, true),
                ("nullMoveMinDepth", p => p.search.nullMoveMinDepth = 12, true), ("nullMoveReduction", p => p.search.nullMoveReduction = 1, true),
                ("quiescenceMinRootDepth", p => p.search.quiescenceMinRootDepth = 12, true), ("quiescenceDepth", p => p.search.quiescenceDepth = 0, true) }) {
                var p = Prm.Base(); mutate(p);
                Assert.True(Nodes(p, quiescence, 4) != baseline4, $"search.{name} did not change the node count at depth 4");
            }
        }
    }
}
