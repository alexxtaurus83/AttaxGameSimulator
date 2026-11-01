using System;
using System.Collections.Generic;
using Attax.Core;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Model {
    public struct PolicyChoice {
        /// <summary>False only when the side to move has no legal move (a terminal position, never passed).</summary>
        public bool HasMove;
        public Move Move;
        public int Action;
        /// <summary>Probability of the chosen action under the distribution it was drawn from (1 for greedy).</summary>
        public float Probability;
        /// <summary>Model value for the side to move, from the same forward pass.</summary>
        public float Value;
        public int LegalCount;
    }

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
                                                    BitboardState board, PlayerColor side, float temperature, int topK, Random rng) {
            var legal = new List<int>(legalCount);
            for (int a = 0; a < ActionCodec.ActionCount; a++) if (legalMask[a]) legal.Add(a);
            if (legal.Count == 0) return new PolicyChoice { HasMove = false, Action = ActionCodec.NoAction, LegalCount = 0 };

            int chosen;
            float prob;
            if (temperature <= 0f || legal.Count == 1) {
                chosen = legal[0];
                float best = logits[offset + chosen];
                for (int i = 1; i < legal.Count; i++) {
                    float l = logits[offset + legal[i]];
                    if (l > best) { best = l; chosen = legal[i]; }   // ties keep the lowest index: deterministic
                }
                prob = 1f;
            } else {
                // Keep the topK highest-logit legal actions (all when topK <= 0), then sample softmax(logit / T).
                legal.Sort((x, y) => {
                    int c = logits[offset + y].CompareTo(logits[offset + x]);
                    return c != 0 ? c : x.CompareTo(y);
                });
                int k = topK <= 0 ? legal.Count : Math.Min(topK, legal.Count);
                double max = logits[offset + legal[0]];
                var w = new double[k];
                double sum = 0;
                for (int i = 0; i < k; i++) {
                    w[i] = Math.Exp((logits[offset + legal[i]] - max) / temperature);
                    sum += w[i];
                }
                double r = rng.NextDouble() * sum;
                int pick = k - 1;
                double acc = 0;
                for (int i = 0; i < k; i++) {
                    acc += w[i];
                    if (r <= acc) { pick = i; break; }
                }
                chosen = legal[pick];
                prob = (float)(w[pick] / sum);
            }

            if (!ActionCodec.TryDecode(chosen, board, side, out var move))
                throw new InvalidOperationException($"Selected action {chosen} is not legal; legal mask and decoder disagree.");

            return new PolicyChoice { HasMove = true, Move = move, Action = chosen, Probability = prob, Value = value, LegalCount = legalCount };
        }
    }
}
