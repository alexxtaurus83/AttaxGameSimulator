namespace Attax.Core {
    /// <summary>
    /// Structural constants only: things that define the board and the array sizes built from it, and so cannot be tuned.
    /// Every TUNABLE number (evaluator weights, root bonuses, search gates) lives in <see cref="EngineParams"/>, which callers
    /// load from their own JSON and pass to the engine. The legacy tunables that used to live here are the field defaults of
    /// EngineParams (same values); the ones nothing ever read (earlyGame, depthToDecrease, depthToIncrease,
    /// earlyGameCloneBonus, lateGameCloneBonus, riskAversionFactor, flipWeight, GetDynamicCloneBonus) were deleted.
    /// </summary>
    public class AttaxConstants {

        public static class BaseConst {
            public const int BoardSize = 7;
            public const int KillerMoveMaxDepth = 10;
        }
    }
}
