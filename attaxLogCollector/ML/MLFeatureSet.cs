using System.Collections.Generic;

public class MLFeatureSet {    
    public List<float> BoardState { get; set; } = new List<float>();
    public float PieceDifferential { get; set; }
    public float TotalPieceCount { get; set; }
    public float MyMobility { get; set; }
    public float OpponentMobility { get; set; }
    public float MobilityDifferential { get; set; }
    public float CandidateCount { get; set; }
    public float AvgCandidateFinalScore { get; set; }
    public float MaxCandidateFinalScore { get; set; }
    public float ChosenMoveFinalScore { get; set; }
    public float ChosenMoveRank { get; set; }
    public float MyStability { get; set; }
    public float OpponentStability { get; set; }
    public float MyCenterControl { get; set; }
    public float OpponentCenterControl { get; set; }
    public float MyCornerScore { get; set; }
    public float MyEdgeScore { get; set; }

    /// <summary>
    /// Gets the header for the CSV file with descriptive feature names.
    /// </summary>
    public string GetCsvHeader() {        
        var boardFeatureNames = new List<string>();
        int boardSize = 7; 
        
        for (int y = 0; y < boardSize; y++) {
            for (int x = 0; x < boardSize; x++) {
                boardFeatureNames.Add($"X{x}_Y{y}_MyPiece");
                boardFeatureNames.Add($"X{x}_Y{y}_OpponentPiece");
                boardFeatureNames.Add($"X{x}_Y{y}_IsBlocked");
            }
        }

        var otherFeatureNames = new List<string>
        {
            "PieceDifferential", "TotalPieceCount", "MyMobility", "OpponentMobility", "MobilityDifferential",
            "CandidateCount", "AvgCandidateFinalScore", "MaxCandidateFinalScore", "ChosenMoveFinalScore", "ChosenMoveRank",
            "MyStability", "OpponentStability", "MyCenterControl", "OpponentCenterControl", "MyCornerScore", "MyEdgeScore"
        };

        return string.Join(",", boardFeatureNames.Concat(otherFeatureNames)) + ",Label";
    }

    /// <summary>
    /// Converts the entire feature set into a flat list of floats for the CSV.    
    /// </summary>
    public List<float> ToFloatList() {
        var list = new List<float>();
        list.AddRange(BoardState);
        list.Add(PieceDifferential);
        list.Add(TotalPieceCount);
        list.Add(MyMobility);
        list.Add(OpponentMobility);
        list.Add(MobilityDifferential);
        list.Add(CandidateCount);
        list.Add(AvgCandidateFinalScore);
        list.Add(MaxCandidateFinalScore);
        list.Add(ChosenMoveFinalScore);
        list.Add(ChosenMoveRank);
        list.Add(MyStability);
        list.Add(OpponentStability);
        list.Add(MyCenterControl);
        list.Add(OpponentCenterControl);
        list.Add(MyCornerScore);
        list.Add(MyEdgeScore);
        return list;
    }
}