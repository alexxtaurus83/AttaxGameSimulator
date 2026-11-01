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
            if (!decision.HasMove) {
                throw new InvalidOperationException($"No move returned by player for {side} when game state is not terminal.");
            }

            return GameAction.MoveAction(decision.Move);
        }
    }
}
