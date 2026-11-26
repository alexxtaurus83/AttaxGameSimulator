using System.Diagnostics;
using System.Globalization;
using System.Text;
using Attax.Agents;
using Attax.Console.Economy;
using Attax.Core.Utils;

namespace Attax.Console {
    // Economy calibration verbs (economy-sim, economy-analyze, economy). Additive: training verbs and the ATLG log are not touched.
    partial class Program {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static void RequireEconomyBooleanValues(string[] args) {
            if (args.Length == 0) return;
            var verb = args[0];
            bool sim = verb.Equals("economy-sim", StringComparison.OrdinalIgnoreCase);
            bool analyze = verb.Equals("economy-analyze", StringComparison.OrdinalIgnoreCase) || verb.Equals("economy", StringComparison.OrdinalIgnoreCase);
            if (!sim && !analyze) return;
            // CommandLineParser silently drops a value option when its argument is missing.
            for (int i = 1; i < args.Length; i++) {
                var option = args[i];
                if (option == "--") break;
                bool needsValue = sim
                    ? option.Equals("--abilities", StringComparison.OrdinalIgnoreCase) || option.Equals("--swap-colors", StringComparison.OrdinalIgnoreCase)
                    : option.Equals("--quiescence", StringComparison.OrdinalIgnoreCase);
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

        // ------------------------------------------------------------------------------------------------ economy-sim

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
            var records = RunSession(settings);
            EconomyLog.Write(o.Out, records);
            System.Console.WriteLine($"wrote {records.Count} game record(s) to {o.Out}");
        }

        static List<EconomyGameRecord> RunSession(EconomySimSettings settings) {
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
            return records;
        }

        static void PlayerSpec_Check(string text) {
            var spec = Attax.Play.PlayerSpec.Parse(text);
            if (!spec.IsSearch) throw new ArgumentException($"Economy players must be search players (classic or value), got '{text}'.");
        }

        // ------------------------------------------------------------------------------------------------ economy-analyze

        static void RunEconomyAnalyze(EconomyAnalyzeOptions o) {
            bool quiescence = ReadEconomyBoolean("quiescence", o.Quiescence, true);
            var files = EconomyLog.ResolveInputs(o.In);
            var records = new List<EconomyGameRecord>();
            bool sessionFilesMatch = true;
            foreach (string f in files) {
                var session = EconomyLog.Read(f);
                sessionFilesMatch &= session.Count > 0 && session.All(r => r.Tag == Path.GetFileNameWithoutExtension(f));
                records.AddRange(session);
            }
            if (records.Count == 0) throw new InvalidOperationException("The economy logs contain no games.");
            var ctx = EconomyContext.Load(o.Rules, o.AiConfig);
            var anchors = EconomyAnchors.Load(o.Anchors, o.Tolerance);
            RunAnalysis(new AnalysisRequest {
                Mode = "economy-analyze", Records = records, Files = files, Context = ctx, Anchors = anchors, OutDir = o.Out, VerifyGames = o.VerifyGames,
                Boards = o.Boards, Depth = o.Depth, Quiescence = quiescence, Iterations = o.Iterations, Seed = o.Seed, Parallel = o.Parallel, HoldoutGames = o.HoldoutGames,
                Apply = o.Apply, EconomyDoc = o.EconomyDoc, SessionFilesMatch = sessionFilesMatch
            });
        }

        // ------------------------------------------------------------------------------------------------ economy

