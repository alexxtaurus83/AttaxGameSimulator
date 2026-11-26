using System;
using System.Collections.Generic;
using System.Linq;
using Attax.Console.Economy;
using Attax.Core.Utils;
using Xunit;

namespace Attax.Core.Tests {
    public class EconomyPriceSolverTests {
        private static List<IncomeGroup> Synthetic(double alpha, double reference) =>
            new[] { 48, 42, 36, 30, 24 }.Select(p => new IncomeGroup(p, 1000, 1000 * reference * Math.Pow(p / 48.0, alpha))).ToList();

        [Theory]
        [InlineData(0.6)]
        [InlineData(1.0)]
        [InlineData(1.4)]
        public void FitAlpha_RecoversKnownAlpha(double alpha) {
            var fit = PriceSolver.FitAlpha(Synthetic(alpha, 6.0));
            Assert.True(fit.Ok);
            Assert.InRange(fit.Alpha, alpha - 0.01, alpha + 0.01);
            Assert.Equal(6.0, fit.ReferenceIncomePerTurn, 6);
        }

        [Fact]
        public void FitAlpha_RejectsMissingReferenceOrSinglePoint() {
            Assert.False(PriceSolver.FitAlpha(new[] { new IncomeGroup(42, 100, 500), new IncomeGroup(36, 100, 400) }).Ok);
            Assert.False(PriceSolver.FitAlpha(new[] { new IncomeGroup(48, 100, 500) }).Ok);
        }

        [Fact]
        public void FitAlpha_ReportsZeroIncomeGroupsAsExcluded() {
            var groups = Synthetic(1.0, 5.0);
            groups.Add(new IncomeGroup(12, 400, 0));
            var fit = PriceSolver.FitAlpha(groups);
            Assert.True(fit.Ok);
            Assert.Contains(12, fit.ExcludedPlayables);
        }

        [Fact]
        public void MinFactor_IsClampedAndRounded() {
            var groups = new[] { new IncomeGroup(48, 100, 1000), new IncomeGroup(12, 100, 50) };
            Assert.Equal(0.4, PriceSolver.DeriveMinFactor(groups, 10.0));
            var mild = new[] { new IncomeGroup(48, 100, 1000), new IncomeGroup(30, 100, 623) };
            Assert.Equal(0.62, PriceSolver.DeriveMinFactor(mild, 10.0));
        }

