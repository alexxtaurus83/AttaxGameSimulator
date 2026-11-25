using System.Diagnostics;
using System.Globalization;
using System.Text;
using Attax.Console.Economy;
using Attax.Core.Utils;

namespace Attax.Console {
    // Economy calibration verbs (economy-sim, economy-analyze, economy). Additive: training verbs and the ATLG log are not touched.
    partial class Program {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ------------------------------------------------------------------------------------------------ economy-sim

        static void RunEconomySim(EconomySimOptions o) {
            var ctx = EconomyContext.Load(o.Rules, o.AiConfig);
            var settings = new EconomySimSettings {
                Boards = EconomySim.ResolveBoards(o.Boards), Games = o.Games, Red = o.Red, Blue = o.Blue ?? o.Red, SwapColors = o.SwapColors,
                Abilities = o.Abilities, SampleValues = Math.Max(0, o.SampleValues), MaxPlies = o.MaxPlies, Seed = o.Seed, Parallel = o.Parallel,
                Tag = string.IsNullOrWhiteSpace(o.Tag) ? Path.GetFileNameWithoutExtension(o.Out) : o.Tag!, Context = ctx
            };
            PlayerSpec_Check(settings.Red); PlayerSpec_Check(settings.Blue);
            System.Console.WriteLine($"economy-sim: {settings.Boards.Count} board(s), {o.Games} {(o.SwapColors ? "opening pair(s) = " + 2 * o.Games + " games" : "game(s)")}, red={settings.Red}, blue={settings.Blue}, abilities={o.Abilities}, sample-values={settings.SampleValues}, rules={ctx.RulesHash}");
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
            var files = EconomyLog.ResolveInputs(o.In);
            var records = new List<EconomyGameRecord>();
            foreach (string f in files) records.AddRange(EconomyLog.Read(f));
            if (records.Count == 0) throw new InvalidOperationException("The economy logs contain no games.");
            var ctx = EconomyContext.Load(o.Rules, o.AiConfig);
            var anchors = EconomyAnchors.Load(o.Anchors, o.Tolerance);
            RunAnalysis(new AnalysisRequest {
                Mode = "economy-analyze", Records = records, Files = files, Context = ctx, Anchors = anchors, OutDir = o.Out, VerifyGames = o.VerifyGames,
                Boards = o.Boards, Depth = o.Depth, Quiescence = o.Quiescence, Iterations = o.Iterations, Seed = o.Seed, Parallel = o.Parallel,
                Apply = o.Apply, EconomyDoc = o.EconomyDoc
            });
        }

        // ------------------------------------------------------------------------------------------------ economy

