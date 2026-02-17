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
