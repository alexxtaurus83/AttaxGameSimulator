using System;
using System.Collections.Generic;
using Attax.Core;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Model {
    /// <summary>
    /// Value-search consumer of the model contract: exposes only the VALUE output, so the engine can run negamax on it.
    /// It cannot be used to read the policy, and PolicyMoveSelector cannot be fed a value-only view, so the two
    /// heads cannot be swapped by accident.
    /// Output is in [-1, 1] from the perspective of <c>sideToMove</c>, which is what the engine's Evaluate expects.
    /// </summary>
    public sealed class ModelValueEvaluator : IValueEvaluator, IBatchValueEvaluator {
        private readonly IPolicyValueModel model;

        public ModelValueEvaluator(IPolicyValueModel model) {
            this.model = model ?? throw new ArgumentNullException(nameof(model));
        }

        public float Evaluate(BitboardState board, PlayerColor sideToMove) {
            return EvaluateBatch(new[] { board }, sideToMove)[0];
        }

        public float[] EvaluateBatch(IReadOnlyList<BitboardState> boards, PlayerColor sideToMove) {
            if (boards == null) throw new ArgumentNullException(nameof(boards));
            if (boards.Count == 0) return Array.Empty<float>();
            var output = model.Evaluate(boards, sideToMove);
            output.ThrowIfInvalid();
            return output.Values;
        }
    }
}
