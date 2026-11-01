using System;
using System.Collections.Generic;
using System.Linq;
using Attax.Core;
using Attax.Play;
using Xunit;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core.Tests {
    public class RootTieBreakTests {
        private static readonly BitboardState Start = TestBoards.Parse("R.....B", ".......", ".......", ".......", ".......", ".......", "B.....R");

        private static AtaxxAIEngine Engine(bool randomTies, int seed, int depth = 2) =>
            new AtaxxAIEngine(Prm.Base(), new AIEngineConfig {
                AiDepth = depth, TrainingMode = true, DisableParallelRootSearch = true, DisableQuiescenceSearch = true, Seed = seed,
                DisableRandomRootTies = !randomTies
            }) { CollectRootScores = true };

        private static (Move best, List<RootScore> scores) Play(bool randomTies, int seed, BitboardState b, PlayerColor side, int depth = 2) {
            var e = Engine(randomTies, seed, depth);
            TestBoards.SetBoard(e, b.Clone(), side); e.AIPlayerColor = side;
            var m = e.GetBestMove(side);
            return (m, e.LastRootScores.ToList());
        }

        private static (int, int, int, int) Key(Move m) => (m.FromX, m.FromY, m.ToX, m.ToY);

        // Positions where at least two root moves tie for the best Final score (found by search, not assumed).
        private static List<(BitboardState b, PlayerColor s)> TiedPositions(int want) {
            var found = new List<(BitboardState, PlayerColor)>();
            foreach (var (b, side) in RefactorGoldenTests.RandomPositions(400, 8128)) {
                var (_, sc) = Play(false, 1, b, side);
                if (sc.Count > 1 && sc[0].Final == sc[1].Final && sc[0].Final < 900_000_000) found.Add((b, side));
                if (found.Count >= want) break;
            }
            return found;
        }

        [Fact]
        public void TheEngineDefaultIsRandomTies_AndItIsNotAnEngineParameter() {
            Assert.False(default(AIEngineConfig).DisableRandomRootTies);                                   // default(config) = random ties ON
            var tied = TiedPositions(3);
            Assert.True(tied.Count >= 2);
            // an engine built with a default config really does vary
            bool varied = false;
            foreach (var (b, s) in tied) {
                var picks = Enumerable.Range(1, 30).Select(seed => {
                    var e = new AtaxxAIEngine(Prm.Base(), new AIEngineConfig { AiDepth = 2, TrainingMode = true, DisableParallelRootSearch = true, DisableQuiescenceSearch = true, Seed = seed });
                    TestBoards.SetBoard(e, b.Clone(), s); e.AIPlayerColor = s; return Key(e.GetBestMove(s));
                }).Distinct().Count();
                varied |= picks > 1;
            }
            Assert.True(varied, "an engine with a default config should vary its play among tied moves");
            // and nothing about it lives in the parameter object or the JSON file
            Assert.Null(typeof(SearchParams).GetField("randomizeRootTies"));
            Assert.DoesNotContain("randomizeRootTies", System.IO.File.ReadAllText(Attax.Play.EngineParamsFile.BasePath()), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("RootTies", string.Join("\n", EngineParamsFile.Flatten(Prm.Base()).Select(kv => kv.Key)));
        }

        [Fact]
        public void Disabled_PlaysTheFirstRankedMove_Always_WhateverTheSeed() {
            var tied = TiedPositions(4);
            Assert.True(tied.Count >= 2, "the sample must contain positions with tied best moves");
            foreach (var (b, s) in tied) {
                var picks = Enumerable.Range(1, 12).Select(seed => Play(false, seed, b, s)).ToList();
                Assert.All(picks, r => Assert.True(r.best.Equals(r.scores[0].Move)));
                Assert.Single(picks.Select(r => Key(r.best)).Distinct());
            }
        }

        [Fact]
        public void Enabled_PicksAmongTiedMovesOnly_VariesAcrossSeeds_AndNeverPlaysAWorseMove() {
            var tied = TiedPositions(4);
            Assert.True(tied.Count >= 2);
            bool variedSomewhere = false;
            foreach (var (b, s) in tied) {
                var results = Enumerable.Range(1, 30).Select(seed => Play(true, seed, b, s)).ToList();
                int top = results[0].scores[0].Final;
                var tiedMoves = results[0].scores.Where(x => x.Final == top).Select(x => x.Move).ToList();
                Assert.All(results, r => Assert.Contains(r.best, tiedMoves));                                  // only ever one of the tied best moves
                variedSomewhere |= results.Select(r => Key(r.best)).Distinct().Count() > 1;
            }
            Assert.True(variedSomewhere, "30 seeds on tied positions should have produced more than one distinct move");
        }

        [Fact]
        public void Enabled_WithAFixedSeed_IsReproducible() {
            foreach (var (b, s) in TiedPositions(3)) {
                var a = Play(true, 77, b, s).best; var c = Play(true, 77, b, s).best;
                Assert.True(a.Equals(c));
            }
        }

        [Fact]
        public void Enabled_DoesNotDisturbTheMainRandomStream() {
            // Blocked-cell placement draws from the engine's own seeded stream. A search that breaks ties first must not shift it.
            var tied = TiedPositions(1);
            Assert.NotEmpty(tied);
            List<(int, int)> Blocks(bool randomTies) {
                var e = Engine(randomTies, 5);
                var (b, s) = tied[0];
                TestBoards.SetBoard(e, b.Clone(), s); e.AIPlayerColor = s; e.GetBestMove(s);          // a tie-breaking search happens first
                return e.GenerateRandomBlockedCellPositions(4, Start.Clone(), 123).ToList();
            }
            Assert.Equal(Blocks(false), Blocks(true));
        }

        [Fact]
        public void BlockedCellPlacement_IsReproducibleFromItsSeedAlone() {
            // GenerateRandomBlockedCellPositions(count, board, seed): the same seed must give the same squares from ANY engine, whatever its own rng did.
            // (An unfinished rename once left the x/y draws on the engine's own rng, so only the zone order followed the seed.)
            var a = new AtaxxAIEngine(Prm.Base(), new AIEngineConfig { Seed = 1 }).GenerateRandomBlockedCellPositions(5, Start.Clone(), 42);
            var b = new AtaxxAIEngine(Prm.Base(), new AIEngineConfig { Seed = 2 }).GenerateRandomBlockedCellPositions(5, Start.Clone(), 42);
            var c = new AtaxxAIEngine(Prm.Base()).GenerateRandomBlockedCellPositions(5, Start.Clone(), 42);
            Assert.Equal(a, b);
            Assert.Equal(a, c);
            Assert.NotEqual(a, new AtaxxAIEngine(Prm.Base()).GenerateRandomBlockedCellPositions(5, Start.Clone(), 43));
        }

        [Fact]
        public void TheSetting_ChangesWhichTiedMoveIsPlayed_NotAnyScore() {
            foreach (var (b, s) in TiedPositions(3)) {
                var a = Play(false, 1, b, s).scores; var c = Play(true, 1, b, s).scores;
                Assert.Equal(a.Select(x => x.Final).ToArray(), c.Select(x => x.Final).ToArray());
                Assert.Equal(a.Select(x => x.Strategic).ToArray(), c.Select(x => x.Strategic).ToArray());
            }
        }

        [Fact]
        public void TiesBreakOnExactScoresOnly_NotOnNearTies() {
            // With one clear best move the setting changes nothing, whatever the seed.
            int checkedPositions = 0;
            foreach (var (b, s) in RefactorGoldenTests.RandomPositions(60, 99)) {
                var baseline = Play(false, 1, b, s);
                if (baseline.scores.Count < 2 || baseline.scores[0].Final == baseline.scores[1].Final) continue;
                Assert.True(Play(true, 9, b, s).best.Equals(baseline.best));
                checkedPositions++;
            }
            Assert.True(checkedPositions >= 15, $"only {checkedPositions} untied positions were checked");
        }

        [Fact]
        public void TheSpecKey_TiesFalse_IsParsedValidatedAndPassedToTheEngine() {
            var spec = PlayerSpec.Parse("classic:depth=2,ties=false");
            Assert.Equal(false, spec.RandomTies);
            Assert.False(PlayerFactory.EffectiveRandomTies(spec));
            Assert.True(PlayerFactory.EffectiveRandomTies(PlayerSpec.Parse("classic:depth=3")));            // default
            Assert.True(PlayerFactory.EffectiveRandomTies(PlayerSpec.Parse("classic:depth=3,ties=true")));
            Assert.Equal(PlayerSpec.Parse("classic:depth=3").SearchKey(), PlayerSpec.Parse("classic:depth=3,ties=true").SearchKey());
            Assert.Contains("ties=false", spec.ToString());
            Assert.Contains("root ties=first", PlayerFactory.Describe(spec, false));   // (the tied positions below are depth-2 ties, so these players search depth 2)
            Assert.Contains("root ties=random", PlayerFactory.Describe(PlayerSpec.Parse("classic:depth=3"), false));
            Assert.Throws<FormatException>(() => PlayerSpec.Parse("classic:ties=maybe"));
            // a parameter override of the old name is now an unknown parameter, not a silent no-op
            Assert.Throws<FormatException>(() => PlayerSpec.Parse("classic:p.search.randomizeRootTies=false"));
            Assert.Throws<System.IO.InvalidDataException>(() => EngineParamsFile.Parse("{ \"search\": { \"randomizeRootTies\": false } }", Prm.Base(), "old"));

            // a player really plays deterministically with ties=false (the first ranked move, any seed) and varies by default
            var tied = TiedPositions(3);
            foreach (var (b, s) in tied) {
                var det = Enumerable.Range(1, 8).Select(seed => { using var p = PlayerFactory.Create(spec, null, seed, defaultTrain: true); return p.Choose(b, s).Action; }).Distinct().Count();
                Assert.Equal(1, det);
            }
            bool varied = false;
            foreach (var (b, s) in tied) {
                varied |= Enumerable.Range(1, 30).Select(seed => { using var p = PlayerFactory.Create(PlayerSpec.Parse("classic:depth=2,train=true"), null, seed, defaultTrain: true); return p.Choose(b, s).Action; }).Distinct().Count() > 1;
            }
            Assert.True(varied);
        }
    }
}
