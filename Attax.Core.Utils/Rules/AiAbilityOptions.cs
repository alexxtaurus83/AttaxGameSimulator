using System;

namespace Attax.Core.Utils {
    public static class AiAbilityOptions {
        public static bool Resolve(AiAbilityConfig file, bool? hostOverride) {
            if (hostOverride.HasValue) return hostOverride.Value;
            return file != null && file.aiAbilitiesEnabled;
        }
    }
}
