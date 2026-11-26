using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Attax.Console.Economy;
using Attax.Data;
using CommandLine;
using Xunit;

namespace Attax.Core.Tests {
    /// <summary>The economy tooling is additive: the training verbs and the v4 (ATLG) log keep working unchanged.</summary>
    public class EconomyLogAndBackwardCompatTests {
        [Theory]
        [InlineData("economy-sim", "abilities", true)]
        [InlineData("economy-sim", "swap-colors", false)]
        [InlineData("economy-analyze", "quiescence", true)]
        [InlineData("economy", "quiescence", true)]
        public void EconomyBooleanOptions_RequireExplicitValuesAndKeepDefaults(string verb, string option, bool defaultValue) {
            using var parser = new CommandLine.Parser(settings => settings.HelpWriter = null);
            var args = verb == "economy-analyze" ? new[] { verb, "--out", "unused", "--in", "unused.jsonl" } : new[] { verb, "--out", "unused" };
            ParserResult<object> Parse(params string[] extra) =>
                parser.ParseArguments<Attax.Console.EconomySimOptions, Attax.Console.EconomyAnalyzeOptions, Attax.Console.EconomyOptions>(args.Concat(extra));
            bool Read(CommandLine.ParserResult<object> result) => Attax.Console.Program.ReadEconomyBoolean(option, Assert.IsType<CommandLine.Parsed<object>>(result).Value switch {
                Attax.Console.EconomySimOptions sim => option == "abilities" ? sim.Abilities : sim.SwapColors,
                Attax.Console.EconomyAnalyzeOptions analyze => analyze.Quiescence,
                Attax.Console.EconomyOptions economy => economy.Quiescence,
                _ => throw new InvalidOperationException("Unexpected economy verb")
            }, defaultValue);

            Assert.Equal(defaultValue, Read(Parse()));
            Assert.False(Read(Parse("--" + option, "FaLsE")));
            Assert.True(Read(Parse("--" + option, "TrUe")));
            var invalid = RunConsole(args.Concat(new[] { "--" + option, "maybe" }).ToArray());
            Assert.NotEqual(0, invalid.exit);
            Assert.Contains("--" + option + " expects true or false", invalid.output);
            var missing = RunConsole(args.Concat(new[] { "--" + option }).ToArray());
            Assert.NotEqual(0, missing.exit);
            Assert.Contains("--" + option + " expects true or false", missing.output);
            if (verb == "economy-analyze") Assert.True(Assert.IsType<Attax.Console.EconomyAnalyzeOptions>(Assert.IsType<CommandLine.Parsed<object>>(Parse("--apply")).Value).Apply);
            if (verb == "economy") Assert.True(Assert.IsType<Attax.Console.EconomyOptions>(Assert.IsType<CommandLine.Parsed<object>>(Parse("--apply")).Value).Apply);
        }

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
                var r = RunConsole("economy-sim", "--boards", "default", "--games", "1", "--red", "classic:depth=1,quiescence=false", "--abilities", "true", "--opening-plies", "4", "--parallel", "1", "--out", log);
                Assert.True(r.exit == 0, r.output);
                Assert.Contains("SUMMARY games=1", r.output);
                Assert.False(File.Exists(log + ".tmp"));
                string first = File.ReadLines(log).First();
                Assert.StartsWith("{", first);
                var record = Assert.Single(EconomyLog.Read(log));
                Assert.Equal(4, record.OpeningPliesPlayed);
                Assert.Equal(record.RedTurnStarts, record.RedIncome.TurnStartCredits.Count);
                Assert.DoesNotContain(record.Casts, c => c.Ply < 4);
                // the training validator must reject it, proving the formats stay separate
                Assert.NotEqual(0, RunConsole("validate-log", "--path", log).exit);
            } finally {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Telemetry_RejectsLegacyAndMixedOpenings_ButReportsUncappedIncomeSeparately() {
            EconomyGameRecord Row(int openingPlies) => new EconomyGameRecord {
                Playable = 48, Plies = 6, OpeningPlies = openingPlies, OpeningPliesPlayed = openingPlies, OpeningPosition = "ABCDEF0000000000",
                TelemetryMethod = Attax.Agents.RulesGameRunner.TelemetryMethod, RedTurnStarts = 1, BlueTurnStarts = 1,
                RedIncome = new EconomySideIncome { OwnTurns = 1, CaptureCredits = 4, CreditsLostToCap = 10, TurnStartCredits = new() { 0 } },
                BlueIncome = new EconomySideIncome { OwnTurns = 1, CaptureCredits = 6, CreditsLostToCap = 0, TurnStartCredits = new() { 3 } }
            };
            var valid = Row(4);
            Assert.True(Attax.Console.Program.HasEconomyTelemetry(new[] { valid }, 300));
            Assert.Equal(5, Assert.Single(Attax.Console.Program.BuildIncomeGroups(new[] { valid })).MeanPerTurn);
            Assert.Equal(10, Assert.Single(Attax.Console.Program.BuildIncomeGroups(new[] { valid }, uncapped: true)).MeanPerTurn);
            Assert.False(Attax.Console.Program.HasEconomyTelemetry(new[] { valid, new EconomyGameRecord() }, 300));
            Assert.False(Attax.Console.Program.HasEconomyTelemetry(new[] { valid, Row(2) }, 300));
            valid.RedIncome.TurnStartCredits.Clear();
            Assert.False(Attax.Console.Program.HasEconomyTelemetry(new[] { valid }, 300));
        }

        [Fact]
        public void LogIntegrity_RejectsEvenUniformMismatchesAgainstTheAnalysisInputs() {
            var ctx = EconomyContext.Load(null, null);
            EconomyGameRecord Record() => new EconomyGameRecord {
                RulesHash = ctx.RulesHash, AiConfigHash = ctx.AiHash, EngineParamsHash = ctx.EngineParamsHash,
                Revision = ctx.Revision, BuildId = ctx.BuildId, SamplingMethod = Attax.Agents.RulesGameRunner.SamplingMethod, Terminal = true
            };
            foreach (var (name, alter) in new (string, Action<EconomyGameRecord>)[] {
                ("rules hash", r => r.RulesHash = "wrong"), ("AI config hash", r => r.AiConfigHash = "wrong"),
                ("engine params hash", r => r.EngineParamsHash = "wrong"), ("source revision", r => r.Revision = "wrong"),
                ("build ID", r => r.BuildId = "wrong"), ("sampling method", r => r.SamplingMethod = "")
            }) {
                var a = Record(); var b = Record();
                alter(a);
                var warnings = Attax.Console.Program.CheckLogIntegrity(new[] { a, b }, ctx, out bool blocked);
                Assert.True(blocked);
                Assert.Contains(warnings, w => w.StartsWith(name, StringComparison.Ordinal));
                alter(b);
                warnings = Attax.Console.Program.CheckLogIntegrity(new[] { a, b }, ctx, out blocked);
                Assert.True(blocked);
                Assert.Contains(warnings, w => w.StartsWith(name, StringComparison.Ordinal));
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
