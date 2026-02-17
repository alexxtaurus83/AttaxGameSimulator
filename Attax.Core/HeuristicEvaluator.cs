using System;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core {
    public class HeuristicEvaluator : IValueEvaluator {
        public float Evaluate(BitboardState board, PlayerColor sideToMove, byte ruleFlags) {
            PlayerColor enemy = SwitchPlayer(sideToMove);
            int finalEvaluationScore = 0;

            var (playerPieces, enemyPieces) = GetRedAndBlueCounts(board, sideToMove);
            int totalPieces = playerPieces + enemyPieces;
            int emptyCount = PopCount(board.EmptySquares());

            // Material
            finalEvaluationScore += (playerPieces - enemyPieces) * AttaxConstants.GetScaledMaterialWeight(totalPieces);

            // Corner and Edge
            finalEvaluationScore += GetCornerEdgeBonusEvaluation(board, sideToMove, enemy);

            // Phase Gating
            if (emptyCount > 14) {
                // Midgame
                ulong playerDestinations = GetMoveDestinationsBitboard(board, sideToMove);
                ulong enemyDestinations = GetMoveDestinationsBitboard(board, enemy);

                int mobility = PopCount(playerDestinations) - PopCount(enemyDestinations);
                finalEvaluationScore += mobility * AttaxConstants.GetScaledMobilityWeight(totalPieces);

                int playerPotential = GetPotentialMobilityInternal(board, playerDestinations);
                int enemyPotential = GetPotentialMobilityInternal(board, enemyDestinations);
                finalEvaluationScore += (playerPotential - enemyPotential) * AttaxConstants.EvaluateHeuristicConst.PotentialMobilityWeight;

                int centerControl = GetCenterControl(board, sideToMove) - GetCenterControl(board, enemy);
                finalEvaluationScore += centerControl * AttaxConstants.EvaluateHeuristicConst.CenterControlWeight;
            } else {
                // Endgame
                var playerStabilityBonus = GetStabilityBonus(board, sideToMove);
                var enemyStabilityBonus = GetStabilityBonus(board, enemy);

                int stability = playerStabilityBonus - enemyStabilityBonus;
                finalEvaluationScore += stability * AttaxConstants.GetDynamicStabilityMult(totalPieces);
            }

            return finalEvaluationScore;
        }

        // --- Helper Methods (Adapted to use BoardLookup) ---

        private (int corner, int edge) EvaluateCornerEdge(ulong playerPieces) {
            int cornerCount = PopCount(playerPieces & BoardLookup.CornerMask);
            int cornerScore = cornerCount * AttaxConstants.EvaluateCornerEdgeConst.cornerWeight;

            int edgeCount = PopCount(playerPieces & BoardLookup.EdgeMask);
            int edgeScore = edgeCount * AttaxConstants.EvaluateCornerEdgeConst.edgeWeight;

            return (cornerScore, edgeScore);
        }

        private int GetCornerEdgeBonusEvaluation(BitboardState boardState, PlayerColor player, PlayerColor enemy) {
            ulong playerPieces = (player == PlayerColor.Red) ? boardState.RedPieces : boardState.BluePieces;
            ulong enemyPieces = (enemy == PlayerColor.Red) ? boardState.RedPieces : boardState.BluePieces;

            var (playerCornerScore, playerEdgeScore) = EvaluateCornerEdge(playerPieces);
            var (enemyCornerScore, enemyEdgeScore) = EvaluateCornerEdge(enemyPieces);

            return playerCornerScore + playerEdgeScore - (enemyCornerScore + enemyEdgeScore);
        }

        private ulong GetMoveDestinationsBitboard(BitboardState board, PlayerColor player)
            => BitboardFeatures.GetMoveDestinationsBitboard(board, player);

        private int GetPotentialMobilityInternal(BitboardState boardState, ulong destinationSquares)
            => BitboardFeatures.GetPotentialMobilityInternal(boardState, destinationSquares);

        private int GetCenterControl(BitboardState boardState, PlayerColor player)
            => BitboardFeatures.GetCenterControl(boardState, player);

        private int GetStabilityBonus(BitboardState boardState, PlayerColor player)
            => BitboardFeatures.GetStabilityBonus(boardState, player);

        private bool IsStable(BitboardState boardState, int x, int y, PlayerColor player)
            => BitboardFeatures.IsStable(boardState, x, y, player);

        private bool HasAdjacentEmpty(BitboardState boardState, int x, int y)
            => BitboardFeatures.HasAdjacentEmpty(boardState, x, y);

        private int TrailingZeroCount(ulong value) => BitboardOps.TrailingZeroCount(value);
        private int PopCount(ulong value) => BitboardOps.PopCount(value);

        public (int p1Count, int p2Count) GetRedAndBlueCounts(BitboardState boardState, PlayerColor p1Color)
            => BitboardFeatures.GetRedAndBlueCounts(boardState, p1Color);
    }
}
