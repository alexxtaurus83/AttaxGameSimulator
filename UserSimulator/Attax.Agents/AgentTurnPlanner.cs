using System;
using System.Threading;
using Attax.Core;
using Attax.Core.Abilities;
using Attax.Core.Utils;
using Attax.Play;

namespace Attax.Agents {
    /// <summary>
    /// Chooses one action for a bot: an ability when <see cref="AbilityDecider"/> verifies one (search players only; the decider uses the bot's own
    /// engine), otherwise a standard move that honours the turn's restriction masks.
    /// </summary>
    public sealed class AgentTurnPlanner {
        private readonly IPlayer player;
        private readonly AiAbilityConfig? abilityConfig;

        /// <param name="abilityConfig">Schema-v2 AI ability config; null or <c>aiAbilitiesEnabled = false</c> disables abilities.</param>
        public AgentTurnPlanner(IPlayer player, AiAbilityConfig? abilityConfig = null) {
            this.player = player ?? throw new ArgumentNullException(nameof(player));
            if (abilityConfig != null) {
                var errors = abilityConfig.Validate();
                if (errors.Count > 0) throw new ArgumentException("Invalid AiAbilityConfig: " + string.Join("; ", errors), nameof(abilityConfig));
            }
            this.abilityConfig = abilityConfig;
        }

        public GameAction Decide(GameState state, PlayerColor side, out bool isAbility, out MoveDecision decision) {
            if (state == null) throw new ArgumentNullException(nameof(state));

            decision = default;
            isAbility = false;

            var searchPlayer = player as ISearchEnginePlayer;
            if (abilityConfig != null && abilityConfig.aiAbilitiesEnabled && searchPlayer != null &&
                side == state.SideToMove && state.AbilityActionsUsedThisTurn == 0) {
                var verdict = AbilityDecider.Decide(state, searchPlayer.Engine, abilityConfig, searchPlayer.SearchDepth, CancellationToken.None);
                if (verdict.UseAbility) {
                    isAbility = true;
                    return verdict.Action;
                }
            }

            ulong frozen = side == state.SideToMove ? state.FrozenSourcesThisTurn : 0UL;
            ulong forbidden = side == state.SideToMove ? state.ForbiddenLandingsThisTurn : 0UL;
            if ((frozen | forbidden) != 0UL && searchPlayer != null) {
                decision = searchPlayer.ChooseRestricted(state.Board, side, frozen, forbidden);
            } else {
                decision = player.Choose(state.Board, side);
            }

            if ((frozen | forbidden) != 0UL && (!decision.HasMove || !AttaxGame.IsLegalStandardMove(state, side, decision.Move))) {
                // Non-search players do not know the turn masks: take the first legal restricted move.
                for (int from = 0; from < 49; from++) {
                    ulong targets = AttaxGame.GetStandardMoveTargets(state, side, from % 7, from / 7);
                    if (targets == 0) continue;
                    int to = BitboardOps.TrailingZeroCount(targets);
                    var move = new Move(from % 7, from / 7, to % 7, to / 7);
                    decision = new MoveDecision { HasMove = true, Move = move, Action = ActionCodec.Encode(move), UsedFallback = true };
                    return GameAction.MoveAction(move);
                }
                throw new InvalidOperationException($"No restriction-legal move remains for {side}.");
            }
            if (!decision.HasMove) {
                throw new InvalidOperationException($"No move returned by player for {side} when game state is not terminal.");
            }

            return GameAction.MoveAction(decision.Move);
        }
    }
}
