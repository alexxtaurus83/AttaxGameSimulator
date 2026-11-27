using System.Diagnostics;
using Attax.Agents;
using Attax.Core;
using Attax.Core.Utils;
using Attax.Play;

namespace Attax.Console.Economy {
    internal sealed class EconomySimSettings {
        public IReadOnlyList<string> Boards = Array.Empty<string>();
        public int Games = 40;
        public string Red = "classic:depth=3,quiescence=true";
        public string Blue = "classic:depth=3,quiescence=true";
        public bool SwapColors;
        public bool Abilities = true;
        public int SampleValues;
        public int OpeningPlies;
        public int MaxPlies = 400;
        public int Seed = 20261124;
        public int Parallel;
        public string Tag = "";
        public EconomyContext Context = null!;
        public Action<int, int>? Progress;
    }

    /// <summary>Plays the economy games of one session: stratified boards, seeded openings, optional colour swap.</summary>
    internal static class EconomySim {
        public static List<string> ResolveBoards(string spec) {
            if (string.IsNullOrWhiteSpace(spec) || string.Equals(spec.Trim(), "all", StringComparison.OrdinalIgnoreCase))
                return BoardShapeCatalog.All.Select(s => s.Id).ToList();
            var ids = new List<string>();
            foreach (string part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                if (!BoardShapeCatalog.TryGetById(part, out var shape)) throw new ArgumentException($"Unknown board shape '{part}'.");
                ids.Add(shape.Id);
            }
            if (ids.Count == 0) throw new ArgumentException("--boards is empty.");
            return ids;
        }

        public static List<EconomyGameRecord> Run(EconomySimSettings s) {
            if (s.Games < 1) throw new ArgumentException("--games must be >= 1.");
            if (s.OpeningPlies < 0 || s.OpeningPlies > 4) throw new ArgumentException("--opening-plies must be in 0..4.");
            if (s.Boards.Count == 0) throw new ArgumentException("No boards selected.");
            var redSpec = PlayerSpec.Parse(s.Red);
            var blueSpec = PlayerSpec.Parse(s.Blue);
            int perOpening = s.SwapColors ? 2 : 1;
            int total = s.Games * perOpening;
            int parallel = s.Parallel > 0 ? s.Parallel : Environment.ProcessorCount;
            var records = new EconomyGameRecord[total];
            int done = 0;

            Parallel.For(0, total, new ParallelOptions { MaxDegreeOfParallelism = parallel }, index => {
                int opening = index / perOpening;
                bool swapped = s.SwapColors && index % 2 == 1;
                records[index] = PlayGame(s, redSpec, blueSpec, opening, index, swapped);
                s.Progress?.Invoke(Interlocked.Increment(ref done), total);
            });
            return records.ToList();
        }

        /// <summary>Balanced board for opening <paramref name="opening"/>: openings are dealt round-robin so every board gets the same count (+1 for the earliest).</summary>
        internal static string BoardFor(IReadOnlyList<string> boards, int opening) => boards[opening % boards.Count];

        internal static int OpeningSeed(int seed, int opening) => Program.CombineSeed(seed, opening * 104729 + 7);

        private static EconomyGameRecord PlayGame(EconomySimSettings s, PlayerSpec redSpec, PlayerSpec blueSpec, int opening, int index, bool swapped) {
            string boardId = BoardFor(s.Boards, opening);
            int openingSeed = OpeningSeed(s.Seed, opening);
            var board = StartBoard.CreateFromShape(boardId, openingSeed);
            var specRed = swapped ? blueSpec : redSpec;
            var specBlue = swapped ? redSpec : blueSpec;

            using var red = PlayerFactory.Create(specRed, null!, Program.CombineSeed(s.Seed, index * 2 + 1), defaultTrain: false);
            using var blue = PlayerFactory.Create(specBlue, null!, Program.CombineSeed(s.Seed, index * 2 + 2), defaultTrain: false);
            var result = RulesGameRunner.Run(board, red, blue, new RulesGameOptions {
                Rules = s.Context.Rules, Ai = s.Context.Ai, Abilities = s.Abilities, MaxActions = s.MaxPlies,
                SampleValues = s.SampleValues, Seed = Program.CombineSeed(s.Seed, opening * 17 + 3),
                OpeningPlies = s.OpeningPlies, OpeningSeed = Program.CombineSeed(openingSeed, 131)
            });
            return ToRecord(s, result, boardId, openingSeed, index, swapped, specRed, specBlue);
        }

