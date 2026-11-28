#nullable enable
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
        public int OwnTurn;
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
        public long CreditsLostToCap;
        public List<int> TurnStartCredits = new List<int>();
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
        public int OpeningPliesPlayed;
        public ulong OpeningPosition;
        public int RedTurnStarts, BlueTurnStarts;
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
        public int OpeningPlies;
        public int OpeningSeed;
    }

    /// <summary>
    /// In-process game loop on the authoritative <see cref="GameState"/> (credits, footprints, abilities): every action goes through
    /// <see cref="AttaxGame.TryApply"/>. Used by the economy calibration tool; the training loop (<c>LocalGameRunner</c>) is untouched.
    /// </summary>
    public static class RulesGameRunner {
        public const string SamplingMethod = "own-turn-4-to-end-minus-4-v1";
        public const string TelemetryMethod = "seeded-legal-opening-turn-balances-cap-loss-v1";

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
                    minGainChips = src.minGainChips, freeCastMinGainChips = src.freeCastMinGainChips,
                    maxCandidatesVerified = src.maxCandidatesVerified, verifyDepthOffset = src.verifyDepthOffset, savingEnabled = src.savingEnabled
                };
                var errors = ai.Validate();
                if (errors.Count > 0) throw new ArgumentException("Invalid AiAbilityConfig: " + string.Join("; ", errors));
            }

            var state = GameState.CreateInitial(board, PlayerColor.Red, options.Rules);
            var result = new RulesGameResult { Playable = state.PlayableCells };
            result.OpeningPosition = state.Board.ZobristHash;
            var openingRandom = options.OpeningPlies > 0 ? new Random(options.OpeningSeed) : null;
            var positions = options.SampleValues > 0 ? new List<(GameState State, int OwnTurn, ISearchEnginePlayer Player)>() : null;
            var wall = Stopwatch.StartNew();
            bool turnStart = true;

            var planners = new Dictionary<IPlayer, AgentTurnPlanner> {
                [red] = new AgentTurnPlanner(red, ai),
                [blue] = new AgentTurnPlanner(blue, ai)
            };
            while (state.Result == TerminalResult.NotOver && state.ActionCount < options.MaxActions) {
                PlayerColor side = state.SideToMove;
                IPlayer player = side == PlayerColor.Red ? red : blue;
                var stats = side == PlayerColor.Red ? result.Red : result.Blue;
                var search = player as ISearchEnginePlayer;

                if (turnStart) {
                    int ownTurn = side == PlayerColor.Red ? ++result.RedTurnStarts : ++result.BlueTurnStarts;
                    stats.TurnStartCredits.Add(state.GetCredits(side));
                    if (positions != null && search != null && ownTurn >= 4) positions.Add((state.Clone(), ownTurn, search));
                }
                turnStart = false;

                ActionOutcome outcome;
                ulong enemyFootprints = side == PlayerColor.Red ? state.BlueFootprints : state.RedFootprints;
                bool opening = result.OpeningPliesPlayed < options.OpeningPlies;
                if (opening) {
                    var action = ChooseOpeningMove(state, side, openingRandom!);
                    var applyError = AttaxGame.TryApply(state, side, action, out outcome);
                    if (applyError != ActionError.None) throw new InvalidOperationException($"Illegal move chosen for {side}: {applyError}.");
                    turnStart = Account(result, options.Rules.economy, side, action.Kind, enemyFootprints, outcome);
                    result.OpeningPliesPlayed++;
                    result.OpeningPosition = state.Board.ZobristHash;
                    continue;
                }

                var plan = planners[player].DecideDetailed(state, side, allowAbilities: ai != null, CancellationToken.None);
                stats.AbilityDecideMs += plan.AbilityDecideMs;
                stats.SearchMs += plan.SearchMs;
                if (plan.UsedFallback) stats.Fallbacks++;

                int ply = state.ActionCount;
                var err = AttaxGame.TryApply(state, side, plan.Action, out outcome);
                if (err != ActionError.None) {
                    throw new InvalidOperationException(plan.IsAbility
                        ? $"AbilityDecider chose an illegal action ({plan.AbilityDecision.Kind} L{plan.AbilityDecision.Level}): {err}."
                        : $"Illegal move chosen for {side}: {err}.");
                }
                if (plan.IsAbility) {
                    var verdict = plan.AbilityDecision;
                    stats.Casts++;
                    result.Casts.Add(new RulesCastRecord {
                        Ply = ply, Side = side, Kind = verdict.Kind, Level = verdict.Level, Cost = outcome.CreditsSpent, Effect = verdict.Effect,
                        Reason = verdict.Reason, NetGainChips = verdict.NetGainChips,
                        EstimatedGainChips = verdict.NetGainChips + outcome.CreditsSpent * ai!.creditValueChips,
                        CandidatesVerified = verdict.CandidatesVerified, AutoPassed = outcome.AutoPassed
                    });
                }
                turnStart = Account(result, options.Rules.economy, side, plan.Action.Kind, enemyFootprints, outcome);
            }

            if (positions != null) {
                positions.RemoveAll(p => !IsEligibleSampleTurn(p.OwnTurn,
                    p.State.SideToMove == PlayerColor.Red ? result.RedTurnStarts : result.BlueTurnStarts));
                var measureConfig = ai ?? DefaultMeasureConfig();
                var rng = new Random(options.Seed); // NOSONAR: sampling schedule, not security
                int count = Math.Min(options.SampleValues, positions.Count);
                for (int i = 0; i < count; i++) {
                    int j = rng.Next(i, positions.Count);
                    (positions[i], positions[j]) = (positions[j], positions[i]);
                    var (snapshot, ownTurn, search) = positions[i];
                    long s0 = Stopwatch.GetTimestamp();
                    var values = AbilityDecider.MeasureValues(snapshot, search.Engine, measureConfig, search.SearchDepth, CancellationToken.None);
                    result.ValueSampleMs += Elapsed(s0);
                    if (values.Count > 0) result.Values.Add(new RulesValueSample {
                        Ply = snapshot.ActionCount, OwnTurn = ownTurn, Side = snapshot.SideToMove, Depth = search.SearchDepth, Values = values
                    });
                }
            }
            wall.Stop();
            result.WallMs = wall.Elapsed.TotalMilliseconds;
            result.Plies = state.ActionCount;
            result.RedChips = AttaxGame.GetScore(state, PlayerColor.Red);
            result.BlueChips = AttaxGame.GetScore(state, PlayerColor.Blue);
            var endResult = state.Result != TerminalResult.NotOver
                ? state.Result
                : AttaxRules.AdjudicateResult(state.Board, state.RedFootprints, state.BlueFootprints, WinRule.Chips);
            result.Terminal = state.Result != TerminalResult.NotOver;
            result.Winner = endResult == TerminalResult.RedWins ? 1 : endResult == TerminalResult.BlueWins ? -1 : 0;
            return result;
        }

        private static AiAbilityConfig DefaultMeasureConfig() => new AiAbilityConfig {
            schemaVersion = AiAbilityConfig.CurrentSchemaVersion, aiAbilitiesEnabled = true, creditValueChips = 0f, minGainChips = 0f,
            maxCandidatesVerified = 8, verifyDepthOffset = 1, savingEnabled = false
        };

        private static double Elapsed(long startTimestamp) => (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

        // Credits and flips are booked per side; returns true when the action ended the turn.
        private static bool Account(RulesGameResult result, EconomyRules economy, PlayerColor actor, ActionKind kind, ulong enemyFootprints, in ActionOutcome outcome) {
            var mine = actor == PlayerColor.Red ? result.Red : result.Blue;
            var theirs = actor == PlayerColor.Red ? result.Blue : result.Red;
            mine.FootprintCredits += outcome.ActorCreditsGained;
            theirs.CaptureCredits += outcome.VictimCreditsGained;
            int flips = BitboardOps.PopCount(outcome.FlippedMask);
            mine.Flips += flips;
            if (economy.enabled) {
                theirs.CreditsLostToCap += (long)flips * economy.captureReward - outcome.VictimCreditsGained;
                if ((kind == ActionKind.Move || kind == ActionKind.Spawn || kind == ActionKind.SuperClone) && (outcome.PlacedMask & enemyFootprints) != 0)
                    mine.CreditsLostToCap += economy.footprintReward - outcome.ActorCreditsGained;
            }
            mine.CreditsSpent += outcome.CreditsSpent;
            if (outcome.TurnAdvanced) mine.OwnTurns++;
            return outcome.TurnAdvanced;
        }

        private static GameAction ChooseOpeningMove(GameState state, PlayerColor side, Random random) {
            // Reservoir sampling gives each legal source/target pair the same chance without allocating a move list.
            Move selected = default;
            int count = 0;
            ulong sources = side == PlayerColor.Red ? state.Board.RedPieces : state.Board.BluePieces;
            while (sources != 0UL) {
                int from = BitboardOps.TrailingZeroCount(sources);
                sources &= sources - 1;
                ulong targets = AttaxGame.GetStandardMoveTargets(state, side, from % 7, from / 7);
                while (targets != 0UL) {
                    int to = BitboardOps.TrailingZeroCount(targets);
                    targets &= targets - 1;
                    if (random.Next(++count) == 0) selected = new Move(from % 7, from / 7, to % 7, to / 7);
                }
            }
            if (count == 0) throw new InvalidOperationException($"No legal opening move remains for {side}.");
            return GameAction.MoveAction(selected);
        }

        public static bool IsEligibleSampleTurn(int ownTurn, int end) => ownTurn >= 4 && ownTurn <= end - 4;
    }
}
