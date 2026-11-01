using System;
using System.Collections.Generic;
using System.Linq;

namespace Attax.Play {
    public enum SprtDecision { Continue, AcceptH1, AcceptH0 }

    /// <summary>
    /// Statistics over PAIRS. One pair = one random opening played twice with colours swapped, scored for player 1 as
    /// (points in both games) / 2, so each value is 0, 0.25, 0.5, 0.75 or 1. Pairs, not games, are the independent units:
    /// the two games of a pair share an opening, and a deterministic engine often plays the same game twice.
    /// </summary>
    public static class PairStats {
        public static double EloToScore(double elo) => 1.0 / (1.0 + Math.Pow(10.0, -elo / 400.0));

        public static double ScoreToElo(double score) {
            if (score <= 0) return double.NegativeInfinity;
            if (score >= 1) return double.PositiveInfinity;
            return -400.0 * Math.Log10(1.0 / score - 1.0);
        }

        public struct Summary {
            public int N;
            public double Mean, StdDev, Lo, Hi;
            public double Elo, EloLo, EloHi;
        }

        /// <summary>Mean score with a 95% normal interval over pairs, plus the Elo equivalents.</summary>
        public static Summary Summarize(IReadOnlyList<double> pairScores) {
            int n = pairScores.Count;
            var s = new Summary { N = n };
            if (n == 0) return s;
            s.Mean = pairScores.Average();
            s.StdDev = n > 1 ? Math.Sqrt(pairScores.Sum(x => (x - s.Mean) * (x - s.Mean)) / (n - 1)) : 0.0;
            double se = s.StdDev / Math.Sqrt(n);
            s.Lo = Math.Max(0.0, s.Mean - 1.96 * se);
            s.Hi = Math.Min(1.0, s.Mean + 1.96 * se);
            s.Elo = ScoreToElo(s.Mean); s.EloLo = ScoreToElo(s.Lo); s.EloHi = ScoreToElo(s.Hi);
            return s;
        }

        /// <summary>Variance floor for the SPRT (a run of identical pairs would otherwise give a zero variance and an infinite LLR).</summary>
        public const double VarianceFloor = 0.01;

        /// <summary>
        /// Sequential probability ratio test on the mean pair score, normal approximation with the sample variance (the usual
        /// GSPRT used for engine testing). H0: true Elo = elo0, H1: true Elo = elo1 (elo1 > elo0).
        /// LLR = n (s1 - s0) (2 mean - s0 - s1) / (2 var). Accept H1 when LLR >= ln((1-beta)/alpha), accept H0 when
        /// LLR <= ln(beta/(1-alpha)). Do not look before ~20 pairs: the variance estimate is too rough.
        /// </summary>
        public static (double llr, double lower, double upper, SprtDecision decision) Sprt(
            IReadOnlyList<double> pairScores, double elo0, double elo1, double alpha, double beta) {
            if (!(elo1 > elo0)) throw new ArgumentException("elo1 must be greater than elo0.");
            if (!(alpha > 0 && alpha < 0.5) || !(beta > 0 && beta < 0.5)) throw new ArgumentException("alpha and beta must be in (0, 0.5).");
            double lower = Math.Log(beta / (1.0 - alpha)), upper = Math.Log((1.0 - beta) / alpha);
            int n = pairScores.Count;
            if (n < 2) return (0.0, lower, upper, SprtDecision.Continue);
            double mean = pairScores.Average();
            double var = pairScores.Sum(x => (x - mean) * (x - mean)) / (n - 1);
            var = Math.Max(var, VarianceFloor);
            double s0 = EloToScore(elo0), s1 = EloToScore(elo1);
            double llr = n * (s1 - s0) * (2.0 * mean - s0 - s1) / (2.0 * var);
            var d = llr >= upper ? SprtDecision.AcceptH1 : llr <= lower ? SprtDecision.AcceptH0 : SprtDecision.Continue;
            return (llr, lower, upper, d);
        }

        /// <summary>Parses "elo0,elo1" (for example "0,5").</summary>
        public static (double elo0, double elo1) ParseSprtBounds(string text) {
            var parts = (text ?? "").Split(',');
            if (parts.Length != 2 || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double a)
                || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double b))
                throw new FormatException($"--sprt expects 'elo0,elo1' such as '0,5', got '{text}'.");
            if (!(b > a)) throw new FormatException("--sprt: elo1 must be greater than elo0.");
            return (a, b);
        }
    }
}
