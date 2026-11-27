using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Attax.Console;
using Attax.Console.Economy;
using Attax.Data;
using CommandLine;
using Xunit;

namespace Attax.Core.Tests {
    /// <summary>Economy simulation and v4 (ATLG) training log generation remain separate.</summary>
    public class EconomyLogAndBackwardCompatTests {
        [Theory]
        [InlineData("abilities", true)]
        [InlineData("swap-colors", false)]
        public void EconomyBooleanOptions_RequireExplicitValuesAndKeepDefaults(string option, bool defaultValue) {
            using var parser = new Parser(settings => settings.HelpWriter = null);
            var args = new[] { "economy-sim", "--out", "unused" };
            bool Read(params string[] extra) {
                var parsed = Assert.IsType<Parsed<EconomySimOptions>>(parser.ParseArguments<EconomySimOptions>(args.Concat(extra))).Value;
                return Program.ReadEconomyBoolean(option, option == "abilities" ? parsed.Abilities : parsed.SwapColors, defaultValue);
            }

            Assert.Equal(defaultValue, Read());
            Assert.False(Read("--" + option, "FaLsE"));
            Assert.True(Read("--" + option, "TrUe"));
            var invalid = RunConsole(args.Concat(new[] { "--" + option, "maybe" }).ToArray());
            Assert.NotEqual(0, invalid.exit);
            Assert.Contains("--" + option + " expects true or false", invalid.output);
            var missing = RunConsole(args.Concat(new[] { "--" + option }).ToArray());
            Assert.NotEqual(0, missing.exit);
            Assert.Contains("--" + option + " expects true or false", missing.output);
        }

        [Theory]
        [InlineData("economy")]
        [InlineData("economy-analyze")]
        public void RemovedEconomyVerbs_AreRejected(string verb) {
            var result = RunConsole(verb, "--apply");
            Assert.NotEqual(0, result.exit);
            Assert.Contains($"Verb '{verb}' is not recognized", result.output);
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
                Assert.Equal(record.RedTurnStarts, record.RedIncome.TurnStartCredits!.Count);
                Assert.DoesNotContain(record.Casts, c => c.Ply < 4);
                Assert.NotEqual(0, RunConsole("validate-log", "--path", log).exit);
            } finally {
                Directory.Delete(dir, true);
            }
        }
    }
}
