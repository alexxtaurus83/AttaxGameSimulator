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
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="policy"/> or <paramref name="engineParams"/> is <c>null</c>.</exception>
        public static AiAbilityPolicy UseEngineEvaluator(this AiAbilityPolicy policy, EngineParams engineParams) {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            if (engineParams == null) throw new ArgumentNullException(nameof(engineParams));
            var evaluator = new HeuristicEvaluator(engineParams);
            policy.TargetEvaluator = evaluator.Evaluate;
            return policy;
        }

        /// <summary>
        /// Configures the policy to evaluate ability target choices with an existing leaf value evaluator.
        /// </summary>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="policy"/> or <paramref name="evaluator"/> is <c>null</c>.</exception>
        public static AiAbilityPolicy UseEngineEvaluator(this AiAbilityPolicy policy, IValueEvaluator evaluator) {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            if (evaluator == null) throw new ArgumentNullException(nameof(evaluator));
            policy.TargetEvaluator = evaluator.Evaluate;
            return policy;
        }
    }
}