        static void RunEconomy(EconomyOptions o) {
            var ctx = EconomyContext.Load(o.Rules, o.AiConfig);
            var anchors = EconomyAnchors.Load(o.Anchors, o.Tolerance);
            Directory.CreateDirectory(o.Out);
            string spec = $"classic:depth={o.Depth},quiescence={(o.Quiescence ? "true" : "false")}";
            var boards = EconomySim.ResolveBoards(o.Boards);
            System.Console.WriteLine($"economy: Pass 0 (abilities off, {o.Games} games) and Pass 1 (abilities on, {o.Games} games, {o.SampleValues} value samples) on {boards.Count} board(s), players {spec}");

            string p0 = Path.Combine(o.Out, "economy_pass0.jsonl"), p1 = Path.Combine(o.Out, "economy_pass1.jsonl");
            var pass0 = RunSession(new EconomySimSettings {
                Boards = boards, Games = o.Games, Red = spec, Blue = spec, Abilities = false, SampleValues = 0, Seed = o.Seed, Parallel = o.Parallel, Tag = "pass0", Context = ctx
            });
            EconomyLog.Write(p0, pass0);
            var pass1 = RunSession(new EconomySimSettings {
                Boards = boards, Games = o.Games, Red = spec, Blue = spec, Abilities = true, SampleValues = Math.Max(0, o.SampleValues), Seed = o.Seed, Parallel = o.Parallel, Tag = "pass1", Context = ctx
            });
            EconomyLog.Write(p1, pass1);

            RunAnalysis(new AnalysisRequest {
                Mode = "economy", Records = pass0.Concat(pass1).ToList(), Files = new List<string> { p0, p1 }, Context = ctx, Anchors = anchors, OutDir = o.Out,
                VerifyGames = o.VerifyGames, Boards = o.Boards, Depth = o.Depth, Quiescence = o.Quiescence, Iterations = o.Iterations, Seed = o.Seed,
                Parallel = o.Parallel, Apply = o.Apply, EconomyDoc = o.EconomyDoc
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
            public int VerifyGames, Depth, Iterations, Seed, Parallel;
            public bool Quiescence, Apply;
            public string Boards = "all";
            public string? EconomyDoc;
        }

        internal static List<IncomeGroup> BuildIncomeGroups(IEnumerable<EconomyGameRecord> records) {
            return records.Where(r => !r.Abilities).GroupBy(r => r.Playable).OrderByDescending(g => g.Key)
                .Select(g => new IncomeGroup(g.Key, g.Sum(r => r.RedIncome.OwnTurns + r.BlueIncome.OwnTurns), g.Sum(r => (double)(r.RedIncome.Income + r.BlueIncome.Income))))
                .ToList();
        }

        /// <summary>Logs must agree on rules, engine parameters and revision, otherwise the analysis mixes incomparable data.</summary>
        internal static List<string> CheckLogIntegrity(IReadOnlyList<EconomyGameRecord> records, EconomyContext ctx, out bool blocking) {
            var warnings = new List<string>();
            bool block = false;
            void Distinct(string name, Func<EconomyGameRecord, string> selector) {
                var values = records.Select(selector).Distinct().ToList();
                if (values.Count > 1) { warnings.Add($"logs disagree on {name}: {string.Join(", ", values)}"); block = true; }
            }
            Distinct("rules hash", r => r.RulesHash);
            Distinct("engine params hash", r => r.EngineParamsHash);
            Distinct("source revision", r => r.Revision);
            if (records.Any(r => r.RulesHash != ctx.RulesHash)) warnings.Add($"logs were generated with rules hash {records[0].RulesHash}, current rules hash is {ctx.RulesHash} (values and income are measured in chips and credits per turn, so they remain usable)");
            if (records.Any(r => r.EngineParamsHash != ctx.EngineParamsHash)) { warnings.Add("logs were generated with other engine parameters than the current engine-params.json"); block = true; }
            if (records.Any(r => !r.Terminal)) warnings.Add($"{records.Count(r => !r.Terminal)} game(s) ended at the ply cap");
            blocking = block;
            return warnings;
        }

        static void RunAnalysis(AnalysisRequest q) {
            var input = new ReportInputs {
                Records = q.Records, InputFiles = q.Files, Context = q.Context, Anchors = q.Anchors, VerifyGames = q.VerifyGames, Iterations = q.Iterations, Seed = q.Seed,
                Mode = q.Mode, VerifyPlayer = $"classic:depth={q.Depth},quiescence={(q.Quiescence ? "true" : "false")}"
            };
            input.Warnings.AddRange(CheckLogIntegrity(q.Records, q.Context, out bool blocking));
            input.Income = BuildIncomeGroups(q.Records);
            var values = Calibrator.SummarizeValues(q.Records.Where(r => r.Abilities).SelectMany(r => r.Values));
            var economy = q.Context.Rules.economy;

            Func<CandidateEconomy, FrequencyMeasure>? measure = null;
            if (q.VerifyGames > 0) {
                var boards = EconomySim.ResolveBoards(q.Boards);
                string spec = input.VerifyPlayer;
                measure = candidate => MeasureFrequency(q, candidate, boards, spec);
            }
            var result = Calibrator.Solve(input.Income, values, economy, q.Anchors, q.Iterations, measure);

            string reason = null!;
            bool canApply = q.Apply;
            if (q.Apply) {
                if (q.VerifyGames <= 0) reason = "--apply needs --verify-games > 0";
                else if (blocking) reason = "the input logs are inconsistent (see Diagnostics)";
                else if (!q.Context.FromSource) reason = "rules/AI configs were not resolved to the AttaxShared source files; pass --rules/--ai-config or set ATTAX_SHARED_REPO";
                else if (!result.Converged || Calibrator.HasFailures(result)) reason = "the calibration did not converge";
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

            if (result.Converged && !Calibrator.HasFailures(result)) {
                var f = result.Final!;
                System.Console.WriteLine($"CONVERGED casts/side/game={f.CastsPerSide:0.00} expensive/side/game={f.ExpensivePerSide:0.00} alpha={result.Fit.Alpha:0.00} minFactor={result.Fit.MinFactor:0.00} creditValueChips={result.CreditValueChips.ToString("0.#####", Inv)} applied={input.Applied}{(appliedDoc.Length > 0 ? " doc=" + appliedDoc : "")}");
            } else {
                System.Console.WriteLine("NOT CONVERGED: " + string.Join("; ", result.Failures.Where(x => !x.StartsWith("note:", StringComparison.Ordinal))));
            }
            if (q.Apply && !input.Applied) throw new InvalidOperationException("--apply rejected: " + reason);
        }

        static FrequencyMeasure MeasureFrequency(AnalysisRequest q, CandidateEconomy candidate, IReadOnlyList<string> boards, string spec) {
            var rules = Calibrator.ApplyTo(q.Context.Rules, candidate.Prices, candidate.Alpha, candidate.MinFactor);
            var errors = rules.Validate();
            if (errors.Count > 0) throw new InvalidOperationException("Candidate prices are invalid: " + string.Join("; ", errors));
            var ai = CloneAi(q.Context.Ai);
            ai.creditValueChips = candidate.CreditValueChips;
            ai.aiAbilitiesEnabled = true;
            var ctx = q.Context.With(rules, ai);
            var records = EconomySim.Run(new EconomySimSettings {
                Boards = boards, Games = q.VerifyGames, Red = spec, Blue = spec, Abilities = true, SampleValues = 0, Seed = q.Seed, Parallel = q.Parallel, Tag = "verify", Context = ctx
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
