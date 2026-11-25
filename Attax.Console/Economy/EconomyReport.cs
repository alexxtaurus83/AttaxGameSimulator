using System.Globalization;
using System.Text;
using System.Text.Json;
using Attax.Core.Utils;

namespace Attax.Console.Economy {
    /// <summary>Everything a report needs besides the calibration result: provenance and measured breakdowns.</summary>
    internal sealed class ReportInputs {
        public List<EconomyGameRecord> Records = new List<EconomyGameRecord>();
        public List<IncomeGroup> Income = new List<IncomeGroup>();
        public List<string> InputFiles = new List<string>();
        public EconomyContext Context = null!;
        public EconomyAnchors Anchors = new EconomyAnchors();
        public int VerifyGames;
        public int Iterations;
        public int Seed;
        public string VerifyPlayer = "";
        public string Mode = "";
        public bool Applied;
        public List<string> Warnings = new List<string>();
    }

    internal static class EconomyReport {
        public const string BeginMarker = "<!-- economy:begin -->";
        public const string EndMarker = "<!-- economy:end -->";
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---------------------------------------------------------------- shared price helpers

        /// <summary>Price after board scaling for <paramref name="playable"/> cells (the same formula as the shared rules).</summary>
        public static int[] Resolve(int[] basePrices, EconomyRules economy, double alpha, double minFactor, int playable) {
            double factor = Math.Min(1.0, Math.Max(minFactor, Math.Pow((double)playable / economy.boardScaling.referencePlayable, alpha)));
            int step = economy.boardScaling.roundStep;
            var result = new int[basePrices.Length];
            long previous = 0;
            for (int i = 0; i < basePrices.Length; i++) {
                previous = Math.Max(previous + step, step * (long)Math.Round(basePrices[i] * factor / step, MidpointRounding.AwayFromZero));
                result[i] = (int)previous;
            }
            return result;
        }

        public static string Roman(int level) => level switch { 1 => "I", 2 => "II", 3 => "III", _ => level.ToString(Inv) };

        // ---------------------------------------------------------------- tables (identical in report and docs/ECONOMY.md)

