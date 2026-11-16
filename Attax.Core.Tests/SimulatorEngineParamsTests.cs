using System;
using System.IO;
using System.Linq;
using Attax.Core;
using Attax.Play;
using Xunit;

namespace Attax.Core.Tests {
    /// <summary>
    /// Simulator-owned tests for <see cref="EngineParamsFile"/>, <see cref="PlayerSpec"/>, and <see cref="PlayerFactory"/>
    /// consuming <c>AttaxShared</c> engine parameters and <c>engine-params.json</c>.
    /// </summary>
    public class SimulatorEngineParamsTests {
        private static string FindSharedCoreDir() {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Attax.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.GetFullPath(Path.Combine(dir.FullName, "..", "AttaxShared", "src", "Attax.Core"));
        }

        [Fact]
        public void BaseFile_InAttaxShared_IsByteIdenticalToTestOutput() {
            string path = Path.Combine(FindSharedCoreDir(), "engine-params.json");
            Assert.True(File.Exists(path), $"Expected engine-params.json at {path}");
            var p = EngineParamsFile.ParseComplete(File.ReadAllText(path), path);
            Assert.Empty(p.Validate());
            Assert.Equal(File.ReadAllText(path), File.ReadAllText(EngineParamsFile.BasePath()));
            Assert.Throws<InvalidDataException>(() => EngineParamsFile.ParseComplete("{ \"root\": { \"riskPoints\": 2.0 } }"));
        }

        [Fact]
        public void LayeredLoading_BasePlusPartialFile_KeepsEverythingElse() {
            var layered = EngineParamsFile.Parse("{ \"root\": { \"riskPoints\": 2.0 }, \"eval\": { \"centerControlTable\": " +
                "[" + string.Join(",", Enumerable.Repeat(1, 49)) + "] } }", Prm.Base(), "test");
            Assert.Equal(2.0, layered.root.riskPoints);
            Assert.Equal(6.0, layered.root.cloneOpening);
            Assert.Equal(30, layered.eval.materialEarly);
            Assert.True(layered.eval.centerControlTable.All(v => v == 1));
            Assert.True(layered.root.positionalTable.SequenceEqual(Prm.Base().root.positionalTable));
            Assert.Empty(layered.Validate());
            Assert.Throws<InvalidDataException>(() => EngineParamsFile.Parse("{ \"root\": { \"cloneOpening\": 1, \"cloneOpening\": 2 } }", Prm.Base(), "dup"));
        }

        [Fact]
        public void TheSpecKey_TiesFalse_IsParsedValidatedAndPassedToTheEngine() {
            var spec = PlayerSpec.Parse("classic:depth=2,ties=false");
            Assert.Equal(false, spec.RandomTies);
            Assert.False(PlayerFactory.EffectiveRandomTies(spec));
            Assert.True(PlayerFactory.EffectiveRandomTies(PlayerSpec.Parse("classic:depth=3")));
            Assert.True(PlayerFactory.EffectiveRandomTies(PlayerSpec.Parse("classic:depth=3,ties=true")));
            Assert.Equal(PlayerSpec.Parse("classic:depth=3").SearchKey(), PlayerSpec.Parse("classic:depth=3,ties=true").SearchKey());
            Assert.Contains("ties=false", spec.ToString());
            Assert.Contains("root ties=first", PlayerFactory.Describe(spec, false));
            Assert.Contains("root ties=random", PlayerFactory.Describe(PlayerSpec.Parse("classic:depth=3"), false));
            Assert.Throws<FormatException>(() => PlayerSpec.Parse("classic:ties=maybe"));
            Assert.Throws<FormatException>(() => PlayerSpec.Parse("classic:p.search.randomizeRootTies=false"));
            Assert.Throws<InvalidDataException>(() => EngineParamsFile.Parse("{ \"search\": { \"randomizeRootTies\": false } }", Prm.Base(), "old"));
        }
    }
}
