using Attax.Core.Utils;

namespace Attax.Console.Economy {
    internal sealed record IncomeGroup(int Playable, double OwnTurns, double Income) {
        public double MeanPerTurn => OwnTurns > 0 ? Income / OwnTurns : 0.0;
    }

    internal sealed class AlphaFit {
        public bool Ok;
        public string Reason = "";
        public double Alpha;
        public double MinFactor;
        public double ReferenceIncomePerTurn;
        public List<int> ExcludedPlayables = new List<int>();
    }

    internal sealed class FrequencyVerdict {
        public bool Converged;
        public bool Failed;
        public string Reason = "";
        /// <summary>measured/targetMid used to rescale the credit scalar (1 when converged or failed).</summary>
        public double Ratio = 1.0;
    }

    /// <summary>Pure calibration math: board-scaling fit, nearest-rank P75, value-to-price conversion and the frequency rescale rule.</summary>
    internal static class PriceSolver {
        public const int ReferencePlayable = 48;
        public const double MinFactorFloor = 0.4;
        public const double ScaleClampLow = 0.7;
        public const double ScaleClampHigh = 1.4;

        /// <summary>
        /// Fits <c>meanIncome(p) = ref * (p / 48)^alpha</c> by least squares on logs. Groups with zero mean income stay in the arithmetic means
        /// (the caller sums them) but cannot enter the log fit; they are reported in <see cref="AlphaFit.ExcludedPlayables"/>.
        /// </summary>
        public static AlphaFit FitAlpha(IReadOnlyList<IncomeGroup> groups) {
            var fit = new AlphaFit();
            var usable = new List<(double x, double y, IncomeGroup g)>();
            foreach (var g in groups) {
                double mean = g.MeanPerTurn;
                if (mean > 0 && g.Playable > 0) usable.Add((Math.Log((double)g.Playable / ReferencePlayable), Math.Log(mean), g));
                else fit.ExcludedPlayables.Add(g.Playable);
            }
            var reference = usable.FirstOrDefault(u => u.g.Playable == ReferencePlayable);
            if (reference.g == null) { fit.Reason = $"no positive income at the reference playable count {ReferencePlayable}"; return fit; }
            fit.ReferenceIncomePerTurn = reference.g.MeanPerTurn;
            if (usable.Select(u => u.g.Playable).Distinct().Count() < 2) { fit.Reason = "fewer than two distinct playable counts with positive income"; return fit; }

            double n = usable.Count, sx = usable.Sum(u => u.x), sy = usable.Sum(u => u.y);
            double sxx = usable.Sum(u => u.x * u.x), sxy = usable.Sum(u => u.x * u.y);
            double denom = n * sxx - sx * sx;
            if (Math.Abs(denom) < 1e-12) { fit.Reason = "degenerate fit (all playable counts equal)"; return fit; }
            fit.Alpha = Math.Max(0.0, (n * sxy - sx * sy) / denom);
            fit.MinFactor = DeriveMinFactor(groups, fit.ReferenceIncomePerTurn);
            fit.Ok = true;
            return fit;
        }

        /// <summary>Smallest observed income ratio to the reference board, rounded to two decimals and clamped to 0.4..1.0.</summary>
        public static double DeriveMinFactor(IReadOnlyList<IncomeGroup> groups, double referenceIncome) {
            if (referenceIncome <= 0) return 1.0;
            double min = 1.0;
            foreach (var g in groups) {
                double mean = g.MeanPerTurn;
                if (mean > 0) min = Math.Min(min, mean / referenceIncome);
            }
            return Math.Round(Math.Min(1.0, Math.Max(MinFactorFloor, min)), 2, MidpointRounding.AwayFromZero);
        }

        /// <summary>Nearest-rank 75th percentile of positive finite values; null when there are none.</summary>
        public static double? P75(IEnumerable<double> values) {
            var sorted = values.Where(v => v > 0 && !double.IsNaN(v) && !double.IsInfinity(v)).OrderBy(v => v).ToList();
            if (sorted.Count == 0) return null;
            int rank = (int)Math.Ceiling(0.75 * sorted.Count);
            return sorted[Math.Min(sorted.Count, Math.Max(1, rank)) - 1];
        }

        /// <summary>Credits per chip of value that makes Wall I cost the mean income of <paramref name="turns"/> own turns on the reference board.</summary>
        public static double InitialScalar(double referenceIncomePerTurn, int turns, double wallValueChips) {
            if (!(wallValueChips > 0)) throw new ArgumentException("Wall I value must be positive.", nameof(wallValueChips));
            return referenceIncomePerTurn * turns / wallValueChips;
        }

        /// <summary>Chips one credit is worth for the AI gate; pinned for every Pass 2 iteration.</summary>
        public static double CreditValueChips(double initialScalar) => 1.0 / initialScalar;

        public static int RoundToStep(double credits, int step) =>
            (int)(step * Math.Round(credits / step, MidpointRounding.AwayFromZero));

