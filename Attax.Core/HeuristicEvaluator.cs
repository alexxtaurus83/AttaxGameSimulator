using System;
using Attax.Core.Utils;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core {

    /// <summary>
    /// The hand-written leaf evaluator. It has no built-in values: the engine builds it from its own parameters
    /// (<c>new AtaxxAIEngine(engineParams, config)</c> with no evaluator), so the evaluator and the root/search groups cannot disagree.
    /// Constructing one directly from <see cref="EngineParams"/> is for tests and tools that evaluate positions without an engine.
    /// </summary>
    public class HeuristicEvaluator : IValueEvaluator {
        private readonly CompiledEngineParams p;

        /// <param name="engineParams">Required. Validated; throws ArgumentException listing every problem.</param>
        public HeuristicEvaluator(EngineParams engineParams) {
            if (engineParams == null) throw new ArgumentNullException(nameof(engineParams), "HeuristicEvaluator needs the engine parameters (load engine-params.json).");
            p = engineParams.Compile();
        }

        internal HeuristicEvaluator(CompiledEngineParams compiled) {
            p = compiled ?? throw new ArgumentNullException(nameof(compiled));
        }

        public float Evaluate(BitboardState board, PlayerColor sideToMove) {
            PlayerColor enemy = SwitchPlayer(sideToMove);
            int finalEvaluationScore = 0;

            var (playerPieces, enemyPieces) = GetRedAndBlueCounts(board, sideToMove);
            int totalPieces = playerPieces + enemyPieces;
            int emptyCount = PopCount(board.EmptySquares());

            // Material
            finalEvaluationScore += (playerPieces - enemyPieces) * p.MaterialWeight[totalPieces];

            // Corner and Edge
            finalEvaluationScore += GetCornerEdgeBonusEvaluation(board, sideToMove, enemy);

            // Phase Gating
            if (emptyCount > p.EndgameEmptyThreshold) {
                // Midgame
                ulong playerDestinations = GetMoveDestinationsBitboard(board, sideToMove);
                ulong enemyDestinations = GetMoveDestinationsBitboard(board, enemy);

                int mobility = PopCount(playerDestinations) - PopCount(enemyDestinations);
                finalEvaluationScore += mobility * p.MobilityWeight[totalPieces];

                int playerPotential = GetPotentialMobilityInternal(board, playerDestinations);
                int enemyPotential = GetPotentialMobilityInternal(board, enemyDestinations);
                finalEvaluationScore += (playerPotential - enemyPotential) * p.PotentialMobilityWeight;

                int centerControl = GetCenterControl(board, sideToMove) - GetCenterControl(board, enemy);
                finalEvaluationScore += centerControl * p.CenterControlWeight;
            } else {
                // Endgame
                var playerStabilityBonus = GetStabilityBonus(board, sideToMove);
                var enemyStabilityBonus = GetStabilityBonus(board, enemy);

                int stability = playerStabilityBonus - enemyStabilityBonus;
                finalEvaluationScore += stability * p.StabilityMult[totalPieces];
            }

            return finalEvaluationScore;
        }

        // --- Helper Methods (Adapted to use BoardLookup) ---

        private (int corner, int edge) EvaluateCornerEdge(ulong playerPieces) {
            int cornerCount = PopCount(playerPieces & BoardLookup.CornerMask);
            int cornerScore = cornerCount * p.CornerWeight;

            int edgeCount = PopCount(playerPieces & BoardLookup.EdgeMask);
            int edgeScore = edgeCount * p.EdgeWeight;

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

        // Same loop as BitboardFeatures.GetCenterControl, reading this evaluator's table instead of the static one.
        private int GetCenterControl(BitboardState boardState, PlayerColor player) {
            int controlScore = 0;
            ulong remaining = (player == PlayerColor.Red) ? boardState.RedPieces : boardState.BluePieces;
            var table = p.CenterControlTable;
            while (remaining > 0) {
                int pieceIndex = BitboardOps.TrailingZeroCount(remaining);
                controlScore += table[pieceIndex];
                remaining &= remaining - 1;
            }
            return controlScore;
        }

        private int GetStabilityBonus(BitboardState boardState, PlayerColor player)
            => BitboardFeatures.GetStabilityBonus(boardState, player);

        private int PopCount(ulong value) => BitboardOps.PopCount(value);

        public (int p1Count, int p2Count) GetRedAndBlueCounts(BitboardState boardState, PlayerColor p1Color)
            => BitboardFeatures.GetRedAndBlueCounts(boardState, p1Color);
    }
}
