using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Attax.Core;
using Attax.Core.Abilities;
using Attax.Core.Utils;
using Attax.Play;

namespace Attax.Agents {
    /// <summary>One ability cast made by the in-process rules runner.</summary>
    public sealed class RulesCastRecord {
        public int Ply;
        public PlayerColor Side;
        public AbilityKind Kind;
        public int Level;
        public int Cost;
        public AbilityLevelEffect Effect;
        public AbilityReason Reason;
        public float EstimatedGainChips;
        public float NetGainChips;
        public int CandidatesVerified;
        public bool AutoPassed;
    }

    /// <summary>Position value measurement (<see cref="AbilityDecider.MeasureValues"/>) taken at the start of an own turn.</summary>
    public sealed class RulesValueSample {
        public int Ply;
        public PlayerColor Side;
        public int Depth;
        public IReadOnlyList<AbilityValueSample> Values = Array.Empty<AbilityValueSample>();
    }

    public sealed class RulesSideStats {
        /// <summary>Turns the side completed (credits per own turn = income / OwnTurns).</summary>
        public int OwnTurns;
        /// <summary>Credits this side received because the opponent flipped its chips.</summary>
        public int CaptureCredits;
        /// <summary>Credits this side received for landing on an enemy footprint.</summary>
        public int FootprintCredits;
        /// <summary>Opponent chips this side flipped.</summary>
        public int Flips;
        public int CreditsSpent;
        public int Casts;
        public int Fallbacks;
        public double SearchMs;
        public double AbilityDecideMs;
    }

    public sealed class RulesGameResult {
        public int Plies;
        public bool Terminal;
        /// <summary>+1 red, -1 blue, 0 draw.</summary>
        public int Winner;
        public int RedChips;
        public int BlueChips;
        public int Playable;
        public double WallMs;
        public double ValueSampleMs;
        public RulesSideStats Red = new RulesSideStats();
        public RulesSideStats Blue = new RulesSideStats();
        public List<RulesCastRecord> Casts = new List<RulesCastRecord>();
        public List<RulesValueSample> Values = new List<RulesValueSample>();
    }

    public sealed class RulesGameOptions {
        public GameRulesConfig Rules = null!;
        /// <summary>Ability AI settings; abilities are cast only when <see cref="Abilities"/> is true.</summary>
        public AiAbilityConfig? Ai;
        public bool Abilities = true;
        /// <summary>Action cap; a capped game is decided by chip count.</summary>
        public int MaxActions = 400;
        /// <summary>Up to this many own-turn positions per game are measured with <see cref="AbilityDecider.MeasureValues"/>.</summary>
        public int SampleValues;
        /// <summary>Seed for choosing which turns are sampled.</summary>
        public int Seed;
    }

    /// <summary>
    /// In-process game loop on the authoritative <see cref="GameState"/> (credits, footprints, abilities): every action goes through
    /// <see cref="AttaxGame.TryApply"/>. Used by the economy calibration tool; the training loop (<c>LocalGameRunner</c>) is untouched.
    /// </summary>
    public static class RulesGameRunner {
        private const int FirstSampledTurn = 6;
        private const int MinEmptyCellsToSample = 6;

