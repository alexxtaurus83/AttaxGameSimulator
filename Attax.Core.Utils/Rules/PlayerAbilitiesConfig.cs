using System;

namespace Attax.Core.Utils {
    [Serializable]
    public sealed class PlayerAbilitiesConfig {
        public AbilityToggles red;
        public AbilityToggles blue;

        public PlayerAbilitiesConfig Clone() {
            PlayerAbilitiesConfig copy = new PlayerAbilitiesConfig();
            copy.red = this.red != null ? this.red.Clone() : null;
            copy.blue = this.blue != null ? this.blue.Clone() : null;
            return copy;
        }

        public bool IsEnabled(PlayerColor player, AbilityKind kind) {
            if (kind == AbilityKind.None) return true;
            if (player == PlayerColor.Red) {
                return red != null && red.IsEnabled(kind);
            }
            if (player == PlayerColor.Blue) {
                return blue != null && blue.IsEnabled(kind);
            }
            return false;
        }
    }
}