        internal static EconomyGameRecord ToRecord(EconomySimSettings s, RulesGameResult r, string boardId, int seed, int index, bool swapped, PlayerSpec red, PlayerSpec blue) {
            var rec = new EconomyGameRecord {
                SchemaVersion = EconomyLog.CurrentSchemaVersion, Tag = s.Tag, Game = index, BoardId = boardId, Playable = r.Playable, Seed = seed,
                SessionGameCount = s.Games * (s.SwapColors ? 2 : 1),
                Red = red.ToString(), Blue = blue.ToString(), Swapped = swapped, Abilities = s.Abilities,
                RulesHash = s.Context.RulesHash, AiConfigHash = s.Context.AiHash, EngineParamsHash = s.Context.EngineParamsHash,
                BuildId = s.Context.BuildId, SamplingMethod = RulesGameRunner.SamplingMethod, RedTurnStarts = r.RedTurnStarts, BlueTurnStarts = r.BlueTurnStarts,
                TelemetryMethod = RulesGameRunner.TelemetryMethod, OpeningPlies = s.OpeningPlies, OpeningPliesPlayed = r.OpeningPliesPlayed,
                OpeningPosition = r.OpeningPosition.ToString("X16"),
                Plies = r.Plies, Terminal = r.Terminal, Winner = r.Winner, RedChips = r.RedChips, BlueChips = r.BlueChips,
                WallMs = r.WallMs, SearchMsRed = r.Red.SearchMs, SearchMsBlue = r.Blue.SearchMs,
                AbilityDecideMsRed = r.Red.AbilityDecideMs, AbilityDecideMsBlue = r.Blue.AbilityDecideMs, ValueSampleMs = r.ValueSampleMs,
                RedIncome = Income(r.Red), BlueIncome = Income(r.Blue)
            };
            foreach (var c in r.Casts) {
                rec.Casts.Add(new EconomyCastRecord {
                    Ply = c.Ply, Side = c.Side.ToString(), Kind = c.Kind.ToString(), Level = c.Level, Cost = c.Cost, Effect = c.Effect.ToString(),
                    Reason = c.Reason.ToString(), EstimatedGainChips = c.EstimatedGainChips, NetGainChips = c.NetGainChips,
                    CandidatesVerified = c.CandidatesVerified, AutoPassed = c.AutoPassed
                });
            }
            foreach (var v in r.Values) {
                foreach (var m in v.Values) {
                    rec.Values.Add(new EconomyValueSample {
                        Ply = v.Ply, OwnTurn = v.OwnTurn, Side = v.Side.ToString(), Depth = v.Depth, Kind = m.Kind.ToString(), Level = m.Level, HasCandidate = m.HasCandidate,
                        HasVerification = m.HasVerification, Terminal = m.TerminalResult != TerminalResult.NotOver, ExceedsCreditCap = m.ExceedsCreditCap,
                        GainChips = m.GainChips
                    });
                }
            }
            return rec;
        }

        private static EconomySideIncome Income(RulesSideStats s) => new EconomySideIncome {
            OwnTurns = s.OwnTurns, CaptureCredits = s.CaptureCredits, FootprintCredits = s.FootprintCredits, Flips = s.Flips,
            CreditsLostToCap = s.CreditsLostToCap, TurnStartCredits = s.TurnStartCredits,
            CreditsSpent = s.CreditsSpent, Casts = s.Casts, SearchMs = s.SearchMs, AbilityDecideMs = s.AbilityDecideMs
        };

        /// <summary>Hash of everything a game decided (no wall-clock fields): identical seeds must give identical hashes.</summary>
        internal static string DeterministicHash(IEnumerable<EconomyGameRecord> records) {
            var sb = new System.Text.StringBuilder();
            foreach (var r in records.OrderBy(x => x.Game)) {
                sb.Append(r.Game).Append('|').Append(r.BoardId).Append('|').Append(r.Plies).Append('|').Append(r.Winner).Append('|')
                  .Append(r.RedChips).Append('|').Append(r.BlueChips).Append('|')
                  .Append(r.OpeningPosition).Append('|').Append(r.OpeningPliesPlayed).Append('|')
                  .Append(r.RedIncome.CreditsLostToCap).Append('|').Append(r.BlueIncome.CreditsLostToCap).Append('|')
                  .Append(string.Join(",", r.RedIncome.TurnStartCredits ?? new List<int>())).Append('|')
                  .Append(string.Join(",", r.BlueIncome.TurnStartCredits ?? new List<int>())).Append('|')
                  .Append(r.RedIncome.CaptureCredits).Append('|').Append(r.RedIncome.FootprintCredits).Append('|')
                  .Append(r.BlueIncome.CaptureCredits).Append('|').Append(r.BlueIncome.FootprintCredits).Append('|');
                foreach (var c in r.Casts) sb.Append(c.Ply).Append(c.Side).Append(c.Kind).Append(c.Level).Append(',');
                foreach (var v in r.Values) sb.Append(v.Ply).Append(v.Side).Append(v.OwnTurn).Append(v.Kind).Append(v.Level).Append(v.GainChips?.ToString("R") ?? "-").Append(',');
                sb.Append('\n');
            }
            return EconomyContext.Sha(sb.ToString());
        }

        public static string SummaryLine(IReadOnlyList<EconomyGameRecord> records, double wallSeconds) {
            int n = Math.Max(1, records.Count);
            double casts = records.Sum(r => r.RedIncome.Casts + r.BlueIncome.Casts) / (2.0 * n);
            return $"SUMMARY games={records.Count} wall_s={wallSeconds:0.0} ms_per_game={records.Average(r => r.WallMs):0} avg_plies={records.Average(r => r.Plies):0.0} " +
                   $"casts_per_side={casts:0.00} value_samples={records.Sum(r => r.Values.Count)}";
        }
    }
}
