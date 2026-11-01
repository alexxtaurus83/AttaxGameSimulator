using Attax.Core;
using Attax.Play;

namespace Attax.Core.Tests {
    /// <summary>Test access to the engine parameters. The values come from engine-params.json (copied next to the test assembly), never from code.</summary>
    internal static class Prm {
        /// <summary>A private, mutable copy of the base file. The root tie rule is NOT a parameter; tests that pin exact moves set
        /// <c>DisableRandomRootTies = true</c> on the engine config (see <see cref="Det"/>).</summary>
        public static EngineParams Base() => EngineParamsFile.LoadBase();

        /// <summary>An engine config with the deterministic tie rule (the first of equally scored root moves), for goldens and "same position, same move" tests.</summary>
        public static AtaxxAIEngine.AIEngineConfig Det(AtaxxAIEngine.AIEngineConfig c) { c.DisableRandomRootTies = true; return c; }
        public static HeuristicEvaluator Eval() => new HeuristicEvaluator(Base());
    }
}