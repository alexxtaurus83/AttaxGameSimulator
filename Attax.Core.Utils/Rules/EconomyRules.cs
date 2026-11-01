using System;

namespace Attax.Core.Utils {
    [Serializable]
    public sealed class EconomyRules {
        public bool enabled;
        public int startingCredits;
        public int maxCredits;
        public int captureReward;
        public int footprintReward;
        public AbilityCosts abilityCosts;

        public EconomyRules Clone() {
            EconomyRules copy = new EconomyRules();
            copy.enabled = this.enabled;
            copy.startingCredits = this.startingCredits;
            copy.maxCredits = this.maxCredits;
            copy.captureReward = this.captureReward;
            copy.footprintReward = this.footprintReward;
            copy.abilityCosts = this.abilityCosts != null ? this.abilityCosts.Clone() : null;
            return copy;
        }
    }
}