        /// <summary>
        /// Reference prices (48 playable) from measured chip values. Rounds to <paramref name="step"/>, keeps every level at least
        /// <paramref name="minLevelStep"/> above the previous one and optionally forces Unblock I to equal Wall I.
        /// </summary>
        public static Dictionary<AbilityKind, int[]> BuildPrices(IReadOnlyDictionary<(AbilityKind, int), double> valueChips, double creditsPerChip,
                                                                 int step, int minLevelStep, bool unblockEqualsWall) {
            var prices = new Dictionary<AbilityKind, int[]>();
            for (int k = 1; k < 10; k++) {
                var kind = (AbilityKind)k;
                int levels = GameRulesConfig.GetMaxLevel(kind);
                var raw = new double[levels];
                for (int l = 1; l <= levels; l++) {
                    if (!valueChips.TryGetValue((kind, l), out double v)) throw new InvalidOperationException($"Missing value for {kind} level {l}.");
                    raw[l - 1] = v * creditsPerChip;
                }
                prices[kind] = Repair(raw, step, minLevelStep);
            }
            if (unblockEqualsWall) {
                int wall = prices[AbilityKind.Wall][0];
                var raw = new double[prices[AbilityKind.Unblock].Length];
                for (int i = 0; i < raw.Length; i++) raw[i] = prices[AbilityKind.Unblock][i];
                raw[0] = wall;
                prices[AbilityKind.Unblock] = Repair(raw, step, minLevelStep);
                prices[AbilityKind.Unblock][0] = wall;
                for (int i = 1; i < raw.Length; i++)
                    prices[AbilityKind.Unblock][i] = Math.Max(prices[AbilityKind.Unblock][i], prices[AbilityKind.Unblock][i - 1] + minLevelStep);
            }
            return prices;
        }

        /// <summary>Rounds, then enforces a strictly increasing ladder with at least <paramref name="minLevelStep"/> between levels.</summary>
        public static int[] Repair(double[] raw, int step, int minLevelStep) {
            var result = new int[raw.Length];
            int previous = 0;
            for (int i = 0; i < raw.Length; i++) {
                int rounded = Math.Max(step, RoundToStep(raw[i], step));
                int floor = i == 0 ? step : previous + Math.Max(minLevelStep, step);
                result[i] = Math.Max(rounded, floor);
                previous = result[i];
            }
            return result;
        }

        /// <summary>Largest price in the table (for the credit-cap check).</summary>
        public static int MaxPrice(IReadOnlyDictionary<AbilityKind, int[]> prices) => prices.Values.SelectMany(p => p).DefaultIfEmpty(0).Max();

        public static int[] PriceLadder(IReadOnlyDictionary<AbilityKind, int[]> prices, AbilityKind kind) => prices[kind];

        /// <summary>
        /// Judges measured cast frequencies. Both inside their bands: converged. One miss: rescale by that metric. Two misses in the same
        /// direction: rescale by the larger relative miss. Opposite directions: failure (no single price scalar can fix both).
        /// </summary>
        public static FrequencyVerdict JudgeFrequency(double casts, double expensive, EconomyAnchors anchors) {
            bool castsLow = casts < anchors.CastsLow, castsHigh = casts > anchors.CastsHigh;
            bool expLow = expensive < anchors.ExpensiveLow, expHigh = expensive > anchors.ExpensiveHigh;
            var verdict = new FrequencyVerdict();
            if (!castsLow && !castsHigh && !expLow && !expHigh) { verdict.Converged = true; return verdict; }

            double castsRatio = anchors.CastsMid > 0 ? casts / anchors.CastsMid : 1.0;
            double expRatio = anchors.ExpensiveCastsPerSidePerGame > 0 ? expensive / anchors.ExpensiveCastsPerSidePerGame : 1.0;
            bool castsMiss = castsLow || castsHigh, expMiss = expLow || expHigh;

            if (castsMiss && expMiss) {
                bool opposite = (castsLow && expHigh) || (castsHigh && expLow);
                if (opposite) {
                    verdict.Failed = true;
                    verdict.Reason = $"frequency targets miss in opposite directions (casts/side/game {casts:0.00} vs target [{anchors.CastsPerSidePerGame[0]}, {anchors.CastsPerSidePerGame[1]}], expensive {expensive:0.00} vs {anchors.ExpensiveCastsPerSidePerGame})";
                    return verdict;
                }
                verdict.Ratio = Math.Abs(Math.Log(Math.Max(castsRatio, 1e-6))) >= Math.Abs(Math.Log(Math.Max(expRatio, 1e-6))) ? castsRatio : expRatio;
            } else {
                verdict.Ratio = castsMiss ? castsRatio : expRatio;
            }
            verdict.Ratio = Math.Min(ScaleClampHigh, Math.Max(ScaleClampLow, verdict.Ratio));
            verdict.Reason = $"casts/side/game {casts:0.00}, expensive/side/game {expensive:0.00}";
            return verdict;
        }

        /// <summary>Whether an ability level counts as expensive for the frequency anchor: every level III, Super Clone and Double Move.</summary>
        public static bool IsExpensive(AbilityKind kind, int level) =>
            kind == AbilityKind.SuperClone || kind == AbilityKind.DoubleMove || level >= 3;
    }
}
