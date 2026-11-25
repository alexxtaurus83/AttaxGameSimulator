using System;
using Attax.Core;
using Attax.Core.Utils;
using Attax.Play;

namespace Attax.Agents {
    public sealed class AgentTurnPlanner {
        private readonly IPlayer player;
        private readonly AiAbilityPolicy? abilities;

        public AgentTurnPlanner(IPlayer player, AiAbilityPolicy? abilities = null) {
            this.player = player ?? throw new ArgumentNullException(nameof(player));
            this.abilities = abilities;
        }

        public GameAction Decide(GameState state, PlayerColor side, out bool isAbility, out MoveDecision decision) {
            if (state == null) throw new ArgumentNullException(nameof(state));

            decision = default;
            isAbility = false;

            if (abilities != null && abilities.Enabled) {
                if (abilities.TryChoose(state, side, out var choice)) {
                    isAbility = true;
                    return choice.Action;
                }
            }

            decision = player.Choose(state.Board, side);
            if (side == state.SideToMove && (state.FrozenSourcesThisTurn | state.ForbiddenLandingsThisTurn) != 0 &&
                (!decision.HasMove || !AttaxGame.IsLegalStandardMove(state, side, decision.Move))) {
                // Temporary online-bot fallback. The raw search stays unchanged until 01c.
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
