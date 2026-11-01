using System.Collections.Generic;
using Attax.Core.Utils;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core {
    public interface IBatchValueEvaluator {
        float[] EvaluateBatch(IReadOnlyList<BitboardState> boards, PlayerColor sideToMove);
    }
}
