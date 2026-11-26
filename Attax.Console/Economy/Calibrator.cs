using Attax.Core.Utils;

namespace Attax.Console.Economy {
    internal sealed record ValueStat(AbilityKind Kind, int Level, int Samples, int Positive, int Terminal, int NoCandidate, int Unverified, int OverCap, double? P75);

    internal sealed record FrequencyMeasure(double CastsPerSide, double ExpensivePerSide, int Games);

    internal sealed record IterationLog(int Iteration, double Scalar, int MaxPrice, double CastsPerSide, double ExpensivePerSide, string Verdict);

    /// <summary>The numbers a measurement needs to simulate candidate economy: reference prices, board scaling and the pinned AI gate value.</summary>
    internal sealed record CandidateEconomy(IReadOnlyDictionary<AbilityKind, int[]> Prices, double Alpha, double MinFactor, float CreditValueChips);

    internal sealed class CalibrationResult {
        public AlphaFit Fit = new AlphaFit();
        public List<ValueStat> Values = new List<ValueStat>();
        public double InitialScalar;
        public double FinalScalar;
        public float CreditValueChips;
        public Dictionary<AbilityKind, int[]> BasePrices = new Dictionary<AbilityKind, int[]>();
        public Dictionary<AbilityKind, int[]> InitialPrices = new Dictionary<AbilityKind, int[]>();
        public List<IterationLog> Iterations = new List<IterationLog>();
        public FrequencyMeasure? Final;
        public FrequencyMeasure? Holdout;
        public double WallTurnsOnReference;
        public bool Converged;
        public List<string> Failures = new List<string>();
        public double Alpha => Fit.Alpha;
        public double MinFactor => Fit.MinFactor;
    }

    /// <summary>Runs Pass 0 -> price -> Pass 2 as a pure function of measured inputs (the frequency measurement is injected so tests can fake it).</summary>
    internal static class Calibrator {
        public static List<ValueStat> SummarizeValues(IEnumerable<EconomyValueSample> samples) {
            var list = new List<ValueStat>();
            var groups = samples.GroupBy(s => (s.Kind, s.Level)).ToDictionary(g => g.Key, g => g.ToList());
            for (int k = 1; k < 10; k++) {
                var kind = (AbilityKind)k;
                int levels = GameRulesConfig.GetMaxLevel(kind);
                for (int level = 1; level <= levels; level++) {
                    groups.TryGetValue((kind.ToString(), level), out var g);
                    g ??= new List<EconomyValueSample>();
                    var positive = g.Where(x => x.GainChips.HasValue && x.GainChips.Value > 0f).Select(x => (double)x.GainChips!.Value).ToList();
                    list.Add(new ValueStat(kind, level, g.Count, positive.Count,
                        g.Count(x => x.Terminal), g.Count(x => !x.HasCandidate), g.Count(x => x.HasCandidate && !x.HasVerification),
                        g.Count(x => x.ExceedsCreditCap), PriceSolver.P75(positive)));
                }
            }
            return list;
        }

        public static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.AwayFromZero);