        public static string Tables(CalibrationResult r, ReportInputs input) {
            var sb = new StringBuilder();
            var economy = input.Context.Rules.economy;
            var prices = r.BasePrices.Count > 0 ? r.BasePrices : CurrentPrices(input.Context.Rules);
            double alpha = r.Fit.Ok ? r.Fit.Alpha : economy.boardScaling.alpha;
            double minFactor = r.Fit.Ok ? r.Fit.MinFactor : economy.boardScaling.minFactor;
            bool calibrated = r.Converged;

            sb.AppendLine(calibrated ? "### Calibrated prices" : "### Draft prices (NOT converged; not applied)");
            sb.AppendLine();
            sb.AppendLine($"Input logs rules hash `{input.Context.RulesHash}`, revision `{input.Context.Revision}`, seed {input.Seed}, engine params `{input.Context.EngineParamsHash}`.");
            sb.AppendLine();
            sb.AppendLine("#### Board scaling and AI gate");
            sb.AppendLine();
            sb.AppendLine("| Parameter | Value |");
            sb.AppendLine("|---|---|");
            sb.AppendLine($"| referencePlayable | {economy.boardScaling.referencePlayable} |");
            sb.AppendLine($"| alpha | {alpha.ToString("0.00", Inv)} |");
            sb.AppendLine($"| minFactor | {minFactor.ToString("0.00", Inv)} |");
            sb.AppendLine($"| roundStep | {economy.boardScaling.roundStep} |");
            sb.AppendLine($"| creditValueChips | {(r.CreditValueChips > 0 ? r.CreditValueChips : input.Context.Ai.creditValueChips).ToString("0.#####", Inv)} |");
            sb.AppendLine($"| minGainChips | {input.Context.Ai.minGainChips.ToString("0.##", Inv)} |");
            sb.AppendLine();

            var playables = ChoosePlayableColumns(input.Records, economy.boardScaling.referencePlayable);
            sb.AppendLine("#### Reference prices (credits, 48 playable cells) and resolved prices on smaller boards");
            sb.AppendLine();
            sb.Append("| Ability | Level | Base (reference)");
            foreach (int p in playables.Where(p => p != economy.boardScaling.referencePlayable)) sb.Append($" | {p} cells");
            sb.AppendLine(" | Value P75 (chips) |");
            sb.Append("|---|---|---");
            foreach (int p in playables.Where(p => p != economy.boardScaling.referencePlayable)) sb.Append("|---");
            sb.AppendLine("|---|");
            var stats = r.Values.ToDictionary(v => (v.Kind, v.Level));
            for (int k = 1; k < 10; k++) {
                var kind = (AbilityKind)k;
                var ladder = prices[kind];
                for (int l = 1; l <= ladder.Length; l++) {
                    sb.Append($"| {kind} | {Roman(l)} | {ladder[l - 1]}");
                    foreach (int p in playables.Where(p => p != economy.boardScaling.referencePlayable))
                        sb.Append($" | {Resolve(ladder, economy, alpha, minFactor, p)[l - 1]}");
                    string value = stats.TryGetValue((kind, l), out var vs) && vs.P75.HasValue ? vs.P75.Value.ToString("0.00", Inv) : "n/a";
                    sb.AppendLine($" | {value} |");
                }
            }
            sb.AppendLine();

            sb.AppendLine("#### Income by playable cells (abilities off)");
            sb.AppendLine();
            sb.AppendLine("| Playable cells | Own turns | Credits/turn | Ratio to 48 | Fitted ratio |");
            sb.AppendLine("|---|---|---|---|---|");
            double referenceIncome = r.Fit.ReferenceIncomePerTurn;
            foreach (var g in input.Income.OrderByDescending(g => g.Playable)) {
                string ratio = referenceIncome > 0 ? (g.MeanPerTurn / referenceIncome).ToString("0.00", Inv) : "n/a";
                string fitted = r.Fit.Ok ? Math.Max(r.Fit.MinFactor, Math.Min(1.0, Math.Pow((double)g.Playable / PriceSolver.ReferencePlayable, r.Fit.Alpha))).ToString("0.00", Inv) : "n/a";
                sb.AppendLine($"| {g.Playable} | {g.OwnTurns.ToString("0", Inv)} | {g.MeanPerTurn.ToString("0.00", Inv)} | {ratio} | {fitted} |");
            }
            sb.AppendLine();

            sb.AppendLine("#### Value samples (Pass 1)");
            sb.AppendLine();
            sb.AppendLine("| Ability | Level | Samples | Positive | Terminal | No candidate | Unverified | Over cap | P75 gain (chips) |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var v in r.Values)
                sb.AppendLine($"| {v.Kind} | {Roman(v.Level)} | {v.Samples} | {v.Positive} | {v.Terminal} | {v.NoCandidate} | {v.Unverified} | {v.OverCap} | {(v.P75.HasValue ? v.P75.Value.ToString("0.00", Inv) : "n/a")} |");
            sb.AppendLine();

            sb.AppendLine("#### Anchors and convergence (Pass 2)");
            sb.AppendLine();
            var a = input.Anchors;
            sb.AppendLine($"Targets: Wall I affordable after {a.WallAffordableByOwnTurn} own turns; casts per side per game {a.CastsPerSidePerGame[0]}-{a.CastsPerSidePerGame[1]} (tolerance {a.Tolerance}); expensive casts per side per game {a.ExpensiveCastsPerSidePerGame}; level step >= {a.MinLevelStep}; Unblock I = Wall I: {a.UnblockL1EqualsWall}.");
            sb.AppendLine();
            if (r.Iterations.Count == 0) sb.AppendLine("Pass 2 was not run.");
            else {
                sb.AppendLine("| Iteration | Credits per chip | Max price | Casts/side/game | Expensive/side/game | Verdict |");
                sb.AppendLine("|---|---|---|---|---|---|");
                foreach (var it in r.Iterations)
                    sb.AppendLine($"| {it.Iteration} | {it.Scalar.ToString("0.000", Inv)} | {it.MaxPrice} | {it.CastsPerSide.ToString("0.00", Inv)} | {it.ExpensivePerSide.ToString("0.00", Inv)} | {it.Verdict} |");
                if (r.WallTurnsOnReference > 0) sb.AppendLine().AppendLine($"Wall I costs {r.WallTurnsOnReference.ToString("0.00", Inv)} own turns of mean income on the 48-cell reference board.");
            }
            return sb.ToString().TrimEnd() + Environment.NewLine;
        }

        private static Dictionary<AbilityKind, int[]> CurrentPrices(GameRulesConfig rules) {
            var d = new Dictionary<AbilityKind, int[]>();
            for (int k = 1; k < 10; k++) d[(AbilityKind)k] = Enumerable.Range(1, GameRulesConfig.GetMaxLevel((AbilityKind)k)).Select(l => rules.GetAbilityCost((AbilityKind)k, l)).ToArray();
            return d;
        }

        // 48 (reference), the median and the smallest playable count seen in the logs.
        private static List<int> ChoosePlayableColumns(IReadOnlyList<EconomyGameRecord> records, int reference) {
            var counts = records.Select(r => r.Playable).Where(p => p > 0).Distinct().OrderBy(p => p).ToList();
            var result = new SortedSet<int> { reference };
            if (counts.Count > 0) { result.Add(counts[0]); result.Add(counts[counts.Count / 2]); }
            return result.OrderByDescending(p => p).ToList();
        }

        // ---------------------------------------------------------------- full markdown + json

        public static string Markdown(CalibrationResult r, ReportInputs input) {
            var sb = new StringBuilder();
            sb.AppendLine("# Economy calibration report");
            sb.AppendLine();
            sb.AppendLine($"- Mode: {input.Mode}; status: **{(r.Converged ? "CONVERGED" : "NOT CONVERGED")}**{(input.Applied ? "; configs APPLIED" : "; configs not modified")}");
            sb.AppendLine($"- Games analysed: {input.Records.Count} from {input.InputFiles.Count} log(s); verify games {input.VerifyGames}; iterations {input.Iterations}; verify player `{input.VerifyPlayer}`");
            sb.AppendLine($"- Rules `{input.Context.RulesPath}` (hash `{input.Context.RulesHash}`), AI config `{input.Context.AiPath}` (hash `{input.Context.AiHash}`)");
            sb.AppendLine($"- Engine params `{input.Context.EngineParamsHash}`, revision `{input.Context.Revision}`, seed {input.Seed}");
            foreach (string f in input.InputFiles) sb.AppendLine($"- Input: `{f}`");
            sb.AppendLine();
            if (r.Failures.Count > 0 || input.Warnings.Count > 0) {
                sb.AppendLine("## Diagnostics");
                sb.AppendLine();
                foreach (string f in r.Failures) sb.AppendLine($"- {f}");
                foreach (string w in input.Warnings) sb.AppendLine($"- {w}");
                sb.AppendLine();
            }
            sb.AppendLine("## Timing and sessions");
            sb.AppendLine();
            sb.AppendLine("| Session (tag) | Games | Wall ms/game | Plies/game | Search ms/game | Ability ms/game | Value sample ms/game | Casts/side/game | Expensive/side/game | Value samples |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
            foreach (var g in input.Records.GroupBy(x => x.Tag).OrderBy(g => g.Key, StringComparer.Ordinal)) {
                int n = g.Count();
                sb.AppendLine($"| {(g.Key.Length == 0 ? "(none)" : g.Key)} | {n} | {g.Average(x => x.WallMs).ToString("0", Inv)} | {g.Average(x => x.Plies).ToString("0.0", Inv)} | " +
                              $"{g.Average(x => x.SearchMsRed + x.SearchMsBlue).ToString("0", Inv)} | {g.Average(x => x.AbilityDecideMsRed + x.AbilityDecideMsBlue).ToString("0", Inv)} | {g.Average(x => x.ValueSampleMs).ToString("0", Inv)} | " +
                              $"{CastsPerSide(g).ToString("0.00", Inv)} | {ExpensivePerSide(g).ToString("0.00", Inv)} | {g.Sum(x => x.Values.Count)} |");
            }
            sb.AppendLine();
            sb.AppendLine("## Matchup breakdown (casts per game by player spec, abilities on)");
            sb.AppendLine();
            sb.AppendLine("| Player spec | Side-games | Casts/game | Expensive/game | Win share |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var row in RoleRows(input.Records.Where(x => x.Abilities)))
                sb.AppendLine($"| `{row.Spec}` | {row.Games} | {row.Casts.ToString("0.00", Inv)} | {row.Expensive.ToString("0.00", Inv)} | {row.WinShare.ToString("0.00", Inv)} |");
            sb.AppendLine();
            sb.AppendLine("## Casts by board (abilities on)");
            sb.AppendLine();
            sb.AppendLine("| Board | Playable | Games | Casts/side/game | Plies/game |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var g in input.Records.Where(x => x.Abilities).GroupBy(x => x.BoardId).OrderBy(g => g.Key, StringComparer.Ordinal))
                sb.AppendLine($"| {g.Key} | {g.First().Playable} | {g.Count()} | {CastsPerSide(g).ToString("0.00", Inv)} | {g.Average(x => x.Plies).ToString("0.0", Inv)} |");
            sb.AppendLine();
            sb.AppendLine("## Calibration tables");
            sb.AppendLine();
            sb.Append(Tables(r, input));
            return sb.ToString();
        }

        internal static double CastsPerSide(IEnumerable<EconomyGameRecord> g) {
            var list = g as IList<EconomyGameRecord> ?? g.ToList();
            return list.Count == 0 ? 0 : list.Sum(x => x.RedIncome.Casts + x.BlueIncome.Casts) / (2.0 * list.Count);
        }

        internal static double ExpensivePerSide(IEnumerable<EconomyGameRecord> g) {
            var list = g as IList<EconomyGameRecord> ?? g.ToList();
            if (list.Count == 0) return 0;
            int expensive = list.Sum(x => x.Casts.Count(c => Enum.TryParse(c.Kind, out AbilityKind k) && PriceSolver.IsExpensive(k, c.Level)));
            return expensive / (2.0 * list.Count);
        }

        private sealed record RoleRow(string Spec, int Games, double Casts, double Expensive, double WinShare);

        private static IEnumerable<RoleRow> RoleRows(IEnumerable<EconomyGameRecord> records) {
            var rows = new Dictionary<string, (int games, int casts, int exp, double wins)>();
            void Add(string spec, int casts, int exp, double win) {
                rows.TryGetValue(spec, out var cur);
                rows[spec] = (cur.games + 1, cur.casts + casts, cur.exp + exp, cur.wins + win);
            }
            foreach (var r in records) {
                int redExp = r.Casts.Count(c => c.Side == "Red" && Enum.TryParse(c.Kind, out AbilityKind k) && PriceSolver.IsExpensive(k, c.Level));
                int blueExp = r.Casts.Count(c => c.Side == "Blue" && Enum.TryParse(c.Kind, out AbilityKind k) && PriceSolver.IsExpensive(k, c.Level));
                Add(r.Red, r.RedIncome.Casts, redExp, r.Winner == 1 ? 1 : r.Winner == 0 ? 0.5 : 0);
                Add(r.Blue, r.BlueIncome.Casts, blueExp, r.Winner == -1 ? 1 : r.Winner == 0 ? 0.5 : 0);
            }
            return rows.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new RoleRow(kv.Key, kv.Value.games, (double)kv.Value.casts / kv.Value.games, (double)kv.Value.exp / kv.Value.games, kv.Value.wins / kv.Value.games));
        }

        public static string Json(CalibrationResult r, ReportInputs input) {
            var economy = input.Context.Rules.economy;
            object Ladder(IReadOnlyDictionary<AbilityKind, int[]> p) => p.OrderBy(kv => (int)kv.Key).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
            var doc = new {
                schemaVersion = 1,
                mode = input.Mode,
                converged = r.Converged,
                applied = input.Applied,
                failures = r.Failures,
                warnings = input.Warnings,
                games = input.Records.Count,
                inputFiles = input.InputFiles,
                verifyGames = input.VerifyGames,
                iterations = input.Iterations,
                seed = input.Seed,
                verifyPlayer = input.VerifyPlayer,
                hashes = new {
                    rules = input.Context.RulesHash, aiConfig = input.Context.AiHash, engineParams = input.Context.EngineParamsHash,
                    revision = input.Context.Revision, rulesPath = input.Context.RulesPath, aiConfigPath = input.Context.AiPath
                },
                anchors = input.Anchors,
                boardScaling = new { economy.boardScaling.referencePlayable, alpha = r.Fit.Ok ? r.Fit.Alpha : economy.boardScaling.alpha, minFactor = r.Fit.Ok ? r.Fit.MinFactor : economy.boardScaling.minFactor, economy.boardScaling.roundStep },
                referenceIncomePerTurn = r.Fit.ReferenceIncomePerTurn,
                excludedPlayables = r.Fit.ExcludedPlayables,
                creditValueChips = r.CreditValueChips,
                initialScalar = r.InitialScalar,
                finalScalar = r.FinalScalar,
                wallTurnsOnReference = r.WallTurnsOnReference,
                basePrices = Ladder(r.BasePrices),
                initialPrices = Ladder(r.InitialPrices),
                income = input.Income.OrderByDescending(g => g.Playable).Select(g => new { playable = g.Playable, ownTurns = g.OwnTurns, income = g.Income, perTurn = g.MeanPerTurn }),
                values = r.Values.Select(v => new { kind = v.Kind.ToString(), v.Level, v.Samples, v.Positive, v.Terminal, v.NoCandidate, v.Unverified, v.OverCap, p75 = v.P75 }),
                iterationLog = r.Iterations,
                final = r.Final,
                sessions = input.Records.GroupBy(x => x.Tag).Select(g => new {
                    tag = g.Key, games = g.Count(), wallMsPerGame = g.Average(x => x.WallMs), pliesPerGame = g.Average(x => x.Plies),
                    searchMsPerGame = g.Average(x => x.SearchMsRed + x.SearchMsBlue), abilityMsPerGame = g.Average(x => x.AbilityDecideMsRed + x.AbilityDecideMsBlue),
                    valueSampleMsPerGame = g.Average(x => x.ValueSampleMs), castsPerSideGame = CastsPerSide(g), expensivePerSideGame = ExpensivePerSide(g)
                }),
                castsByBoard = input.Records.Where(x => x.Abilities).GroupBy(x => x.BoardId).Select(g => new { board = g.Key, playable = g.First().Playable, games = g.Count(), castsPerSideGame = CastsPerSide(g) })
            };
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }

        // ---------------------------------------------------------------- docs/ECONOMY.md marker block

        public static string ReplaceBlock(string document, string tables) {
            int begin = document.IndexOf(BeginMarker, StringComparison.Ordinal);
            int end = document.IndexOf(EndMarker, StringComparison.Ordinal);
            if (begin < 0 || end < 0 || end < begin) throw new InvalidDataException($"The economy document needs {BeginMarker} and {EndMarker} markers (in that order).");
            string nl = document.Contains("\r\n") ? "\r\n" : "\n";
            string body = tables.Replace("\r\n", "\n").Replace("\n", nl);
            return document.Substring(0, begin + BeginMarker.Length) + nl + nl + body + nl + document.Substring(end);
        }

        public static string ExtractBlock(string document) {
            int begin = document.IndexOf(BeginMarker, StringComparison.Ordinal);
            int end = document.IndexOf(EndMarker, StringComparison.Ordinal);
            if (begin < 0 || end < begin) throw new InvalidDataException("Economy markers not found.");
            return document.Substring(begin + BeginMarker.Length, end - begin - BeginMarker.Length).Trim().Replace("\r\n", "\n");
        }
    }
}
