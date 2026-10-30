using System;
using Attax.Core.Utils;

namespace Attax.Core {
    /// <summary>
    /// Extension methods for wiring AI engine evaluators into <see cref="AiAbilityPolicy"/>
    /// for smart target selection in "engine" mode.
    /// </summary>
    public static class AiAbilityPolicyExtensions {
        /// <summary>
        /// Configures the policy to evaluate ability target choices with the heuristic evaluator compiled from engine parameters.
        /// </summary>
        public static AiAbilityPolicy UseEngineEvaluator(this AiAbilityPolicy policy, EngineParams engineParams) {
            if (policy == null) return null;
            if (engineParams == null) throw new ArgumentNullException(nameof(engineParams));
            var evaluator = new HeuristicEvaluator(engineParams);
            policy.TargetEvaluator = evaluator.Evaluate;
            return policy;
        }

        /// <summary>
        /// Configures the policy to evaluate ability target choices with an existing leaf value evaluator.
        /// </summary>
        public static AiAbilityPolicy UseEngineEvaluator(this AiAbilityPolicy policy, IValueEvaluator evaluator) {
            if (policy == null) return null;
            if (evaluator == null) throw new ArgumentNullException(nameof(evaluator));
            policy.TargetEvaluator = evaluator.Evaluate;
            return policy;
        }
    }
}
