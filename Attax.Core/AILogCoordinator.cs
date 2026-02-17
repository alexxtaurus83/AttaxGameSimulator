using Attax.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
namespace Attax.Core {
    public class AILogCoordinator {
        public int TurnIndex { get; set; }
        public Dictionary<int, TurnLog> GlobalLog { get; set; }
        public AILogCoordinator() {
            this.TurnIndex = 0;
            this.GlobalLog = new Dictionary<int, TurnLog>();
        }

        [Serializable]
        public class AIMoveCandidates {
            public AtaxxAIEngine.Move move { get; set; }
            public bool isClone { get; set; }
            public int flipsCount { get; set; }
            public int opponentFlipRiskCount { get; set; }
            public int evalScore { get; set; }     // Raw evaluation from AlphaBeta
            public int finalScore { get; set; }    // Sum after all bonuses/penalties
            public int positionalBonusScore { get; set; }
            public int cloneBonusScore { get; set; }
            public int flippedScore { get; set; }
            public int opponentFlipRiskScore { get; set; }
            //public int mobilityScore;
            //public int centerControlScore;
            //public int stabilityScore;
        }
        [Serializable]
        public class AIMoveDetails {
            public int moveId { get; set; }
            public int time { get; set; }
            public int aiDepthUsed { get; set; }
            public Dictionary<int, AIMoveCandidates> aiMoveCandidates { get; set; }
            public AIMoveDetails(int moveId, int time, int aiDepthUsed, Dictionary<int, AIMoveCandidates> aiMoveCandidates) {
                this.moveId = moveId;
                this.time = time;
                this.aiDepthUsed = aiDepthUsed;
                this.aiMoveCandidates = aiMoveCandidates;
            }
        }
        [Serializable]
        public class MoveDetails {
            public AtaxxAIEngine.Move move { get; set; }
            [JsonConverter(typeof(StringEnumConverter))]
            public AtaxxAIEngine.PlayerColor PlayerColor { get; set; }
            public bool isClone { get; set; }
            public int flippedChips { get; set; }
            public string boardBeforeMove { get; set; }
            public bool isHuman { get; set; }
            public int redCount { get; set; }
            public int blueCount { get; set; }
            public AIMoveDetails aIMoveDetails { get; set; }
            public MoveDetails() { }
            public MoveDetails(AtaxxAIEngine.Move move, AtaxxAIEngine.PlayerColor playerColor, bool isClone, int flippedChips, string boardBeforeMove, bool isHuman = true, AIMoveDetails aIMoveDetails = null) {
                this.move = move;
                this.PlayerColor = playerColor;
                this.isClone = isClone;
                this.flippedChips = flippedChips;
                this.boardBeforeMove = boardBeforeMove;
                this.isHuman = isHuman;
                this.aIMoveDetails = aIMoveDetails;
            }
        }
        [Serializable]
        public class TurnLog {
            public MoveDetails playerMove { get; set; }
            public MoveDetails opponentMove { get; set; }
            public bool useOrthogonalOnlyCapture { get; set; }
            public TurnLog(bool useOrthogonalOnlyCapture) { this.useOrthogonalOnlyCapture = useOrthogonalOnlyCapture; }
        }

        public class AIEffectiveness {
            public string PlayerDescription { get; set; } // Describes the AI being primarily evaluated (e.g., "Blue AI - Mobility On")
            public string OpponentDescription { get; set; }      // Describes the opponent AI (e.g., "Red AI - Baseline")
            public AtaxxAIEngine.PlayerColor EvaluatedPlayerColor { get; set; }
            public int FinalPieceDifference { get; set; } // Piece difference from the EvaluatedPlayer's perspective (positive if EvaluatedPlayer has more pieces at the end)
            public string GameOutcome { get; set; } // e.g., "Evaluated AI Wins", "Opponent Wins", "Draw by Pieces", "Draw by Stalemate"
            public int TotalMovesByEvaluatedAI { get; set; }
            public int TotalPiecesFlippedByEvaluatedAI { get; set; }
            public float AverageMoveTime_ms { get; set; }
            public float MaxMoveTime_ms { get; set; }
            public float MinMoveTime_ms { get; set; }

            public int NumberOfOvertimeMoves { get; set; }        // Moves > 1 second
                                                                  // Optional: If full logs with AIMoveCandidates become available and are parsed
            public double AverageEvalScoreOfChosenMoves_ByEvaluatedAI { get; set; }
            public double AverageFinalScoreOfChosenMoves_ByEvaluatedAI { get; set; }


            public override string ToString() {
                return $"--- Effectiveness: {PlayerDescription} vs {OpponentDescription} ---\n" +
                       $"Outcome for {EvaluatedPlayerColor}: {GameOutcome}\n" +
                       $"Final Piece Difference: {FinalPieceDifference} ({EvaluatedPlayerColor} perspective)\n" +
                       $"Total Moves: {TotalMovesByEvaluatedAI}\n" +
                       $"Total Pieces Flipped: {TotalPiecesFlippedByEvaluatedAI}\n" +
                       $"Average Eval Score: {AverageEvalScoreOfChosenMoves_ByEvaluatedAI}\n" +
                       $"Average Final Score: {AverageFinalScoreOfChosenMoves_ByEvaluatedAI}\n" +
                       $"Avg. Move Time: {AverageMoveTime_ms:F0} ms\n" +
                       $"Max Move Time: {MaxMoveTime_ms:F0} ms\n" +
                       $"Min Move Time: {MinMoveTime_ms:F0} ms\n" +
                       $"Overtime Moves (>1s): {NumberOfOvertimeMoves}";
            }
        }
    }
}