using System.Collections.Generic;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core {
    public interface IBatchValueEvaluator {
        float[] EvaluateBatch(IReadOnlyList<BitboardState> boards, PlayerColor sideToMove, byte ruleFlags);
    }
}
