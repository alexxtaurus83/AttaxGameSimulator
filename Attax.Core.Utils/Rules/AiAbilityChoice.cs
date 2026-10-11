using System;

namespace Attax.Core.Utils {
    public struct AiAbilityChoice {
        public GameAction Action;
        public AbilityKind Ability;
        public bool EndsTurn;
    }
}