        public static CalibrationResult Solve(IReadOnlyList<IncomeGroup> income, IReadOnlyList<ValueStat> values, EconomyRules economy, EconomyAnchors anchors,
                                              int iterations, Func<CandidateEconomy, FrequencyMeasure>? measure, double? wallBudget = null) {
            var result = new CalibrationResult { Values = values.ToList() };
            result.Fit = PriceSolver.FitAlpha(income);
            if (!result.Fit.Ok) result.Failures.Add("Pass 0 income fit failed: " + result.Fit.Reason);
            else {
                result.Fit.Alpha = Round(result.Fit.Alpha, 2);
                if (result.Fit.ExcludedPlayables.Count > 0)
                    result.Failures.Add("note: playable counts with zero income excluded from the log fit: " + string.Join(",", result.Fit.ExcludedPlayables.OrderBy(x => x)));
            }

            var missing = values.Where(v => v.P75 == null).Select(v => $"{v.Kind} {v.Level}").ToList();
            if (missing.Count > 0) result.Failures.Add("Pass 1 has no positive finite gain samples for: " + string.Join(", ", missing) + "; rerun value sampling");
            if (!result.Fit.Ok || missing.Count > 0) return Hard(result);

            var valueMap = values.ToDictionary(v => (v.Kind, v.Level), v => v.P75!.Value);
            double wallValue = valueMap[(AbilityKind.Wall, 1)];
            int step = economy.boardScaling.roundStep;
            result.InitialScalar = wallBudget.HasValue
                ? Math.Max(step, wallBudget.Value) / wallValue
                : PriceSolver.InitialScalar(result.Fit.ReferenceIncomePerTurn, anchors.WallAffordableByOwnTurn, wallValue);
            result.CreditValueChips = (float)Round(PriceSolver.CreditValueChips(result.InitialScalar), 5);
            if (!(result.CreditValueChips > 0f)) result.Failures.Add("creditValueChips rounds to zero; the measured values are too small for the pinned AI gate");
            result.InitialPrices = PriceSolver.BuildPrices(valueMap, result.InitialScalar, step, anchors.MinLevelStep, anchors.UnblockL1EqualsWall);
            result.BasePrices = result.InitialPrices;
            result.FinalScalar = result.InitialScalar;
            if (result.Failures.Any(f => !f.StartsWith("note:", StringComparison.Ordinal))) return Hard(result);

            if (measure == null) {
                result.Failures.Add("Pass 2 frequency verification was not run (--verify-games 0): analytical report only");
                return Hard(result);
            }

            double scalar = result.InitialScalar;
            double? tooCheap = null, tooDear = null;
            for (int it = 1; it <= Math.Max(1, iterations); it++) {
                var prices = PriceSolver.BuildPrices(valueMap, scalar, step, anchors.MinLevelStep, anchors.UnblockL1EqualsWall);
                var candidate = new CandidateEconomy(prices, result.Fit.Alpha, result.Fit.MinFactor, result.CreditValueChips);
                var m = measure(candidate);
                var verdict = PriceSolver.JudgeFrequency(m.CastsPerSide, m.ExpensivePerSide, anchors);
                double next = scalar * verdict.Ratio;
                if (!verdict.Converged && !verdict.Failed) {
                    if (verdict.Ratio > 1) tooCheap = scalar;
                    else tooDear = scalar;
                    // The same seed schedule is reused while tuning. Once it brackets a crossing, stop jumping over it.
                    if (tooCheap is double low && tooDear is double high && low < high) next = Math.Sqrt(low * high);
                }
                result.Iterations.Add(new IterationLog(it, scalar, PriceSolver.MaxPrice(prices), m.CastsPerSide, m.ExpensivePerSide,
                    verdict.Converged ? "converged" : verdict.Failed ? "failed: " + verdict.Reason : $"rescale x{next / scalar:0.000} ({verdict.Reason})"));
                result.BasePrices = prices;
                result.FinalScalar = scalar;
                result.Final = m;
                if (verdict.Failed) { result.Failures.Add(verdict.Reason); return Hard(result); }
                if (verdict.Converged) { result.Converged = true; break; }
                scalar = next;
            }
            if (!result.Converged) result.Failures.Add($"Pass 2 did not converge within {Math.Max(1, iterations)} iteration(s)");

            int cap = economy.maxCredits;
            foreach (var kv in result.BasePrices)
                for (int i = 0; i < kv.Value.Length; i++)
                    if (kv.Value[i] > cap) { result.Failures.Add($"{kv.Key} level {i + 1} price {kv.Value[i]} exceeds the credit cap {cap}"); result.Converged = false; }

            result.WallTurnsOnReference = result.BasePrices[AbilityKind.Wall][0] / result.Fit.ReferenceIncomePerTurn;
            return Hard(result);
        }

        public static void CheckHoldout(CalibrationResult result, FrequencyMeasure holdout, EconomyAnchors anchors) {
            result.Holdout = holdout;
            if (PriceSolver.JudgeFrequency(holdout.CastsPerSide, holdout.ExpensivePerSide, anchors).Converged) return;
            result.Converged = false;
            result.Failures.Add($"Holdout missed the frequency bands: casts/side/game {holdout.CastsPerSide:0.00}, expensive/side/game {holdout.ExpensivePerSide:0.00}; prices were not retuned on the holdout.");
        }

        // Only "note:" entries are not failures.
        private static CalibrationResult Hard(CalibrationResult r) {
            if (r.Failures.Any(f => !f.StartsWith("note:", StringComparison.Ordinal))) r.Converged = false;
            return r;
        }

        public static bool HasFailures(CalibrationResult r) => r.Failures.Any(f => !f.StartsWith("note:", StringComparison.Ordinal));

        /// <summary>Applies the candidate to a copy of <paramref name="rules"/> (reference prices and board scaling); the copy is validated by the caller.</summary>
        public static GameRulesConfig ApplyTo(GameRulesConfig rules, IReadOnlyDictionary<AbilityKind, int[]> prices, double alpha, double minFactor) {
            var copy = rules.Clone();
            copy.economy.abilityCosts = new AbilityCosts {
                superJump = prices[AbilityKind.SuperJump], convert = prices[AbilityKind.Convert], block = prices[AbilityKind.Block], wall = prices[AbilityKind.Wall],
                doubleMove = prices[AbilityKind.DoubleMove], spawn = prices[AbilityKind.Spawn], superClone = prices[AbilityKind.SuperClone],
                unblock = prices[AbilityKind.Unblock], bomb = prices[AbilityKind.Bomb]
            };
            copy.economy.boardScaling.alpha = alpha;
            copy.economy.boardScaling.minFactor = minFactor;
            return copy;
        }
    }
}