        [Fact]
        public void P75_IsNearestRankOfPositiveFiniteValues() {
            Assert.Null(PriceSolver.P75(new double[] { 0, -1, double.NaN }));
            Assert.Equal(3.0, PriceSolver.P75(new double[] { 1, 2, 3, 4 }));
            Assert.Equal(6.0, PriceSolver.P75(new double[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
            Assert.Equal(3.0, PriceSolver.P75(new double[] { 1, 2, 3, double.PositiveInfinity, 0 }));
        }

        [Fact]
        public void Repair_RoundsAndEnforcesMonotonicSteps() {
            var ladder = PriceSolver.Repair(new[] { 61.0, 62.0, 58.0 }, 5, 5);
            Assert.Equal(new[] { 60, 65, 70 }, ladder);
            Assert.All(PriceSolver.Repair(new[] { 0.2, 0.1 }, 5, 5), v => Assert.True(v >= 5));
        }

        [Fact]
        public void BuildPrices_LinksUnblockOneToWall() {
            var values = new Dictionary<(AbilityKind, int), double>();
            for (int k = 1; k < 10; k++)
                for (int l = 1; l <= GameRulesConfig.GetMaxLevel((AbilityKind)k); l++) values[((AbilityKind)k, l)] = 3.0 * k + l;
            var prices = PriceSolver.BuildPrices(values, 10.0, 5, 5, true);
            Assert.Equal(prices[AbilityKind.Wall][0], prices[AbilityKind.Unblock][0]);
            foreach (var ladder in prices.Values)
                for (int i = 1; i < ladder.Length; i++) Assert.True(ladder[i] - ladder[i - 1] >= 5);
        }

        [Fact]
        public void CreditsPerChip_ScalesPricesInTheRightDirection() {
            var values = new Dictionary<(AbilityKind, int), double>();
            for (int k = 1; k < 10; k++)
                for (int l = 1; l <= GameRulesConfig.GetMaxLevel((AbilityKind)k); l++) values[((AbilityKind)k, l)] = 4.0 + k + l;
            var cheap = PriceSolver.BuildPrices(values, 5.0, 5, 5, false);
            var dear = PriceSolver.BuildPrices(values, 10.0, 5, 5, false);
            Assert.True(PriceSolver.MaxPrice(dear) > PriceSolver.MaxPrice(cheap));
            Assert.True(PriceSolver.CreditValueChips(10.0) < PriceSolver.CreditValueChips(5.0));
        }

        [Fact]
        public void InitialScalar_MakesWallOneCostThreeTurnsOfIncome() {
            double scalar = PriceSolver.InitialScalar(6.0, 3, 2.0);
            Assert.Equal(6.0 * 3, 2.0 * scalar, 9);
        }

        [Fact]
        public void JudgeFrequency_Rules() {
            var a = new EconomyAnchors();
            Assert.True(PriceSolver.JudgeFrequency(3.5, 1.0, a).Converged);
            // single miss uses that metric: too many casts -> raise prices (ratio > 1)
            var high = PriceSolver.JudgeFrequency(5.2, 1.0, a);
            Assert.False(high.Converged);
            Assert.True(high.Ratio > 1.0);
            var low = PriceSolver.JudgeFrequency(2.0, 1.0, a);
            Assert.True(low.Ratio < 1.0);
            // same direction: larger relative miss, clamped
            var both = PriceSolver.JudgeFrequency(8.0, 3.0, a);
            Assert.Equal(PriceSolver.ScaleClampHigh, both.Ratio);
            // opposite directions fail
            Assert.True(PriceSolver.JudgeFrequency(2.0, 2.0, a).Failed);
        }

        [Fact]
        public void IsExpensive_CountsLevelThreeSuperCloneAndDoubleMove() {
            Assert.True(PriceSolver.IsExpensive(AbilityKind.Bomb, 3));
            Assert.True(PriceSolver.IsExpensive(AbilityKind.SuperClone, 1));
            Assert.True(PriceSolver.IsExpensive(AbilityKind.DoubleMove, 1));
            Assert.False(PriceSolver.IsExpensive(AbilityKind.SuperJump, 1));
            Assert.False(PriceSolver.IsExpensive(AbilityKind.Convert, 2));
        }

        private static (EconomyRules economy, List<ValueStat> values) Fixture() {
            var rules = new GameRulesConfig {
                schemaVersion = 2, winRule = "chips",
                economy = new EconomyRules {
                    enabled = true, maxCredits = 300, captureReward = 10, footprintReward = 5,
                    abilityCosts = new AbilityCosts(), boardScaling = new BoardScaling { referencePlayable = 48, alpha = 1, minFactor = 0.5, roundStep = 5 }
                }
            };
            var values = new List<ValueStat>();
            for (int k = 1; k < 10; k++)
                for (int l = 1; l <= GameRulesConfig.GetMaxLevel((AbilityKind)k); l++)
                    values.Add(new ValueStat((AbilityKind)k, l, 10, 10, 0, 0, 0, 0, 2.0 + k * 0.5 + l));
            return (rules.economy, values);
        }

        [Fact]
        public void Solve_ConvergesWithFakeMeasurement_AndPinsCreditValue() {
            var (economy, values) = Fixture();
            var seen = new List<float>();
            int call = 0;
            var result = Calibrator.Solve(Synthetic(1.0, 6.0), values, economy, new EconomyAnchors(), 4, c => {
                seen.Add(c.CreditValueChips);
                call++;
                return call == 1 ? new FrequencyMeasure(6.0, 1.0, 10) : new FrequencyMeasure(3.5, 1.0, 10);
            });
            Assert.True(result.Converged, string.Join("; ", result.Failures));
            Assert.Equal(2, result.Iterations.Count);
            Assert.Single(seen.Distinct());
            Assert.True(result.Iterations[1].Scalar > result.Iterations[0].Scalar);
        }

        [Fact]
        public void Solve_UsesMeasuredWallBudget_AndBracketsOscillatingPrices() {
            var (economy, values) = Fixture();
            int call = 0;
            var result = Calibrator.Solve(Synthetic(1.0, 6.0), values, economy, new EconomyAnchors(), 3, _ => ++call switch {
                1 => new FrequencyMeasure(8, 3, 100),
                2 => new FrequencyMeasure(1, 0, 100),
                _ => new FrequencyMeasure(3.5, 1, 100)
            }, wallBudget: 35);
            Assert.Equal(35, result.InitialPrices[AbilityKind.Wall][0]);
            Assert.True(result.Converged);
            Assert.InRange(result.Iterations[2].Scalar, result.Iterations[0].Scalar, result.Iterations[1].Scalar);
            var prices = result.BasePrices;
            Calibrator.CheckHoldout(result, new FrequencyMeasure(4.51, 1, 100), new EconomyAnchors());
            Assert.False(result.Converged);
            Assert.Same(prices, result.BasePrices);
            Assert.Contains(result.Failures, s => s.StartsWith("Holdout missed", StringComparison.Ordinal));
        }

        [Fact]
        public void Solve_RejectsMissingValuesNonConvergenceAndNoVerification() {
            var (economy, values) = Fixture();
            var missing = values.Select(v => v.Kind == AbilityKind.Wall && v.Level == 1 ? v with { P75 = null, Positive = 0 } : v).ToList();
            var noWall = Calibrator.Solve(Synthetic(1.0, 6.0), missing, economy, new EconomyAnchors(), 2, c => new FrequencyMeasure(3.5, 1, 1));
            Assert.False(noWall.Converged);
            Assert.Contains(noWall.Failures, f => f.Contains("Wall 1"));

            var stuck = Calibrator.Solve(Synthetic(1.0, 6.0), values, economy, new EconomyAnchors(), 2, c => new FrequencyMeasure(9.0, 1.0, 1));
            Assert.False(stuck.Converged);

            var analytical = Calibrator.Solve(Synthetic(1.0, 6.0), values, economy, new EconomyAnchors(), 2, null);
            Assert.False(analytical.Converged);
            Assert.NotEmpty(analytical.BasePrices);
        }

        [Fact]
        public void Solve_RejectsOverCapPrices() {
            var (economy, values) = Fixture();
            var huge = values.Select(v => v.Kind == AbilityKind.Wall && v.Level == 1 ? v with { P75 = 1.0 } : v with { P75 = 500.0 }).ToList();
            var result = Calibrator.Solve(Synthetic(1.0, 6.0), huge, economy, new EconomyAnchors(), 1, c => new FrequencyMeasure(3.5, 1, 1));
            Assert.False(result.Converged);
            Assert.Contains(result.Failures, f => f.Contains("exceeds the credit cap"));
        }

        [Fact]
        public void Anchors_DefaultsMatchThePlan() {
            var a = new EconomyAnchors();
            Assert.Equal(3, a.WallAffordableByOwnTurn);
            Assert.Equal(new double[] { 3, 4 }, a.CastsPerSidePerGame);
            Assert.Equal(1.0, a.ExpensiveCastsPerSidePerGame);
            Assert.Equal(5, a.MinLevelStep);
            Assert.True(a.UnblockL1EqualsWall);
            Assert.Equal(0.5, a.Tolerance);
        }

        [Fact]
        public void ReplaceBlock_OnlyTouchesTheMarkedRegion() {
            string doc = "head\n" + EconomyReport.BeginMarker + "\nold\n" + EconomyReport.EndMarker + "\ntail\n";
            string updated = EconomyReport.ReplaceBlock(doc, "NEW TABLE\n");
            Assert.StartsWith("head\n", updated);
            Assert.EndsWith(EconomyReport.EndMarker + "\ntail\n", updated);
            Assert.Equal("NEW TABLE", EconomyReport.ExtractBlock(updated));
            Assert.Throws<System.IO.InvalidDataException>(() => EconomyReport.ReplaceBlock("no markers", "x"));
        }
    }
}
