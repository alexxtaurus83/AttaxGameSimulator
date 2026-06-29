using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Attax.Core {
    public class AttaxConstants {

        public static class BaseConst {
            public const int BoardSize = 7;
            public const int KillerMoveMaxDepth = 10;
        }
        public static class ChipsCountConst {
            public const int earlyGame = 14;
            public const int lateGame = 35;
            public const int startGame = 4;
            public const int endGame = 49;
        }

        public static class DynamicDepthConst {
            public const int depthToDecrease = 2;
            public const int depthToIncrease = 2;
        }
        public static class GetBestMoveConst {

            public const int earlyGameCloneBonus = 25;
            public const int lateGameCloneBonus = 2;

            public const int riskAversionFactor = 10; //12
            public const int flipWeight = 25;
            public const int centerWeightMultiplier = 1;
        }

        // Root-only move-selection nudges, expressed in HEURISTIC POINTS (same scale as the
        // leaf evaluator output, before *evaluationScale). 1 point here is comparable to ~1/35
        // of a piece (material weight is 30-40). These are deliberately small: material already
        // gives a clone its intrinsic +1-piece edge, and the depth>=2 search already accounts
        // for flips and the opponent's reply, so these only fine-tune move choice.
        public static class RootBonusConst {
            // Phase-aware extra clone preference (on top of material). ~6 = mild, ~20 = strong,
            // ~40 = dominant (≈ one piece). Opening favors clones; late game lets captures win.
            public const double cloneOpening = 6.0;   // totalPieces <= openingMax
            public const double cloneMid = 3.0;       // openingMax < totalPieces <= midMax
            public const double cloneLate = 1.0;      // totalPieces > midMax

            public const int openingMax = 8;
            public const int midMax = 20;

            // Per-flip immediate-capture nudge. ~0 because the search already sees flips as
            // material one ply down; a tiny value only breaks near-ties toward active moves.
            public const double flipPoints = 0.0;

            // Per-opponent-flip-risk penalty: a mild safety margin beyond what the search sees.
            public const double riskPoints = 0.8;

            // Center/positional nudge per GetPositionalValue unit (value is 1..7).
            public const double positionalPoints = 0.3;

            // Penalty (heuristic points) for a jump that neither captures nor grows — pure
            // repositioning that abandons a piece's spot. Steers the root choice toward growth
            // (clones/captures). Root-only, so the leaf evaluation / positional "feel" is untouched.
            // Folded into the clone (growth) axis in the move log. Raise if too many jumps persist.
            public const double nonCapturingJumpPenalty = 6.0;
        }

        // Phase-aware extra clone preference in heuristic points. Replaces the old
        // GetDynamicCloneBonus * DynamicCloneBonusMultiplier hack (which was ~1/1,000,000 the
        // scale of the search score and therefore numerically inert).
        public static double GetCloneBonusPoints(int totalPieces) {
            if (totalPieces <= RootBonusConst.openingMax) return RootBonusConst.cloneOpening;
            if (totalPieces <= RootBonusConst.midMax) return RootBonusConst.cloneMid;
            return RootBonusConst.cloneLate;
        }

        // Root-only "comeback" aggression. aiDelta = aiPieces - opponentPieces from the AI's
        // (fixed root) perspective. When far behind, the AI chases captures, fears risk less,
        // and values passive clones less. Applied ONLY at the root so it never breaks the
        // negamax antisymmetry of the leaf evaluator.
        public static double GetAggressionFactor(int aiDelta) {
            if (aiDelta >= -2) return 1.0;  // even or ahead: normal style
            if (aiDelta >= -5) return 1.5;  // losing by 3-5
            if (aiDelta >= -8) return 2.0;  // losing by 6-8
            return 2.5;                     // losing by 9+: fight hard
        }
        public static class EvaluateHeuristicConst {

            public const int earlyMaterialWeight = 30;
            public const int lateMaterialWeight = 40;

            public const int earlyMobilityWeight = 3;
            public const int lateMobilityWeight = 4;

            public const int PotentialMobilityWeight = 3;

            public const int CenterControlWeight = 2;
            public const int StabilityWeight = 14;
        }

        public static class EvaluateCornerEdgeConst {
            public const int cornerWeight = 5;
            public const int edgeWeight = 2;
        }

        public static int GetDynamicCloneBonus(int totalPieces) {
            if (totalPieces <= ChipsCountConst.startGame) return AttaxConstants.GetBestMoveConst.earlyGameCloneBonus;
            if (totalPieces >= ChipsCountConst.endGame) return AttaxConstants.GetBestMoveConst.lateGameCloneBonus;

            float progress = (float)(totalPieces - ChipsCountConst.startGame) / (ChipsCountConst.endGame - ChipsCountConst.startGame);
            return (int)Math.Round(AttaxConstants.GetBestMoveConst.earlyGameCloneBonus -
                (AttaxConstants.GetBestMoveConst.earlyGameCloneBonus - AttaxConstants.GetBestMoveConst.lateGameCloneBonus) * progress);
        }

        public static int GetDynamicStabilityMult(int totalPieces) {
            return totalPieces >= ChipsCountConst.lateGame ? EvaluateHeuristicConst.StabilityWeight : 1;
        }

        public static int GetScaledMobilityWeight(int totalPieces) {
            if (totalPieces <= ChipsCountConst.startGame) return EvaluateHeuristicConst.earlyMobilityWeight;
            if (totalPieces >= ChipsCountConst.endGame) return EvaluateHeuristicConst.lateMobilityWeight;
            // Linearly scale the weight downwards
            float progress = (float)(totalPieces - ChipsCountConst.startGame) / (ChipsCountConst.endGame - ChipsCountConst.startGame);
            return (int)(EvaluateHeuristicConst.earlyMobilityWeight - (EvaluateHeuristicConst.earlyMobilityWeight - EvaluateHeuristicConst.lateMobilityWeight) * progress);
        }

        public static int GetScaledMaterialWeight(int totalPieces) {
            if (totalPieces <= ChipsCountConst.startGame) return EvaluateHeuristicConst.earlyMaterialWeight;
            if (totalPieces >= ChipsCountConst.endGame) return EvaluateHeuristicConst.lateMaterialWeight;
            // Linearly scale the weight between the min and max values
            float progress = (float)(totalPieces - ChipsCountConst.startGame) / (ChipsCountConst.endGame - ChipsCountConst.startGame);
            return (int)(EvaluateHeuristicConst.earlyMaterialWeight + (EvaluateHeuristicConst.lateMaterialWeight - EvaluateHeuristicConst.earlyMaterialWeight) * progress);
        }



    }
}
