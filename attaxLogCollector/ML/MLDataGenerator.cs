using Assets.Scripts.Base;
using Assets.Scripts.Base.Ataxx;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using static AtaxxAIEngine;

// A helper struct to pass the game result clearly.
public class GameResult {
    public AtaxxAIEngine.PlayerColor Winner;
    public bool IsDraw;
}

public class MLDataGenerator {

    // This is the main method called by the simulation runner.    
    public void AppendTrainingDataFromGame(AILogCoordinator gameLog, string filePath)
    {
        MLFeatureSet mLFeatureSet = new MLFeatureSet();
        GameResult result = GetResultFromFinalLogEntry(gameLog);
        if (result == null) return;

        var trainingLines = new List<string>();

        // The core logic for appending:
        // 1. Check if the file exists.
        // 2. If not, create it and write the header.
        // 3. Then, append all new data lines.
        if (!File.Exists(filePath))
        {
            File.WriteAllText(filePath, mLFeatureSet.GetCsvHeader() + System.Environment.NewLine);
        }

        foreach (var turnEntry in gameLog.GlobalLog.OrderBy(kvp => kvp.Key))
        {
            var turn = turnEntry.Value;
            if (turn.playerMove?.boardBeforeMove != null && !turn.playerMove.isHuman)
            {
                string line = GenerateTrainingLine(turn.playerMove, result);
                if (line != null) trainingLines.Add(line);
            }
            if (turn.opponentMove?.boardBeforeMove != null && !turn.opponentMove.isHuman)
            {
                string line = GenerateTrainingLine(turn.opponentMove, result);
                if (line != null) trainingLines.Add(line);
            }
        }

        if (trainingLines.Any())
        {
            File.AppendAllLines(filePath, trainingLines);
        }
    }
        
    private MLFeatureSet ExtractFeaturesFromMove(AILogCoordinator.MoveDetails move) {
        var board = ParseBoardFromString(move.boardBeforeMove);
        if (board == null) return null;
        FeatureExtractor featureExtractor = new FeatureExtractor();
        MLFeatureSet features = featureExtractor.CreateBaseFeatureSet(board, move.PlayerColor);

        var candidates = move.aIMoveDetails?.aiMoveCandidates?.Values;
        if (candidates != null && candidates.Any()) {
            features.CandidateCount = candidates.Count;
            var finalScores = candidates.Select(c => (float)c.finalScore).ToList();
            features.AvgCandidateFinalScore = finalScores.Average();
            features.MaxCandidateFinalScore = finalScores.Max();

            var chosenCandidate = candidates.FirstOrDefault(c => c.move.Equals(move.move));
            if (chosenCandidate != null) {
                features.ChosenMoveFinalScore = chosenCandidate.finalScore;
                var rankedCandidates = candidates.OrderByDescending(c => c.finalScore).ToList();
                features.ChosenMoveRank = rankedCandidates.FindIndex(c => c.move.Equals(move.move)) + 1;
            }
        }
        return features;
    }

    private string GenerateTrainingLine(AILogCoordinator.MoveDetails move, GameResult result) {
        float label = (result.IsDraw) ? 0.0f : (move.PlayerColor == result.Winner ? 1.0f : -1.0f);
        MLFeatureSet features = ExtractFeaturesFromMove(move);
        if (features == null) return null;

        var culture = CultureInfo.InvariantCulture;
        var featureValues = features.ToFloatList().Select(f => f.ToString(culture));
        return string.Join(",", featureValues) + "," + label.ToString(culture);
    }

    private GameResult GetResultFromFinalLogEntry(AILogCoordinator gameLog) {
        var lastTurnKvp = gameLog.GlobalLog.OrderByDescending(kvp => kvp.Key).FirstOrDefault();
        if (lastTurnKvp.Value == null) return null;

        var lastMoveDetails = lastTurnKvp.Value.opponentMove ?? lastTurnKvp.Value.playerMove;
        if (lastMoveDetails == null) return null;

        int redCount = lastMoveDetails.redCount;
        int blueCount = lastMoveDetails.blueCount;
        int pieceGain = lastMoveDetails.isClone ? 1 : 0;

        if (lastMoveDetails.PlayerColor == AtaxxAIEngine.PlayerColor.Red) {
            redCount += lastMoveDetails.flippedChips + pieceGain;
            blueCount -= lastMoveDetails.flippedChips;
        } else {
            blueCount += lastMoveDetails.flippedChips + pieceGain;
            redCount -= lastMoveDetails.flippedChips;
        }
        if (redCount == blueCount) return new GameResult { IsDraw = true };
        return new GameResult {
            Winner = redCount > blueCount ? AtaxxAIEngine.PlayerColor.Red : AtaxxAIEngine.PlayerColor.Blue,
            IsDraw = false
        };
    }

    private BitboardState ParseBoardFromString(string boardString) {
        int size = AttaxConstants.BaseConst.BoardSize;
        var boardState = new BitboardState();
        if (string.IsNullOrEmpty(boardString) || boardString.Length != size * size) {
            return boardState;
        }
        for (int i = 0; i < boardString.Length; i++) {
            PlayerColor color = (PlayerColor)(boardString[i] - '0');
            ulong bit = 1UL << i;
            switch (color) {
                case PlayerColor.Red:
                    boardState.RedPieces |= bit;
                    break;
                case PlayerColor.Blue:
                    boardState.BluePieces |= bit;
                    break;
                case PlayerColor.Blocked:
                    boardState.BlockedSquares |= bit;
                    break;
            }
        }
        return boardState;
    }
}