        static void RunEconomy(EconomyOptions o) {
            bool quiescence = ReadEconomyBoolean("quiescence", o.Quiescence, true);
            var ctx = EconomyContext.Load(o.Rules, o.AiConfig);
            var anchors = EconomyAnchors.Load(o.Anchors, o.Tolerance);
            Directory.CreateDirectory(o.Out);
            string spec = $"classic:depth={o.Depth},quiescence={(quiescence ? "true" : "false")}";
            var boards = EconomySim.ResolveBoards(o.Boards);
            System.Console.WriteLine($"economy: Pass 0 (abilities off, {o.Games} games) and Pass 1 (abilities on, {o.Games} games, {o.SampleValues} value samples) on {boards.Count} board(s), players {spec}");

            string p0 = Path.Combine(o.Out, "economy_pass0.jsonl"), p1 = Path.Combine(o.Out, "economy_pass1.jsonl");
            var pass0 = RunSession(new EconomySimSettings {
                Boards = boards, Games = o.Games, Red = spec, Blue = spec, Abilities = false, SampleValues = 0, OpeningPlies = o.OpeningPlies, Seed = o.Seed, Parallel = o.Parallel, Tag = "pass0", Context = ctx
            });
            EconomyLog.Write(p0, pass0);
            var pass1 = RunSession(new EconomySimSettings {
                Boards = boards, Games = o.Games, Red = spec, Blue = spec, Abilities = true, SampleValues = Math.Max(0, o.SampleValues), OpeningPlies = o.OpeningPlies, Seed = o.Seed, Parallel = o.Parallel, Tag = "pass1", Context = ctx
            });
            EconomyLog.Write(p1, pass1);

            RunAnalysis(new AnalysisRequest {
                Mode = "economy", Records = pass0.Concat(pass1).ToList(), Files = new List<string> { p0, p1 }, Context = ctx, Anchors = anchors, OutDir = o.Out,
                VerifyGames = o.VerifyGames, Boards = o.Boards, Depth = o.Depth, Quiescence = quiescence, Iterations = o.Iterations, Seed = o.Seed,
                Parallel = o.Parallel, Apply = o.Apply, EconomyDoc = o.EconomyDoc, HoldoutGames = o.HoldoutGames
            });
        }

        // ------------------------------------------------------------------------------------------------ shared analysis

        internal sealed class AnalysisRequest {
            public string Mode = "";
            public List<EconomyGameRecord> Records = new List<EconomyGameRecord>();
            public List<string> Files = new List<string>();
            public EconomyContext Context = null!;
            public EconomyAnchors Anchors = null!;
            public string OutDir = "";
            public int VerifyGames, HoldoutGames, Depth, Iterations, Seed, Parallel;
            public bool Quiescence, Apply;
            public bool SessionFilesMatch;
            public string Boards = "all";
            public string? EconomyDoc;
        }

        internal static List<IncomeGroup> BuildIncomeGroups(IEnumerable<EconomyGameRecord> records, bool uncapped = false) {
            return records.Where(r => !r.Abilities).GroupBy(r => r.Playable).OrderByDescending(g => g.Key)
                .Select(g => new IncomeGroup(g.Key, g.Sum(r => r.RedIncome.OwnTurns + r.BlueIncome.OwnTurns), g.Sum(r =>
                    (double)r.RedIncome.Income + r.BlueIncome.Income + (uncapped ? (r.RedIncome.CreditsLostToCap ?? 0) + (r.BlueIncome.CreditsLostToCap ?? 0) : 0))))
                .ToList();
        }

