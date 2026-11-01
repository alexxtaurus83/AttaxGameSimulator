using System;
using System.Collections.Generic;

namespace Attax.Core.Utils {
    [Serializable]
    public sealed class AiAbilityConfig {
        public const int CurrentSchemaVersion = 1;
        public int schemaVersion;
        public bool aiAbilitiesEnabled;
        public int useChancePercent;
        public int creditReserve;
        public int maxUsesPerGame;
        public bool safetyFilter;
        public string targetSelection;

        public AiAbilityConfig Clone() {
            AiAbilityConfig copy = new AiAbilityConfig();
            copy.schemaVersion = this.schemaVersion;
            copy.aiAbilitiesEnabled = this.aiAbilitiesEnabled;
            copy.useChancePercent = this.useChancePercent;
            copy.creditReserve = this.creditReserve;
            copy.maxUsesPerGame = this.maxUsesPerGame;
            copy.safetyFilter = this.safetyFilter;
            copy.targetSelection = this.targetSelection;
            return copy;
        }

        public List<string> Validate() {
            List<string> errors = new List<string>();

            if (schemaVersion != CurrentSchemaVersion) {
                errors.Add("Unsupported schemaVersion: " + schemaVersion);
            }
            if (useChancePercent < 0 || useChancePercent > 100) {
                errors.Add("useChancePercent must be between 0 and 100");
            }
            if (creditReserve < 0) {
                errors.Add("creditReserve cannot be negative");
            }
            if (maxUsesPerGame < 0) {
                errors.Add("maxUsesPerGame cannot be negative");
            }
            if (!string.IsNullOrEmpty(targetSelection) &&
                !string.Equals(targetSelection, "random", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(targetSelection, "scored", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(targetSelection, "engine", StringComparison.OrdinalIgnoreCase)) {
                errors.Add("targetSelection must be 'random', 'scored', 'engine', or empty/null: " + targetSelection);
            }

            return errors;
        }
    }
}
