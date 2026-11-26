using System;
using System.Linq;
using Attax.Agents;
using Attax.Console.Economy;
using Attax.Core.Utils;
using Attax.Play;
using Newtonsoft.Json;
using Xunit;

namespace Attax.Core.Tests {
    public class RulesGameRunnerTests {
        private static EconomyContext Context() => EconomyContext.Load(null, null);

        private static RulesGameResult Play(string boardId, int seed, bool abilities, int samples = 0, string spec = "classic:depth=2,quiescence=false") {
            var ctx = Context();
            var board = StartBoard.CreateFromShape(boardId, seed);
            using var red = PlayerFactory.Create(PlayerSpec.Parse(spec), null!, seed + 1, defaultTrain: false);
            using var blue = PlayerFactory.Create(PlayerSpec.Parse(spec), null!, seed + 2, defaultTrain: false);
            return RulesGameRunner.Run(board, red, blue, new RulesGameOptions { Rules = ctx.Rules, Ai = ctx.Ai, Abilities = abilities, SampleValues = samples, Seed = seed });
        }

        [Fact]
        public void IncomeGame_CreditsEqualCaptureAndFootprintOutcomes_AndNoCasts() {
            var r = Play("default", 11, abilities: false);
            Assert.True(r.Plies > 0);
            Assert.Empty(r.Casts);
            Assert.Equal(0, r.Red.CreditsSpent + r.Blue.CreditsSpent);
            Assert.True(r.Red.OwnTurns > 0 && r.Blue.OwnTurns > 0);
            Assert.True(r.Red.CaptureCredits + r.Blue.CaptureCredits + r.Red.FootprintCredits + r.Blue.FootprintCredits > 0);
            // Every flip pays the victim captureReward unless the cap clipped it.
            Assert.True(r.Red.CaptureCredits <= r.Blue.Flips * 10 && r.Blue.CaptureCredits <= r.Red.Flips * 10);
        }

        [Fact]
        public void IncomeGame_LogsBalanceAtTurnStartAndRewardsLostToCap() {
            var ctx = Context();
            ctx.Rules.economy.maxCredits = 1;
            ctx.Rules.economy.startingCredits = 1;
            using var red = PlayerFactory.Create(PlayerSpec.Parse("classic:depth=1"), null!, 11, defaultTrain: false);
            using var blue = PlayerFactory.Create(PlayerSpec.Parse("classic:depth=1"), null!, 12, defaultTrain: false);
            var r = RulesGameRunner.Run(StartBoard.Standard(), red, blue, new RulesGameOptions { Rules = ctx.Rules, Abilities = false });
            Assert.True(r.Plies > 0);
            Assert.Empty(r.Casts);
            Assert.Equal(0, r.Red.CreditsSpent + r.Blue.CreditsSpent);
            Assert.Equal(0, r.Red.CaptureCredits + r.Blue.CaptureCredits + r.Red.FootprintCredits + r.Blue.FootprintCredits);
            Assert.True(r.Red.Flips + r.Blue.Flips > 0);
            Assert.True(r.Red.CreditsLostToCap + r.Blue.CreditsLostToCap >= (r.Red.Flips + r.Blue.Flips) * ctx.Rules.economy.captureReward);
            Assert.Equal(r.RedTurnStarts, r.Red.TurnStartCredits.Count);
            Assert.Equal(r.BlueTurnStarts, r.Blue.TurnStartCredits.Count);
            Assert.All(r.Red.TurnStartCredits.Concat(r.Blue.TurnStartCredits), credits => Assert.Equal(1, credits));
        }

        [Fact]
        public void AbilityGame_RecordsCastsWithCostsAndStaysLegal() {
            RulesGameResult? best = null;
            for (int seed = 1; seed <= 4 && best == null; seed++) {
                var r = Play("default", seed, abilities: true);
                if (r.Casts.Count > 0) best = r;
            }
            Assert.NotNull(best);
            Assert.All(best!.Casts, c => Assert.True(c.Cost > 0));
            Assert.Equal(best.Casts.Sum(c => c.Cost), best.Red.CreditsSpent + best.Blue.CreditsSpent);
            Assert.Equal(best.Casts.Count, best.Red.Casts + best.Blue.Casts);
        }

