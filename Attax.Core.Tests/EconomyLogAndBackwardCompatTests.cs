using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Attax.Console.Economy;
using Attax.Data;
using Xunit;

namespace Attax.Core.Tests {
    /// <summary>The economy tooling is additive: the training verbs and the v4 (ATLG) log keep working unchanged.</summary>
    public class EconomyLogAndBackwardCompatTests {
        private static (int exit, string output) RunConsole(params string[] args) {
            string dll = Path.Combine(AppContext.BaseDirectory, "Attax.Console.dll");
            Assert.True(File.Exists(dll), "Attax.Console.dll must be next to the test assembly");
            var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(dll);
            foreach (string a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, stdout + stderr);
        }

        [Fact]
        public void SelfPlay_StillWritesAValidV4Log() {
            string dir = Path.Combine(Path.GetTempPath(), "attax-compat-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                string log = Path.Combine(dir, "g.bin");
                var play = RunConsole("selfplay", "--games", "2", "--seed", "3", "--out", log, "--red", "classic:depth=1", "--samples", "4", "--parallel", "1");
                Assert.True(play.exit == 0, play.output);
                var validate = RunConsole("validate-log", "--path", log);
                Assert.True(validate.exit == 0, validate.output);
                Assert.Contains("Log is valid: 2 games", validate.output);

                int games = new LogV3Reader().ReadGames(log).Count();
                Assert.Equal(2, games);
            } finally {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void EconomySim_Verb_WritesAJsonlLogNotAnAtlgLog() {
            string dir = Path.Combine(Path.GetTempPath(), "attax-compat-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                string log = Path.Combine(dir, "e.jsonl");
                var r = RunConsole("economy-sim", "--boards", "default", "--games", "1", "--red", "classic:depth=1,quiescence=false", "--abilities", "true", "--parallel", "1", "--out", log);
                Assert.True(r.exit == 0, r.output);
                Assert.Contains("SUMMARY games=1", r.output);
                Assert.False(File.Exists(log + ".tmp"));
                string first = File.ReadLines(log).First();
                Assert.StartsWith("{", first);
                Assert.Single(EconomyLog.Read(log));
                // the training validator must reject it, proving the formats stay separate
                Assert.NotEqual(0, RunConsole("validate-log", "--path", log).exit);
            } finally {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void EconomyAnalyze_ApplyIsRejectedWithoutVerificationAndLeavesConfigsUntouched() {
            string dir = Path.Combine(Path.GetTempPath(), "attax-compat-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                string log = Path.Combine(dir, "e.jsonl");
                Assert.Equal(0, RunConsole("economy-sim", "--boards", "default,story-02", "--games", "2", "--red", "classic:depth=1,quiescence=false", "--abilities", "false", "--parallel", "1", "--out", log).exit);
                string rules = Path.Combine(dir, "game-rules.json"), ai = Path.Combine(dir, "ai-ability-config.json"), doc = Path.Combine(dir, "ECONOMY.md");
                var ctx = EconomyContext.Load(null, null);
                File.Copy(ctx.RulesPath, rules); File.Copy(ctx.AiPath, ai);
                File.WriteAllText(doc, EconomyReport.BeginMarker + "\nold\n" + EconomyReport.EndMarker + "\n");
                string rulesBefore = File.ReadAllText(rules), aiBefore = File.ReadAllText(ai), docBefore = File.ReadAllText(doc);

                var draft = RunConsole("economy-analyze", "--in", log, "--out", Path.Combine(dir, "out"), "--verify-games", "0", "--rules", rules, "--ai-config", ai);
                Assert.True(draft.exit == 0, draft.output);
                Assert.True(File.Exists(Path.Combine(dir, "out", "economy-report.md")));
                Assert.True(File.Exists(Path.Combine(dir, "out", "economy-report.json")));

                var apply = RunConsole("economy-analyze", "--in", log, "--out", Path.Combine(dir, "out2"), "--verify-games", "0", "--apply", "--rules", rules, "--ai-config", ai, "--economy-doc", doc);
                Assert.NotEqual(0, apply.exit);
                Assert.Equal(rulesBefore, File.ReadAllText(rules));
                Assert.Equal(aiBefore, File.ReadAllText(ai));
                Assert.Equal(docBefore, File.ReadAllText(doc));
            } finally {
                Directory.Delete(dir, true);
            }
        }
    }
}
