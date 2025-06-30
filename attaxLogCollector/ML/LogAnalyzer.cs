// This file not in use. Planning to implement later for better log quality.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static AILogCoordinator;

namespace attaxLogCollector.ML {
    public class LogAnalyzer {

        private const int MIN_GAME_TURNS = 15;
        private const int LOPSIDED_THRESHOLD = 15; // Max piece difference in early game
        private const int LOPSIDED_CHECK_UNTIL_TURN = 10;
        private const int BLUNDER_SCORE_DELTA = 5000;
        private const int MAX_BLUNDERS_PER_GAME = 3;

        public bool IsHighQuality(Dictionary<int, TurnLog> gameLog) {
            // Game Length Check
            if (gameLog.Count < MIN_GAME_TURNS) {
                Console.WriteLine("REJECTED: Game too short.");
                return false;
            }

            int blunderCount = 0;

            foreach (var turnEntry in gameLog) {
                var turnLog = turnEntry.Value;
                var turnNumber = turnEntry.Key;

                // Analyze both the player and opponent move in the turn
                var movesToAnalyze = new List<MoveDetails> { turnLog.playerMove, turnLog.opponentMove };

                foreach (var moveDetails in movesToAnalyze) {
                    if (moveDetails?.aIMoveDetails == null) continue;

                    // Lopsided Game Check (only for early game)
                    if (turnNumber < LOPSIDED_CHECK_UNTIL_TURN) {
                        if (Math.Abs(moveDetails.redCount - moveDetails.blueCount) > LOPSIDED_THRESHOLD) {
                            Console.WriteLine($"REJECTED: Game lopsided at turn {turnNumber}.");
                            return false;
                        }
                    }

                    // Blunder Detection
                    var candidates = moveDetails.aIMoveDetails.aiMoveCandidates;
                    if (candidates == null || candidates.Count == 0) continue;

                    int maxPossibleScore = candidates.Values.Max(c => c.finalScore);
                    int playedMoveId = moveDetails.aIMoveDetails.moveId;

                    // Find the score of the move that was actually played
                    if (candidates.TryGetValue(playedMoveId, out var playedMoveCandidate)) {
                        int scoreOfPlayedMove = playedMoveCandidate.finalScore;
                        int delta = maxPossibleScore - scoreOfPlayedMove;

                        if (delta > BLUNDER_SCORE_DELTA) {
                            blunderCount++;
                        }
                    }
                }
            }

            if (blunderCount > MAX_BLUNDERS_PER_GAME) {
                Console.WriteLine($"REJECTED: Found {blunderCount} blunders.");
                return false;
            }

            // If all checks pass, it's a high-quality log
            Console.WriteLine("ACCEPTED: Log is high quality.");
            return true;
        }
    }
}
