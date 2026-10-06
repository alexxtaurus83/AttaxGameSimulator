using System;
using System.Collections.Generic;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core {
    public struct PolicyChoice {
        /// <summary>False only when the side to move has no legal move (a terminal position, never passed).</summary>
        public bool HasMove;
        public Move Move;
        public int Action;
        /// <summary>Probability of the chosen action under the distribution it was drawn from (1 for greedy).</summary>
        public float Probability;
        public int LegalCount;
    }

    public static class PolicyLogitSelector {
        /// <summary>Selection from already computed logits for deterministic greedy play.</summary>
        public static PolicyChoice SelectGreedy(float[] logits, BitboardState board, PlayerColor side) {
            if (logits == null) throw new ArgumentNullException(nameof(logits));
            if (logits.Length != ActionCodec.ActionCount) throw new ArgumentException($"Expected {ActionCodec.ActionCount} logits.", nameof(logits));

            var mask = new bool[ActionCodec.ActionCount];
            int legalCount = ActionCodec.FillLegalMask(board, side, mask);
            
            if (legalCount == 0) return new PolicyChoice { HasMove = false, Action = ActionCodec.NoAction, LegalCount = 0 };

            int chosen = -1;
            float best = float.NegativeInfinity;
            
            for (int a = 0; a < ActionCodec.ActionCount; a++) {
                if (!float.IsFinite(logits[a])) throw new InvalidOperationException($"Logit at {a} is not finite.");
                if (mask[a]) {
                    float l = logits[a];
                    if (l > best) { 
                        best = l; 
                        chosen = a; 
                    }
                }
            }
            
            if (chosen == -1) {
                return new PolicyChoice { HasMove = false, Action = ActionCodec.NoAction, LegalCount = 0 };
            }

            if (!ActionCodec.TryDecode(chosen, board, side, out var move))
                throw new InvalidOperationException($"Selected action {chosen} is not legal; legal mask and decoder disagree.");

            return new PolicyChoice { HasMove = true, Move = move, Action = chosen, Probability = 1f, LegalCount = legalCount };
        }
    }
}
