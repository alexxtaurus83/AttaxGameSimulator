using System.Diagnostics;
using Attax.Console.Economy;

namespace Attax.Console {
    partial class Program {
        private static void RequireEconomyBooleanValues(string[] args) {
            if (args.Length == 0 || !args[0].Equals("economy-sim", StringComparison.OrdinalIgnoreCase)) return;
            // CommandLineParser silently drops a value option when its argument is missing.
            for (int i = 1; i < args.Length; i++) {
                var option = args[i];
                if (option == "--") break;
                bool needsValue = option.Equals("--abilities", StringComparison.OrdinalIgnoreCase) || option.Equals("--swap-colors", StringComparison.OrdinalIgnoreCase);
                if (needsValue && (i + 1 == args.Length || args[i + 1].StartsWith("-", StringComparison.Ordinal)))
                    throw new ArgumentException($"{option} expects true or false.");
            }
        }

        internal static bool ReadEconomyBoolean(string option, string? value, bool defaultValue) {
            if (value == null) return defaultValue;
            if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            throw new ArgumentException($"--{option} expects true or false, got '{value}'.");
        }

        static void RunEconomySim(EconomySimOptions o) {
            bool swapColors = ReadEconomyBoolean("swap-colors", o.SwapColors, false);
            bool abilities = ReadEconomyBoolean("abilities", o.Abilities, true);
            var ctx = EconomyContext.Load(o.Rules, o.AiConfig);
            var settings = new EconomySimSettings {
                Boards = EconomySim.ResolveBoards(o.Boards), Games = o.Games, Red = o.Red, Blue = o.Blue ?? o.Red, SwapColors = swapColors,
                Abilities = abilities, SampleValues = Math.Max(0, o.SampleValues), OpeningPlies = o.OpeningPlies, MaxPlies = o.MaxPlies, Seed = o.Seed, Parallel = o.Parallel,
                Tag = string.IsNullOrWhiteSpace(o.Tag) ? Path.GetFileNameWithoutExtension(o.Out) : o.Tag!, Context = ctx
            };
            PlayerSpec_Check(settings.Red); PlayerSpec_Check(settings.Blue);
            System.Console.WriteLine($"economy-sim: {settings.Boards.Count} board(s), {o.Games} {(settings.SwapColors ? "opening pair(s) = " + 2 * o.Games + " games" : "game(s)")}, red={settings.Red}, blue={settings.Blue}, abilities={settings.Abilities}, sample-values={settings.SampleValues}, rules={ctx.RulesHash}");
            var clock = Stopwatch.StartNew();
            int last = -1;
            settings.Progress = (done, total) => {
                int step = Math.Max(1, total / 10);
                if (done % step != 0 && done != total) return;
                if (Interlocked.Exchange(ref last, done) == done) return;
                System.Console.WriteLine($"  {done}/{total} games ({clock.Elapsed.TotalSeconds:0.0}s)");
            };
            var records = EconomySim.Run(settings);
            clock.Stop();
            System.Console.WriteLine(EconomySim.SummaryLine(records, clock.Elapsed.TotalSeconds));
            EconomyLog.Write(o.Out, records);
            System.Console.WriteLine($"wrote {records.Count} game record(s) to {o.Out}");
        }

        static void PlayerSpec_Check(string text) {
            var spec = Attax.Play.PlayerSpec.Parse(text);
            if (!spec.IsSearch) throw new ArgumentException($"Economy players must be search players (classic or value), got '{text}'.");
        }
    }
}
