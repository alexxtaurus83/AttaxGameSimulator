using System;
using Attax.Core.Utils;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core {
    public interface IValueEvaluator {
        float Evaluate(BitboardState board, PlayerColor sideToMove);
    }
}