        public static RulesGameResult Run(BitboardState board, IPlayer red, IPlayer blue, RulesGameOptions options) {
            if (board == null) throw new ArgumentNullException(nameof(board));
            if (red == null) throw new ArgumentNullException(nameof(red));
            if (blue == null) throw new ArgumentNullException(nameof(blue));
            if (options == null || options.Rules == null) throw new ArgumentNullException(nameof(options));

            AiAbilityConfig? ai = null;
            if (options.Abilities) {
                var src = options.Ai ?? throw new ArgumentException("Abilities need an AiAbilityConfig.", nameof(options));
                ai = new AiAbilityConfig {
                    schemaVersion = src.schemaVersion, aiAbilitiesEnabled = true, creditValueChips = src.creditValueChips,
                    minGainChips = src.minGainChips, maxCandidatesVerified = src.maxCandidatesVerified,
                    verifyDepthOffset = src.verifyDepthOffset, savingEnabled = src.savingEnabled
                };
                var errors = ai.Validate();
                if (errors.Count > 0) throw new ArgumentException("Invalid AiAbilityConfig: " + string.Join("; ", errors));
            }

            var state = GameState.CreateInitial(board, PlayerColor.Red, options.Rules);
            var result = new RulesGameResult { Playable = state.PlayableCells };
            var sampleTurns = ChooseSampleTurns(options.SampleValues, options.Seed);
            var wall = Stopwatch.StartNew();
            bool turnStart = true;

            while (state.Result == TerminalResult.NotOver && state.ActionCount < options.MaxActions) {
                PlayerColor side = state.SideToMove;
                IPlayer player = side == PlayerColor.Red ? red : blue;
                var stats = side == PlayerColor.Red ? result.Red : result.Blue;
                var search = player as ISearchEnginePlayer;

                if (turnStart && search != null && sampleTurns.Contains(state.TurnNumber) &&
                    BitboardOps.PopCount(state.Board.EmptySquares()) >= MinEmptyCellsToSample) {
                    long s0 = Stopwatch.GetTimestamp();
                    var values = AbilityDecider.MeasureValues(state, search.Engine, ai ?? DefaultMeasureConfig(), search.SearchDepth, CancellationToken.None);
                    result.ValueSampleMs += Elapsed(s0);
                    if (values.Count > 0) result.Values.Add(new RulesValueSample { Ply = state.ActionCount, Side = side, Depth = search.SearchDepth, Values = values });
                }
                turnStart = false;

                ActionOutcome outcome;
                if (ai != null && search != null && state.AbilityActionsUsedThisTurn == 0) {
                    long a0 = Stopwatch.GetTimestamp();
                    var verdict = AbilityDecider.Decide(state, search.Engine, ai, search.SearchDepth, CancellationToken.None);
                    stats.AbilityDecideMs += Elapsed(a0);
                    if (verdict.UseAbility) {
                        int ply = state.ActionCount;
                        var err = AttaxGame.TryApply(state, side, verdict.Action, out outcome);
                        if (err != ActionError.None) throw new InvalidOperationException($"AbilityDecider chose an illegal action ({verdict.Kind} L{verdict.Level}): {err}.");
                        stats.Casts++;
                        result.Casts.Add(new RulesCastRecord {
                            Ply = ply, Side = side, Kind = verdict.Kind, Level = verdict.Level, Cost = outcome.CreditsSpent, Effect = verdict.Effect,
                            Reason = verdict.Reason, NetGainChips = verdict.NetGainChips,
                            EstimatedGainChips = verdict.NetGainChips + outcome.CreditsSpent * ai.creditValueChips,
                            CandidatesVerified = verdict.CandidatesVerified, AutoPassed = outcome.AutoPassed
                        });
                        turnStart = Account(result, side, outcome);
                        continue;
                    }
                }

                var action = ChooseStandardMove(state, side, player, search, stats);
                var applyError = AttaxGame.TryApply(state, side, action, out outcome);
                if (applyError != ActionError.None) throw new InvalidOperationException($"Illegal move chosen for {side}: {applyError}.");
                turnStart = Account(result, side, outcome);
            }

            wall.Stop();
            result.WallMs = wall.Elapsed.TotalMilliseconds;
            result.Plies = state.ActionCount;
            result.RedChips = BitboardOps.PopCount(state.Board.RedPieces);
            result.BlueChips = BitboardOps.PopCount(state.Board.BluePieces);
            result.Terminal = state.Result != TerminalResult.NotOver;
            if (result.Terminal) result.Winner = state.Result == TerminalResult.RedWins ? 1 : state.Result == TerminalResult.BlueWins ? -1 : 0;
            else result.Winner = result.RedChips > result.BlueChips ? 1 : result.BlueChips > result.RedChips ? -1 : 0;
            return result;
        }

        private static AiAbilityConfig DefaultMeasureConfig() => new AiAbilityConfig {
            schemaVersion = AiAbilityConfig.CurrentSchemaVersion, aiAbilitiesEnabled = true, creditValueChips = 0f, minGainChips = 0f,
            maxCandidatesVerified = 8, verifyDepthOffset = 1, savingEnabled = false
        };

        private static double Elapsed(long startTimestamp) => (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

        // Credits and flips are booked per side; returns true when the action ended the turn.
        private static bool Account(RulesGameResult result, PlayerColor actor, in ActionOutcome outcome) {
            var mine = actor == PlayerColor.Red ? result.Red : result.Blue;
            var theirs = actor == PlayerColor.Red ? result.Blue : result.Red;
            mine.FootprintCredits += outcome.ActorCreditsGained;
            theirs.CaptureCredits += outcome.VictimCreditsGained;
            mine.Flips += BitboardOps.PopCount(outcome.FlippedMask);
            mine.CreditsSpent += outcome.CreditsSpent;
            if (outcome.TurnAdvanced) mine.OwnTurns++;
            return outcome.TurnAdvanced;
        }

        private static GameAction ChooseStandardMove(GameState state, PlayerColor side, IPlayer player, ISearchEnginePlayer? search, RulesSideStats stats) {
            ulong frozen = state.FrozenSourcesThisTurn;
            ulong forbidden = state.ForbiddenLandingsThisTurn;
            MoveDecision decision = (frozen | forbidden) != 0UL && search != null
                ? search.ChooseRestricted(state.Board, side, frozen, forbidden)
                : player.Choose(state.Board, side);
            stats.SearchMs += decision.Milliseconds;

            if (decision.HasMove && AttaxGame.IsLegalStandardMove(state, side, decision.Move)) return GameAction.MoveAction(decision.Move);

            // A player that does not know the turn restriction: take the first legal restricted move.
            stats.Fallbacks++;
            for (int from = 0; from < 49; from++) {
                ulong targets = AttaxGame.GetStandardMoveTargets(state, side, from % 7, from / 7);
                if (targets == 0UL) continue;
                int to = BitboardOps.TrailingZeroCount(targets);
                return GameAction.MoveAction(new Move(from % 7, from / 7, to % 7, to / 7));
            }
            throw new InvalidOperationException($"No legal move remains for {side} although the game is not over.");
        }

        // Distinct turn numbers (both sides' turns are counted) at which values are measured.
        private static HashSet<int> ChooseSampleTurns(int count, int seed) {
            var set = new HashSet<int>();
            if (count <= 0) return set;
            int span = Math.Max(30, count * 3);
            int target = Math.Min(count, span);
            var rng = new Random(seed); // NOSONAR: sampling schedule, not security
            while (set.Count < target) set.Add(FirstSampledTurn + rng.Next(span));
            return set;
        }
    }
}
