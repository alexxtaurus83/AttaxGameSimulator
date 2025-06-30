using Assets.Scripts.Base;
using Assets.Scripts.Base.Ataxx;

public class FeatureExtractor {    
    private AtaxxAIEngine helperEngine = new AtaxxAIEngine();

    /// <summary>
    /// The core method that creates a rich feature set from a given board state.
    /// It calculates all features that do NOT depend on logged AIMoveCandidates.
    /// </summary>
    /// <returns>A populated MLFeatureSet object.</returns>
    public MLFeatureSet CreateBaseFeatureSet(BitboardState board, AtaxxAIEngine.PlayerColor currentPlayer) {
        var mlFeatureSet = new MLFeatureSet();
        var opponentPlayer = (currentPlayer == AtaxxAIEngine.PlayerColor.Red) ? AtaxxAIEngine.PlayerColor.Blue : AtaxxAIEngine.PlayerColor.Red;

        // --- Board State and Piece Count Features ---

        // First, get the piece counts instantly using PopCount.
        int myPieces = helperEngine.PopCount((currentPlayer == AtaxxAIEngine.PlayerColor.Red) ? board.RedPieces : board.BluePieces);
        int opponentPieces = helperEngine.PopCount((currentPlayer == AtaxxAIEngine.PlayerColor.Red) ? board.BluePieces : board.RedPieces);

        // Create the feature vector for the board state by iterating through each square's bit.        
        ulong playerPiecesBitboard = (currentPlayer == AtaxxAIEngine.PlayerColor.Red) ? board.RedPieces : board.BluePieces;
        ulong opponentPiecesBitboard = (currentPlayer == AtaxxAIEngine.PlayerColor.Red) ? board.BluePieces : board.RedPieces;

        for (int i = 0; i < AttaxConstants.BaseConst.BoardSize * AttaxConstants.BaseConst.BoardSize; i++) {
            ulong bit = 1UL << i;
            mlFeatureSet.BoardState.Add((playerPiecesBitboard & bit) != 0 ? 1.0f : 0.0f);
            mlFeatureSet.BoardState.Add((opponentPiecesBitboard & bit) != 0 ? 1.0f : 0.0f);
            mlFeatureSet.BoardState.Add((board.BlockedSquares & bit) != 0 ? 1.0f : 0.0f);
        }

        // Calculate normalized feature values.
        float totalSquares = AttaxConstants.BaseConst.BoardSize * AttaxConstants.BaseConst.BoardSize;
        mlFeatureSet.PieceDifferential = (myPieces - opponentPieces) / totalSquares;
        mlFeatureSet.TotalPieceCount = (myPieces + opponentPieces) / totalSquares;
        
        // Load the current board state into the helper engine to use its evaluation methods.        
        helperEngine.Board = board;
        
        mlFeatureSet.MyMobility = helperEngine.GetAllValidMoves(board, currentPlayer).Count;
        mlFeatureSet.OpponentMobility = helperEngine.GetAllValidMoves(board, opponentPlayer).Count;
        mlFeatureSet.MobilityDifferential = mlFeatureSet.MyMobility - mlFeatureSet.OpponentMobility;

        mlFeatureSet.MyStability = helperEngine.GetStabilityBonus(board, currentPlayer);
        mlFeatureSet.OpponentStability = helperEngine.GetStabilityBonus(board, opponentPlayer);

        mlFeatureSet.MyCenterControl = helperEngine.GetCenterControl(board, currentPlayer);
        mlFeatureSet.OpponentCenterControl = helperEngine.GetCenterControl(board, opponentPlayer);
        
        var myPositionalScores = helperEngine.GetCornerAndEdgeScores(board,currentPlayer);
        mlFeatureSet.MyCornerScore = myPositionalScores.cornerScore;
        mlFeatureSet.MyEdgeScore = myPositionalScores.edgeScore;

        return mlFeatureSet;
    }
}