        /// <summary>Reject release calibration when inputs cannot be traced to the same build, configs and sampling method.</summary>
        internal static List<string> CheckLogIntegrity(IReadOnlyList<EconomyGameRecord> records, EconomyContext ctx, out bool blocking) {
            var warnings = new List<string>();
            bool block = false;
            void Check(string name, Func<EconomyGameRecord, string> selector, string expected) {
                var values = records.Select(selector).Distinct().ToList();
                if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]) || values[0] != expected) {
                    warnings.Add($"{name} is missing, mixed, or differs from the analysis input (expected {expected}; logged {string.Join(", ", values)}).");
                    block = true;
                }
            }
            Check("rules hash", r => r.RulesHash, ctx.RulesHash);
            Check("AI config hash", r => r.AiConfigHash, ctx.AiHash);
            Check("engine params hash", r => r.EngineParamsHash, ctx.EngineParamsHash);
            Check("source revision", r => r.Revision, ctx.Revision);
            Check("build ID", r => r.BuildId, ctx.BuildId);
            Check("sampling method", r => r.SamplingMethod, RulesGameRunner.SamplingMethod);
            if (!HasEconomyTelemetry(records, ctx.Rules.economy.maxCredits)) {
                warnings.Add("Economy telemetry is missing, invalid, or mixed across opening settings; legacy logs are diagnostic only.");
                block = true;
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(ctx.Revision, @"\Ashared=[0-9a-f]{40};simulator=[0-9a-f]{40};state=clean\z")) {
                warnings.Add("The analysis build does not identify clean, full Shared and Simulator source revisions.");
                block = true;
            }
            if (records.Any(r => r.Values.Any(v =>
                !RulesGameRunner.IsEligibleSampleTurn(v.OwnTurn, v.Side switch { "Red" => r.RedTurnStarts, "Blue" => r.BlueTurnStarts, _ => 0 }) ||
                v.Ply < 0 || v.Ply >= r.Plies))) {
                warnings.Add("A value sample lies outside its side's own-turn window or recorded game.");
                block = true;
            }
            if (records.Any(r => !r.Terminal)) warnings.Add($"{records.Count(r => !r.Terminal)} game(s) ended at the ply cap");
            blocking = block;
            return warnings;
        }

        internal static bool HasEconomyTelemetry(IReadOnlyList<EconomyGameRecord> records, int cap) {
            if (records.Count == 0 || records[0].OpeningPlies is not (>= 0 and <= 4)) return false;
            int requested = records[0].OpeningPlies!.Value;
            bool SideValid(EconomySideIncome side, int turns) => side.CreditsLostToCap is >= 0 &&
                side.TurnStartCredits is { } credits && credits.Count == turns && credits.All(c => c >= 0 && c <= cap);
            return records.All(r => r.TelemetryMethod == RulesGameRunner.TelemetryMethod && r.OpeningPlies == requested &&
                r.OpeningPliesPlayed == Math.Min(requested, r.Plies) && r.OpeningPosition?.Length == 16 &&
                SideValid(r.RedIncome, r.RedTurnStarts) && SideValid(r.BlueIncome, r.BlueTurnStarts));
        }

        static void RunAnalysis(AnalysisRequest q) {
            if (q.HoldoutGames < 0) throw new ArgumentException("--holdout-games must be >= 0.");
            var boards = EconomySim.ResolveBoards(q.Boards);
            var input = new ReportInputs {
                Records = q.Records, InputFiles = q.Files, Context = q.Context, Anchors = q.Anchors, VerifyGames = q.VerifyGames, Iterations = q.Iterations, Seed = q.Seed,
                Mode = q.Mode, VerifyPlayer = $"classic:depth={q.Depth},quiescence={(q.Quiescence ? "true" : "false")}"
            };
            input.Warnings.AddRange(CheckLogIntegrity(q.Records, q.Context, out bool blocking));
            if (q.Mode == "economy-analyze" && (!q.SessionFilesMatch || !HasCompleteOvernightSessions(q.Files, q.Records))) {
                input.Warnings.Add("Release analysis requires all six expected, complete overnight sessions without duplicate inputs.");
                blocking = true;
            }
            input.DraftOnly = blocking;
            input.Income = BuildIncomeGroups(q.Records);
            bool hasTelemetry = HasEconomyTelemetry(q.Records, q.Context.Rules.economy.maxCredits);
            if (hasTelemetry) {
                input.UncappedIncome = BuildIncomeGroups(q.Records, uncapped: true);
                input.OpeningPlies = q.Records[0].OpeningPlies!.Value;
                int turnIndex = q.Anchors.WallAffordableByOwnTurn - 1;
                input.WallBalances = q.Records.Where(r => !r.Abilities && r.Playable == PriceSolver.ReferencePlayable)
                    .SelectMany(r => new[] { r.RedIncome.TurnStartCredits!, r.BlueIncome.TurnStartCredits! })
                    .Where(credits => turnIndex < credits.Count).Select(credits => credits[turnIndex]).ToArray();
                if (input.WallBalances.Length == 0) {
                    input.Warnings.Add("No measured reference-board balances at the Wall target turn; cannot apply.");
                    input.DraftOnly = blocking = true;
                }
            }
            var values = Calibrator.SummarizeValues(q.Records.Where(r => r.Abilities).SelectMany(r => r.Values));
            var economy = q.Context.Rules.economy;

            Func<CandidateEconomy, FrequencyMeasure>? measure = null;
            if (q.VerifyGames > 0 && !(q.Apply && blocking)) {
                string spec = input.VerifyPlayer;
                measure = candidate => MeasureFrequency(q, candidate, boards, spec, input.OpeningPlies, q.VerifyGames, q.Seed);
            }
            var result = Calibrator.Solve(input.UncappedIncome ?? input.Income, values, economy, q.Anchors, q.Iterations, measure,
                input.WallBalances.Length == 0 ? null : input.WallBalances.Average(), hasTelemetry ? q.Context.Ai.creditValueChips : null);
            if (result.WallTurnsOnReference > 0 && input.UncappedIncome != null) {
                double credited = input.Income.First(g => g.Playable == PriceSolver.ReferencePlayable).MeanPerTurn;
                result.WallTurnsOnReference = credited > 0 ? result.BasePrices[AbilityKind.Wall][0] / credited : 0;
            }
            if (result.Converged && q.HoldoutGames > 0 && hasTelemetry) {
                input.HoldoutSeed = CombineSeed(q.Seed, 77871);
                var candidate = new CandidateEconomy(result.BasePrices, result.Fit.Alpha, result.Fit.MinFactor, result.CreditValueChips);
                Calibrator.CheckHoldout(result, MeasureFrequency(q, candidate, boards, input.VerifyPlayer, input.OpeningPlies, q.HoldoutGames, input.HoldoutSeed.Value), q.Anchors);
            }

            string reason = null!;
            bool canApply = q.Apply;
            if (q.Apply) {
                if (q.VerifyGames <= 0) reason = "--apply needs --verify-games > 0";
                else if (blocking) reason = "the input logs are inconsistent (see Diagnostics)";
                else if (!q.Context.FromSource) reason = "rules/AI configs were not resolved to the Shared checkout used for the build; pass --rules/--ai-config explicitly for an intentional override";
                else if (q.VerifyGames < boards.Count || q.HoldoutGames < boards.Count) reason = "--apply needs at least one tuning and one holdout game per selected board";
                else if (!result.Converged || Calibrator.HasFailures(result)) reason = "the calibration did not converge on both tuning and holdout games";
                canApply = reason == null;
                if (!canApply) result.Failures.Add("apply rejected: " + reason);
            }

            string appliedDoc = "";
            if (canApply) {
                appliedDoc = ApplyCalibration(q, result, input);
                input.Applied = true;
            }

            Directory.CreateDirectory(q.OutDir);
            File.WriteAllText(Path.Combine(q.OutDir, "economy-report.md"), EconomyReport.Markdown(result, input));
            File.WriteAllText(Path.Combine(q.OutDir, "economy-report.json"), EconomyReport.Json(result, input));
            System.Console.WriteLine($"report: {Path.Combine(q.OutDir, "economy-report.md")}");

            if (result.Converged && !Calibrator.HasFailures(result) && !input.DraftOnly) {
                var f = result.Final!;
                System.Console.WriteLine($"CONVERGED casts/side/game={f.CastsPerSide:0.00} expensive/side/game={f.ExpensivePerSide:0.00} alpha={result.Fit.Alpha:0.00} minFactor={result.Fit.MinFactor:0.00} creditValueChips={result.CreditValueChips.ToString("0.#####", Inv)} applied={input.Applied}{(appliedDoc.Length > 0 ? " doc=" + appliedDoc : "")}");
            } else {
                System.Console.WriteLine(input.DraftOnly ? "DRAFT ONLY: input integrity checks failed; see Diagnostics."
                    : "NOT CONVERGED: " + string.Join("; ", result.Failures.Where(x => !x.StartsWith("note:", StringComparison.Ordinal))));
            }
            if (q.Apply && !input.Applied) throw new InvalidOperationException("--apply rejected: " + reason);
        }

        internal static bool HasCompleteOvernightSessions(IReadOnlyList<string> files, IReadOnlyList<EconomyGameRecord> records) {
            var expected = new[] { "s01_income_d2q0", "s02_small_d1q1_vs_d2q0", "s03_d2q0_vs_d3q1", "s04_mirror_d2q0", "s05_mirror_d2q1", "s06_shipped_d3q1" };
            if (files.Count != expected.Length || !expected.ToHashSet().SetEquals(files.Select(f => Path.GetFileNameWithoutExtension(f)))) return false;
            var sessions = records.ToLookup(r => r.Tag);
            if (sessions.Count != expected.Length) return false;
            return expected.All(tag => {
                var rows = sessions[tag].OrderBy(r => r.Game).ToArray();
                return rows.Length > 0 && rows.Length == rows[0].SessionGameCount &&
                    rows.Select((r, i) => r.Game == i && r.SessionGameCount == rows.Length && r.Abilities == (tag != expected[0])).All(ok => ok);
            });
        }

        static FrequencyMeasure MeasureFrequency(AnalysisRequest q, CandidateEconomy candidate, IReadOnlyList<string> boards, string spec, int openingPlies, int games, int seed) {
            var rules = Calibrator.ApplyTo(q.Context.Rules, candidate.Prices, candidate.Alpha, candidate.MinFactor);
            var errors = rules.Validate();
            if (errors.Count > 0) throw new InvalidOperationException("Candidate prices are invalid: " + string.Join("; ", errors));
            var ai = CloneAi(q.Context.Ai);
            ai.creditValueChips = candidate.CreditValueChips;
            ai.aiAbilitiesEnabled = true;
            var ctx = q.Context.With(rules, ai);
            var records = EconomySim.Run(new EconomySimSettings {
                Boards = boards, Games = games, Red = spec, Blue = spec, Abilities = true, SampleValues = 0, OpeningPlies = openingPlies,
                Seed = seed, Parallel = q.Parallel, Tag = "verify", Context = ctx
            });
            return new FrequencyMeasure(EconomyReport.CastsPerSide(records), EconomyReport.ExpensivePerSide(records), records.Count);
        }

        internal static AiAbilityConfig CloneAi(AiAbilityConfig s) => new AiAbilityConfig {
            schemaVersion = s.schemaVersion, aiAbilitiesEnabled = s.aiAbilitiesEnabled, creditValueChips = s.creditValueChips, minGainChips = s.minGainChips,
            maxCandidatesVerified = s.maxCandidatesVerified, verifyDepthOffset = s.verifyDepthOffset, savingEnabled = s.savingEnabled
        };

        // ------------------------------------------------------------------------------------------------ apply

        /// <summary>Writes the three outputs transactionally: validates every candidate first and restores all previous contents on any failure.</summary>
        static string ApplyCalibration(AnalysisRequest q, CalibrationResult result, ReportInputs input) {
            var rules = Calibrator.ApplyTo(q.Context.Rules, result.BasePrices, result.Fit.Alpha, result.Fit.MinFactor);
            var errors = rules.Validate();
            if (errors.Count > 0) throw new InvalidOperationException("Calibrated rules are invalid: " + string.Join("; ", errors));
            var ai = CloneAi(q.Context.Ai);
            ai.creditValueChips = result.CreditValueChips;
            var aiErrors = ai.Validate();
            if (aiErrors.Count > 0) throw new InvalidOperationException("Calibrated AI config is invalid: " + string.Join("; ", aiErrors));

            string docPath = q.EconomyDoc ?? EconomyContext.FindEconomyDoc()
                ?? throw new FileNotFoundException("docs/ECONOMY.md not found; pass --economy-doc or set ATTAX_UNITY_REPO.");
            if (!File.Exists(docPath)) throw new FileNotFoundException($"Economy document not found: {docPath}");
            string oldDoc = File.ReadAllText(docPath);
            input.Applied = true;
            string newDoc = EconomyReport.ReplaceBlock(oldDoc, EconomyReport.Tables(result, input));
            input.Applied = false;

            string rulesPath = q.Context.RulesPath, aiPath = q.Context.AiPath;
            string oldRules = File.ReadAllText(rulesPath), oldAi = File.ReadAllText(aiPath);
            string newRules = RenderRules(rules, DetectNewline(oldRules)), newAi = RenderAi(ai, DetectNewline(oldAi));

            var written = new List<(string path, string old)>();
            try {
                WriteAtomic(rulesPath, newRules); written.Add((rulesPath, oldRules));
                WriteAtomic(aiPath, newAi); written.Add((aiPath, oldAi));
                WriteAtomic(docPath, newDoc); written.Add((docPath, oldDoc));
                var reloaded = EconomyContext.Load(rulesPath, aiPath);
                if (reloaded.Rules.Fingerprint() != rules.Fingerprint()) throw new InvalidDataException("Written rules do not round-trip.");
                if (EconomyReport.ExtractBlock(File.ReadAllText(docPath)) != EconomyReport.ExtractBlock(newDoc)) throw new InvalidDataException("Written economy tables do not round-trip.");
            } catch (Exception) {
                foreach (var (path, old) in written) File.WriteAllText(path, old, new UTF8Encoding(false));
                throw;
            }
            return docPath;
        }

        static string DetectNewline(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

        static void WriteAtomic(string path, string text) {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            File.Move(tmp, path, true);
        }

        static string Arr(int[] values) => "[" + string.Join(", ", values) + "]";

        /// <summary>Same layout as the shipped game-rules.json so diffs only show changed values.</summary>
        internal static string RenderRules(GameRulesConfig r, string nl) {
            var e = r.economy;
            var c = e.abilityCosts;
            var sb = new StringBuilder();
            sb.Append("{").Append(nl);
            sb.Append($"  \"schemaVersion\": {r.schemaVersion},").Append(nl);
            sb.Append($"  \"winRule\": \"{r.winRule}\",").Append(nl);
            sb.Append("  \"economy\": {").Append(nl);
            sb.Append($"    \"enabled\": {(e.enabled ? "true" : "false")},").Append(nl);
            sb.Append($"    \"startingCredits\": {e.startingCredits},").Append(nl);
            sb.Append($"    \"maxCredits\": {e.maxCredits},").Append(nl);
            sb.Append($"    \"captureReward\": {e.captureReward},").Append(nl);
            sb.Append($"    \"footprintReward\": {e.footprintReward},").Append(nl);
            sb.Append("    \"abilityCosts\": {").Append(nl);
            sb.Append($"      \"superJump\": {Arr(c.superJump)},").Append(nl);
            sb.Append($"      \"convert\": {Arr(c.convert)},").Append(nl);
            sb.Append($"      \"block\": {Arr(c.block)},").Append(nl);
            sb.Append($"      \"wall\": {Arr(c.wall)},").Append(nl);
            sb.Append($"      \"doubleMove\": {Arr(c.doubleMove)},").Append(nl);
            sb.Append($"      \"spawn\": {Arr(c.spawn)},").Append(nl);
            sb.Append($"      \"superClone\": {Arr(c.superClone)},").Append(nl);
            sb.Append($"      \"unblock\": {Arr(c.unblock)},").Append(nl);
            sb.Append($"      \"bomb\": {Arr(c.bomb)}").Append(nl);
            sb.Append("    },").Append(nl);
            sb.Append("    \"boardScaling\": {").Append(nl);
            sb.Append($"      \"referencePlayable\": {e.boardScaling.referencePlayable},").Append(nl);
            sb.Append($"      \"alpha\": {e.boardScaling.alpha.ToString("0.0#", Inv)},").Append(nl);
            sb.Append($"      \"minFactor\": {e.boardScaling.minFactor.ToString("0.0#", Inv)},").Append(nl);
            sb.Append($"      \"roundStep\": {e.boardScaling.roundStep}").Append(nl);
            sb.Append("    }").Append(nl);
            sb.Append("  },").Append(nl);
            sb.Append("  \"turn\": {").Append(nl);
            sb.Append($"    \"maxAbilityActionsPerTurn\": {r.turn.maxAbilityActionsPerTurn}").Append(nl);
            sb.Append("  }").Append(nl);
            sb.Append("}");
            return sb.ToString();
        }

        internal static string RenderAi(AiAbilityConfig a, string nl) {
            var sb = new StringBuilder();
            sb.Append("{").Append(nl);
            sb.Append($"  \"schemaVersion\": {a.schemaVersion},").Append(nl);
            sb.Append($"  \"aiAbilitiesEnabled\": {(a.aiAbilitiesEnabled ? "true" : "false")},").Append(nl);
            sb.Append($"  \"creditValueChips\": {a.creditValueChips.ToString("0.0####", Inv)},").Append(nl);
            sb.Append($"  \"minGainChips\": {a.minGainChips.ToString("0.0#", Inv)},").Append(nl);
            sb.Append($"  \"maxCandidatesVerified\": {a.maxCandidatesVerified},").Append(nl);
            sb.Append($"  \"verifyDepthOffset\": {a.verifyDepthOffset},").Append(nl);
            sb.Append($"  \"savingEnabled\": {(a.savingEnabled ? "true" : "false")}").Append(nl);
            sb.Append("}");
            return sb.ToString();
        }
    }
}