        [Fact]
        public void SameSeed_GivesIdenticalGames() {
            var a = Play("story-04", 5, abilities: true);
            var b = Play("story-04", 5, abilities: true);
            Assert.Equal(a.Plies, b.Plies);
            Assert.Equal(a.RedChips, b.RedChips);
            Assert.Equal(a.Casts.Select(c => (c.Ply, c.Kind, c.Level)), b.Casts.Select(c => (c.Ply, c.Kind, c.Level)));
            Assert.Equal(a.Red.CaptureCredits, b.Red.CaptureCredits);
        }

        [Fact]
        public void ValueSampling_ProducesMeasurementsForAllowedLevels() {
            var r = Play("default", 3, abilities: false, samples: 2);
            Assert.NotEmpty(r.Values);
            Assert.InRange(r.Values.Count, 1, 2);
            Assert.Equal(r.Values.Count, r.Values.Select(v => (v.Side, v.OwnTurn)).Distinct().Count());
            Assert.All(r.Values, v => Assert.True(RulesGameRunner.IsEligibleSampleTurn(v.OwnTurn,
                v.Side == PlayerColor.Red ? r.RedTurnStarts : r.BlueTurnStarts)));
            var repeat = Play("default", 3, abilities: false, samples: 2);
            Assert.Equal(r.Values.Select(v => (v.Ply, v.Side, v.OwnTurn)), repeat.Values.Select(v => (v.Ply, v.Side, v.OwnTurn)));
            var noSamples = Play("default", 3, abilities: false);
            Assert.Empty(noSamples.Values);
            Assert.Equal(noSamples.Plies, r.Plies);
            Assert.Equal(noSamples.Winner, r.Winner);
            var first = r.Values[0];
            Assert.Equal(17, first.Values.Count);
            Assert.All(r.Values.SelectMany(v => v.Values), s => Assert.True(!s.GainChips.HasValue || (s.GainChips > 0 && !float.IsInfinity(s.GainChips.Value))));
        }

        [Theory]
        [InlineData(4, 7, false)]
        [InlineData(4, 8, true)]
        [InlineData(22, 26, true)]
        [InlineData(22, 25, false)]
        [InlineData(3, 40, false)]
        public void SamplingWindow_UsesInclusiveSideLocalBounds(int turn, int end, bool expected) =>
            Assert.Equal(expected, RulesGameRunner.IsEligibleSampleTurn(turn, end));

        [Fact]
        public void SearchPlayer_HonoursRestrictionMasks() {
            using var player = PlayerFactory.Create(PlayerSpec.Parse("classic:depth=2"), null!, 7, defaultTrain: false);
            var search = Assert.IsAssignableFrom<ISearchEnginePlayer>(player);
            var board = StartBoard.Standard();
            ulong frozen = 1UL << 0;                     // Red's corner chip may not move
            var d = search.ChooseRestricted(board, PlayerColor.Red, frozen, 0UL);
            Assert.True(d.HasMove);
            Assert.NotEqual((0, 0), (d.Move.FromX, d.Move.FromY));
            ulong forbidden = 1UL << (1 * 7 + 1);
            var d2 = search.ChooseRestricted(board, PlayerColor.Red, 0UL, forbidden);
            Assert.True(d2.HasMove);
            Assert.NotEqual((1, 1), (d2.Move.ToX, d2.Move.ToY));
        }

