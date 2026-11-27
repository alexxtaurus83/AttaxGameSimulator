using System;
using Attax.Core;

namespace Attax.Model {
    /// <summary>
    /// Direct-policy mode: one forward pass, no search. Illegal logits are masked out before selection, so an
    /// illegal move can never be returned. Temperature is in logit units (p ~ exp(logit / T)); T <= 0 is greedy.
    /// </summary>
    public static class PolicyMoveSelector {
        public static PolicyChoice Select(IPolicyValueModel model, BitboardState board, PlayerColor side,
                                          float temperature, int topK, Random rng) {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (board == null) throw new ArgumentNullException(nameof(board));

            var mask = new bool[ActionCodec.ActionCount];
            int legalCount = ActionCodec.FillLegalMask(board, side, mask);
            if (legalCount == 0) return new PolicyChoice { HasMove = false, Action = ActionCodec.NoAction, LegalCount = 0 };

            var output = model.Evaluate(new[] { board }, side);
            output.ThrowIfInvalid();
            return SelectFromLogits(output.Logits, 0, output.Values[0], mask, legalCount, board, side, temperature, topK, rng);
        }

        /// <summary>Selection from already computed logits (used by tests and by batched callers).</summary>
        public static PolicyChoice SelectFromLogits(float[] logits, int offset, float value, bool[] legalMask, int legalCount,
                                                    BitboardState board, PlayerColor side, float temperature, int topK, Random rng)
            => PolicyLogitSelector.SelectFromLogits(logits, offset, value, legalMask, legalCount, board, side, temperature, topK, rng);
    }
}
