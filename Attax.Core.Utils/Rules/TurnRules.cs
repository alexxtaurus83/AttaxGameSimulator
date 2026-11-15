using System;

namespace Attax.Core.Utils {
    [Serializable]
    public sealed class TurnRules {
        public int maxAbilityActionsPerTurn;

        internal TurnRules Clone() {
            var copy = new TurnRules();
            copy.maxAbilityActionsPerTurn = this.maxAbilityActionsPerTurn;
            return copy;
        }
    }
}