        [Fact]
        public void EconomyLog_RoundTripsAndWritesAtomically() {
            var ctx = Context();
            var settings = new EconomySimSettings {
                Boards = new[] { "default", "story-02" }, Games = 2, Red = "classic:depth=1,quiescence=false", Blue = "classic:depth=1,quiescence=false",
                Abilities = true, SampleValues = 1, Parallel = 1, Tag = "t", Context = ctx, Seed = 9
            };
            var records = EconomySim.Run(settings);
            Assert.Equal(2, records.Count);
            Assert.Equal(new[] { "default", "story-02" }, records.Select(r => r.BoardId));

            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "attax-econ-" + Guid.NewGuid().ToString("N"));
            try {
                string path = System.IO.Path.Combine(dir, "s.jsonl");
                EconomyLog.Write(path, records);
                Assert.False(System.IO.File.Exists(path + ".tmp"));
                var back = EconomyLog.Read(path);
                Assert.Equal(records.Count, back.Count);
                Assert.Equal(EconomySim.DeterministicHash(records), EconomySim.DeterministicHash(back));
                Assert.All(back, r => Assert.Equal(EconomyLog.CurrentSchemaVersion, r.SchemaVersion));
                Assert.Equal(ctx.RulesHash, back[0].RulesHash);

                // directory input reads top-level *.jsonl only
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "x.jsonl.tmp"), "garbage");
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "analysis"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "analysis", "y.jsonl"), "garbage");
                Assert.Single(EconomyLog.ResolveInputs(new[] { dir }));

                System.IO.File.WriteAllText(path, "{\"schemaVersion\":99}\n");
                Assert.Throws<System.IO.InvalidDataException>(() => EconomyLog.Read(path));
            } finally {
                if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void IdenticalSeed_GivesIdenticalLogHash() {
            var ctx = Context();
            EconomySimSettings Make() => new EconomySimSettings {
                Boards = new[] { "default", "story-04" }, Games = 2, Red = "classic:depth=1,quiescence=false", Blue = "classic:depth=1,quiescence=false",
                Abilities = true, SampleValues = 1, Parallel = 2, Tag = "t", Context = ctx, Seed = 77
            };
            Assert.Equal(EconomySim.DeterministicHash(EconomySim.Run(Make())), EconomySim.DeterministicHash(EconomySim.Run(Make())));
        }

        [Fact]
        public void SwapColors_PlaysEachOpeningTwiceWithSwappedPlayers() {
            var ctx = Context();
            var records = EconomySim.Run(new EconomySimSettings {
                Boards = new[] { "default" }, Games = 2, Red = "classic:depth=1,quiescence=false", Blue = "classic:depth=2,quiescence=false",
                SwapColors = true, Abilities = false, OpeningPlies = 2, Parallel = 1, Context = ctx, Seed = 4
            });
            Assert.Equal(4, records.Count);
            Assert.Equal(records[0].Seed, records[1].Seed);
            Assert.False(records[0].Swapped);
            Assert.True(records[1].Swapped);
            Assert.Equal(records[0].Red, records[1].Blue);
            Assert.All(records, r => Assert.Equal(2, r.OpeningPliesPlayed));
            Assert.Equal(records[0].OpeningPosition, records[1].OpeningPosition);
            Assert.Equal(records[2].OpeningPosition, records[3].OpeningPosition);
            Assert.NotEqual(records[0].OpeningPosition, records[2].OpeningPosition);
        }

        [Fact]
        public void BoardsAreStratified_EveryShapeGetsTheSameCount() {
            var boards = EconomySim.ResolveBoards("all");
            Assert.Equal(20, boards.Count);
            var counts = Enumerable.Range(0, 40).GroupBy(i => EconomySim.BoardFor(boards, i)).Select(g => g.Count()).Distinct().ToList();
            Assert.Single(counts);
        }

        [Fact]
        public void Calibration_ApplyRoundTripsRulesFingerprint() {
            var ctx = Context();
            var parsed = JsonConvert.DeserializeObject<GameRulesConfig>(Attax.Console.Program.RenderRules(ctx.Rules, "\n"));
            Assert.NotNull(parsed);
            Assert.Equal(ctx.Rules.Fingerprint(), parsed!.Fingerprint());
        }
    }
}
