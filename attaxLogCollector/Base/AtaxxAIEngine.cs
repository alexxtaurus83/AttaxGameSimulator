using Assets.Scripts.Base.Ataxx;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ZLinq; 
using static AILogCoordinator; 

public class AtaxxAIEngine {

    #region Configurable AI Features    
    public bool UseMLEvaluation { get; set; }
    public int? seed = null; //optional seed parameter
    #endregion

    #region Configurables
    public AtaxxLogger ataxxLogger;
    public AtaxxThreadHelper ataxxHelper { get; set; }
    public bool useOrthogonalOnlyCapture { get; set; }
    public int aiDepth { get; set; }
    public AILogCoordinator logCoordinator { get; set; }    
    private ConcurrentDictionary<ulong, TTEntry> transpositionTable = new();

    private ulong[,,] zobristTable = new ulong[AttaxConstants.BaseConst.BoardSize, AttaxConstants.BaseConst.BoardSize, 3];

    private ulong zobristSideToMove; 
    private ulong[] SingleStepMoves;
    private ulong[] TwoStepMoves;
    private ulong CornerMask;
    private ulong EdgeMask;
    private ulong[] OrthogonalStepMoves;
    private int[] CenterControlWeights;
    #endregion

    #region enums
    
    public struct TTEntry {
        public int depth;
        public int score;
        public NodeType nodeType;
        public int age;        
    }
    
    private int currentSearchAge = 0;

    public struct MoveResult {
        // --- For the UI/GameManager (Visuals) ---
        public Move MoveMade;
        public bool WasClone;
        public List<(int x, int y)> FlippedPieces;
        public UndoMoveInfo UndoInfo;
    }
    public struct UndoMoveInfo {     
        public ulong PreviousZobristHash;        
        public ulong FlippedPiecesMask;
    }
    public struct Move {
        public int FromX, FromY, ToX, ToY;
        public Move(int fx, int fy, int tx, int ty) {
            FromX = fx; FromY = fy; ToX = tx; ToY = ty;
        }

        public override bool Equals(object obj) {
            return obj is Move other &&
                   FromX == other.FromX &&
                   FromY == other.FromY &&
                   ToX == other.ToX &&
                   ToY == other.ToY;
        }

        public override int GetHashCode() {
            return HashCode.Combine(FromX, FromY, ToX, ToY);
        }

        public static bool operator ==(Move left, Move right) {
            return left.Equals(right);
        }

        public static bool operator !=(Move left, Move right) {
            return !(left == right);
        }
    }
    public enum PlayerColor { None, Red, Blue, Blocked }   
    public enum NodeType { Exact, LowerBound, UpperBound }

    #endregion

    #region publics    

    public PlayerColor AIPlayerColor;
    public BitboardState Board { get; set; }    
    #endregion
    
    #region constructor
    public AtaxxAIEngine(
        AtaxxLogger ataxxLogger = null,
        AILogCoordinator coordinator = null,
        bool useMLEvaluation = false,        
        bool useOrthogonalOnlyCapture = false,
        int aiDepth = 3
    ) {
        this.ataxxLogger = ataxxLogger;
        this.useOrthogonalOnlyCapture = useOrthogonalOnlyCapture;
        this.aiDepth = aiDepth;
        this.logCoordinator = coordinator;
        this.UseMLEvaluation = useMLEvaluation;        
        this.Board = new BitboardState();
        InitZobrist(seed);
        SetupBoard(SwitchPlayer(AIPlayerColor));            
        
    }
    #endregion
    #region board helpers
    public int GetBitIndex(int x, int y) {
        return y * AttaxConstants.BaseConst.BoardSize + x;
    }

    /// <summary>
    /// Gets the color of the piece or state of a single square from the internal bitboard representation.
    /// AtaxxGameManager can use this to query the board.
    /// </summary>
    /// <param name="x">The x-coordinate (column).</param>
    /// <param name="y">The y-coordinate (row).</param>
    /// <returns>The PlayerColor at the specified coordinates.</returns>
    public PlayerColor GetColorAt(int x, int y) {
        int bitIndex = GetBitIndex(x, y);
        ulong bit = 1UL << bitIndex;
        // Check the bitboards in order
        if ((Board.RedPieces & bit) != 0) return PlayerColor.Red;
        if ((Board.BluePieces & bit) != 0) return PlayerColor.Blue;
        if ((Board.BlockedSquares & bit) != 0) return PlayerColor.Blocked;
        return PlayerColor.None;
    }

    /// <summary>
    /// Sets the state of a single square on the internal bitboards.
    /// AtaxxGameManager can use this for initial board setup.
    /// </summary>
    /// <param name="x">The x-coordinate (column).</param>
    /// <param name="y">The y-coordinate (row).</param>
    /// <param name="color">The color to set the square to.</param>
    public void SetColorAt(int x, int y, PlayerColor color) {
        // First, find out what color is currently at the position.
        PlayerColor currentColor = GetColorAt(x, y);

        // If a piece is already here, XOR its value OUT of the hash to remove it.
        if (currentColor != PlayerColor.None) {
            Board.ZobristHash ^= zobristTable[x, y, GetPieceTypeIndex(currentColor)];
        }

        // If we are placing a new piece (not making it empty),
        // XOR its value IN to the hash to add it.
        if (color != PlayerColor.None) {
            Board.ZobristHash ^= zobristTable[x, y, GetPieceTypeIndex(color)];
        }

        // Logic to update the bitboards
        int bitIndex = GetBitIndex(x, y);
        ulong bit = 1UL << bitIndex;

        // First, clear the bit from all bitboards to ensure a clean state
        Board.RedPieces &= ~bit;
        Board.BluePieces &= ~bit;
        Board.BlockedSquares &= ~bit;

        // Now, set the bit on the correct bitboard
        switch (color) {
            case PlayerColor.Red:
                Board.RedPieces |= bit;
                break;
            case PlayerColor.Blue:
                Board.BluePieces |= bit;
                break;
            case PlayerColor.Blocked:
                Board.BlockedSquares |= bit;
                break;
                // If PlayerColor.None, we do nothing after clearing it.
        }
    }
    private void SetupBoard(PlayerColor startPlayer) {
        int boardSize = AttaxConstants.BaseConst.BoardSize;
        int totalSquares = boardSize * boardSize;
        SingleStepMoves = new ulong[totalSquares];
        TwoStepMoves = new ulong[totalSquares];

        for (int y = 0; y < boardSize; y++) {
            for (int x = 0; x < boardSize; x++) {
                int fromIndex = y * boardSize + x;
                ulong singleStepMask = 0UL;
                ulong twoStepMask = 0UL;

                // Check all squares in a 5x5 box around the current square
                for (int dy = -2; dy <= 2; dy++) {
                    for (int dx = -2; dx <= 2; dx++) {
                        if (dx == 0 && dy == 0) continue;

                        int toX = x + dx;
                        int toY = y + dy;

                        if (toX >= 0 && toX < boardSize && toY >= 0 && toY < boardSize) {
                            int toIndex = toY * boardSize + toX;
                            // The distance is the larger of the x or y change
                            int distance = Math.Max(Math.Abs(dx), Math.Abs(dy));

                            if (distance == 1) // Clone move distance
                            {
                                singleStepMask |= (1UL << toIndex);
                            } else if (distance == 2) // Jump move distance
                              {
                                twoStepMask |= (1UL << toIndex);
                            }
                        }
                    }
                }
                SingleStepMoves[fromIndex] = singleStepMask;
                TwoStepMoves[fromIndex] = twoStepMask;
            }
        }
        
        ulong cornerMask = 0UL;
        ulong edgeMask = 0UL;

        // Define Corners
        cornerMask |= (1UL << GetBitIndex(0, 0));
        cornerMask |= (1UL << GetBitIndex(boardSize - 1, 0));
        cornerMask |= (1UL << GetBitIndex(0, boardSize - 1));
        cornerMask |= (1UL << GetBitIndex(boardSize - 1, boardSize - 1));
        CornerMask = cornerMask;

        // Define Edges (excluding corners)
        for (int i = 0; i < boardSize; i++) {
            edgeMask |= (1UL << GetBitIndex(i, 0));             // Top row
            edgeMask |= (1UL << GetBitIndex(i, boardSize - 1)); // Bottom row
            edgeMask |= (1UL << GetBitIndex(0, i));             // Left column
            edgeMask |= (1UL << GetBitIndex(boardSize - 1, i)); // Right column
        }
        EdgeMask = edgeMask & ~CornerMask; // Exclude corners from the edge mask       
        
        OrthogonalStepMoves = new ulong[totalSquares];
        for (int i = 0; i < totalSquares; i++) {
            ulong mask = 0UL;
            int x = i % boardSize;
            int y = i / boardSize;

            // Check the 4 orthogonal neighbors
            if (x > 0) mask |= (1UL << (i - 1));             // Left
            if (x < boardSize - 1) mask |= (1UL << (i + 1)); // Right
            if (y > 0) mask |= (1UL << (i - boardSize));     // Up
            if (y < boardSize - 1) mask |= (1UL << (i + boardSize)); // Down

            OrthogonalStepMoves[i] = mask;
        }        
                
        CenterControlWeights = new int[totalSquares];

        int centerAreaStart = boardSize / 3; // For 7x7, this is 2. Loop from 2 to 4.
        int exactCenter = boardSize / 2;     // For 7x7, this is 3.

        for (int y = centerAreaStart; y < boardSize - centerAreaStart; y++) {
            for (int x = centerAreaStart; x < boardSize - centerAreaStart; x++) {                
                int dx = Math.Abs(x - exactCenter);
                int dy = Math.Abs(y - exactCenter);
                int weight = (exactCenter - dx) + (exactCenter - dy);

                int index = GetBitIndex(x, y);
                CenterControlWeights[index] = weight;
            }
        }

        this.Board.ZobristHash = ComputeZobristHash(this.Board, startPlayer);
    }
    #endregion

    #region board operations    
    /// <summary>
    /// Generates all valid moves for a given player from a bitboard state.
    /// This is the high-performance version that will be used by the AI search.
    /// </summary>
    /// <param name="boardState">The current bitboard state of the game.</param>
    /// <param name="player">The player to generate moves for.</param>
    /// <returns>A list of all valid moves.</returns>
    public List<Move> GetAllValidMoves(BitboardState boardState, PlayerColor player) {
        var moves = new List<Move>();
        ulong playerPieces = (player == PlayerColor.Red) ? boardState.RedPieces : boardState.BluePieces;
        ulong emptySquares = boardState.EmptySquares();

        // Loop through each of the player's pieces using a fast bit-twiddling technique
        ulong remainingPieces = playerPieces;
        while (remainingPieces > 0) {
            // Get the index of the current piece using our new helper method
            int fromIndex = TrailingZeroCount(remainingPieces);
            int fromX = fromIndex % AttaxConstants.BaseConst.BoardSize;
            int fromY = fromIndex / AttaxConstants.BaseConst.BoardSize;

            // Generate Clone Moves
            ulong validCloneMoves = SingleStepMoves[fromIndex] & emptySquares;

            ulong remainingClones = validCloneMoves;
            while (remainingClones > 0) {
                int toIndex = TrailingZeroCount(remainingClones); 
                moves.Add(new Move(fromX, fromY, toIndex % AttaxConstants.BaseConst.BoardSize, toIndex / AttaxConstants.BaseConst.BoardSize));
                remainingClones &= remainingClones - 1; 
            }

            // Generate Jump Moves
            ulong validJumpMoves = TwoStepMoves[fromIndex] & emptySquares;

            ulong remainingJumps = validJumpMoves;
            while (remainingJumps > 0) {
                int toIndex = TrailingZeroCount(remainingJumps); 
                moves.Add(new Move(fromX, fromY, toIndex % AttaxConstants.BaseConst.BoardSize, toIndex / AttaxConstants.BaseConst.BoardSize));
                remainingJumps &= remainingJumps - 1; 
            }

            // Clear the piece we just processed and move to the next one
            remainingPieces &= remainingPieces - 1;
        }

        return moves;
    }
    /// <summary>
    /// Calculates corner and edge scores for a given player's piece bitboard.
    /// This is now the core internal logic.
    /// </summary>
    private (int corner, int edge) EvaluateCornerEdge(ulong playerPieces) {
        // Count pieces on corners by intersecting the player's pieces with the corner mask
        int cornerCount = PopCount(playerPieces & CornerMask);
        int cornerScore = cornerCount * AttaxConstants.EvaluateCornerEdgeConst.cornerWeight;

        // Count pieces on edges by intersecting with the edge mask
        int edgeCount = PopCount(playerPieces & EdgeMask);
        int edgeScore = edgeCount * AttaxConstants.EvaluateCornerEdgeConst.edgeWeight;

        return (cornerScore, edgeScore);
    }
    /// <summary>
    /// Gets the corner and edge scores for a given player from the engine's current board state.
    /// </summary>
    public (int cornerScore, int edgeScore) GetCornerAndEdgeScores(BitboardState boardState, PlayerColor player) {        
        ulong playerPieces = (player == PlayerColor.Red) ? boardState.RedPieces : boardState.BluePieces;
        return EvaluateCornerEdge(playerPieces);
    }

    /// <summary>
    /// Calculates the difference in corner/edge scores between two players for a given board state.
    /// </summary>
    private int GetCornerEdgeBonusEvaluation(BitboardState boardState, PlayerColor player, PlayerColor enemy) {        
        ulong playerPieces = (player == PlayerColor.Red) ? boardState.RedPieces : boardState.BluePieces;
        ulong enemyPieces = (enemy == PlayerColor.Red) ? boardState.RedPieces : boardState.BluePieces;

        var (playerCornerScore, playerEdgeScore) = EvaluateCornerEdge(playerPieces);
        var (enemyCornerScore, enemyEdgeScore) = EvaluateCornerEdge(enemyPieces);

        return playerCornerScore + playerEdgeScore - (enemyCornerScore + enemyEdgeScore);
    }
    /// <summary>
    /// Generates a string representation of the board by querying the bitboard state.
    /// </summary>
    /// <returns>A string representing the board state, e.g., for logging.</returns>
    public string GetBoardStateString() {        
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        for (int x = 0; x < AttaxConstants.BaseConst.BoardSize; x++) {
            for (int y = 0; y < AttaxConstants.BaseConst.BoardSize; y++) {                
                PlayerColor color = GetColorAt(x, y);
                sb.Append((int)color);
            }
        }
        return sb.ToString();
    }
    /// <summary>
    /// Gets the piece counts for a player and their opponent from a given bitboard state.
    /// </summary>
    /// <param name="boardState">The bitboard state to count pieces from.</param>
    /// <param name="p1Color">The player from whose perspective to count.</param>
    /// <returns>A tuple containing the piece count for p1 and their opponent (p2).</returns>
    public (int p1Count, int p2Count) GetRedAndBlueCounts(BitboardState boardState, PlayerColor p1Color) {
        int p1Count;
        int p2Count;
        
        if (p1Color == PlayerColor.Red) {
            p1Count = PopCount(boardState.RedPieces);
            p2Count = PopCount(boardState.BluePieces);
        } else // p1Color is Blue
          {
            p1Count = PopCount(boardState.BluePieces);
            p2Count = PopCount(boardState.RedPieces);
        }

        return (p1Count, p2Count);
    }
    /// <summary>
    /// Checks for game-over conditions using fast bitboard operations.
    /// </summary>
    /// <param name="boardState">The current bitboard state of the game.</param>
    /// <returns>True if the game is over, otherwise false.</returns>
    public bool IsGameOver(BitboardState boardState) {
        // Check for win/loss/draw by piece count or full board.
        // This is extremely fast using PopCount on the bitboards.
        int redCount = PopCount(boardState.RedPieces);
        int blueCount = PopCount(boardState.BluePieces);
        int emptyCount = PopCount(boardState.EmptySquares());

        if (redCount == 0 || blueCount == 0 || emptyCount == 0) {
            return true;
        }

        // Check for stalemate (neither player has a valid move).
        // This now uses our high-performance, bitboard-native GetAllValidMoves method.
        if (GetAllValidMoves(boardState, PlayerColor.Red).Count == 0 &&
            GetAllValidMoves(boardState, PlayerColor.Blue).Count == 0) {
            return true;
        }

        return false;
    }
    /// <summary>
    /// Counts how many enemy pieces are adjacent to a destination square, using bitboards.
    /// </summary>
    /// <param name="boardState">The current bitboard state of the game.</param>
    /// <param name="move">The move being made.</param>
    /// <param name="player">The player making the move.</param>
    /// <returns>The number of adjacent enemy pieces that will be flipped.</returns>
    public int CountFlippedEnemies(BitboardState boardState, Move move, PlayerColor player) {
        // Determine which bitboard holds the opponent's pieces.
        ulong opponentPieces = (player == PlayerColor.Red)
            ? boardState.BluePieces
            : boardState.RedPieces;

        // Get the index of the destination square.
        int toIndex = GetBitIndex(move.ToX, move.ToY);

        // Select the correct attack mask based on the capture rule.
        // We reuse SingleStepMoves for standard 8-directional captures.
        ulong attackMask = useOrthogonalOnlyCapture
            ? OrthogonalStepMoves[toIndex]
            : SingleStepMoves[toIndex];

        // Find the opponent's pieces that are in the attack mask area.
        ulong flippedPieces = opponentPieces & attackMask;

        // Return the count of those pieces using the fast PopCount helper.
        return PopCount(flippedPieces);
    }



    #endregion


    #region inital chips setup
    public Dictionary<PlayerColor, List<(int x, int y)>> GetInitialChipPositions(bool randomize, int? seed) {
        var positions = new Dictionary<PlayerColor, List<(int x, int y)>> {
            [AtaxxAIEngine.PlayerColor.Red] = new List<(int x, int y)>(),
            [AtaxxAIEngine.PlayerColor.Blue] = new List<(int x, int y)>()
        };

        int size = AttaxConstants.BaseConst.BoardSize;

        if (!randomize) {
            // Default corner positions inherently satisfy the distance rule on a 7x7 board.
            positions[AtaxxAIEngine.PlayerColor.Red].Add((0, 0));
            positions[AtaxxAIEngine.PlayerColor.Red].Add((size - 1, size - 1));
            positions[AtaxxAIEngine.PlayerColor.Blue].Add((0, size - 1));
            positions[AtaxxAIEngine.PlayerColor.Blue].Add((size - 1, 0));
            return positions;
        }

        // --- Randomized positions with distance rule ---
        var rng = seed.HasValue ? new Random(seed.Value) : new Random();
        var allPlacedChips = new List<(int x, int y)>();
        var allSquares = new List<(int x, int y)>();
        for (int x = 0; x < size; x++) {
            for (int y = 0; y < size; y++) {
                allSquares.Add((x, y));
            }
        }

        // Place 2 Red pieces
        for (int i = 0; i < 2; i++) {
            var validSquares = allSquares.AsValueEnumerable().Except(allPlacedChips).ToList();
            if (!validSquares.AsValueEnumerable().Any()) throw new InvalidOperationException("Could not find a valid spot for a Red piece.");
            var pos = validSquares[rng.Next(validSquares.Count)];
            positions[AtaxxAIEngine.PlayerColor.Red].Add(pos);
            allPlacedChips.Add(pos);
        }

        // Place 2 Blue pieces, ensuring they are distant from the Red pieces
        for (int i = 0; i < 2; i++) {
            // Find all squares that are not yet occupied AND are far enough from all enemy (Red) pieces.
            var validSquares = allSquares
                .AsValueEnumerable().Except(allPlacedChips)
                .Where(sq => positions[AtaxxAIEngine.PlayerColor.Red].AsValueEnumerable().All(redPos => IsSufficientlyDistant(sq, redPos, 3)))
                .ToList();

            if (!validSquares.AsValueEnumerable().Any()) {
                // This is a rare fallback case if no valid spot can be found (e.g., on a very small board).
                // It will just place the piece on any remaining empty square.
                validSquares = allSquares.AsValueEnumerable().Except(allPlacedChips).ToList();
                if (!validSquares.AsValueEnumerable().Any()) throw new InvalidOperationException("Board is full, cannot place Blue piece.");
            }

            var pos = validSquares[rng.Next(validSquares.Count)];
            positions[AtaxxAIEngine.PlayerColor.Blue].Add(pos);
            allPlacedChips.Add(pos);
        }

        return positions;
    }
    private bool IsSufficientlyDistant((int x, int y) p1, (int x, int y) p2, int minDistance) {        
        int distance = Math.Max(Math.Abs(p1.x - p2.x), Math.Abs(p1.y - p2.y));
        return distance >= minDistance;
    }
    /// <summary>
    /// Generates a list of random positions for blocked cells on a given bitboard state.
    /// </summary>
    /// <param name="blocksToPlace">The number of blocks to generate.</param>
    /// <param name="currentBoardState">The current bitboard state to find empty squares on.</param>
    /// <param name="seed">An optional seed for the random number generator.</param>
    /// <returns>A list of (x,y) coordinates for the new blocked cells.</returns>
    public List<(int x, int y)> GenerateRandomBlockedCellPositions(int blocksToPlace, BitboardState currentBoardState, int? seed = null) {
        if (blocksToPlace <= 0) {
            return new List<(int, int)>();
        }

        var rng = seed.HasValue ? new Random(seed.Value) : new Random();
        var blockedPositions = new List<(int, int)>();
        int maxAttempts = 1000;
        int attempts = 0;

        // Get a bitboard of all currently empty squares.
        ulong emptySquares = currentBoardState.EmptySquares();

        // The zoning logic for spreading out blocks remains the same.
        int zoneCount = (int)Math.Ceiling(Math.Sqrt(blocksToPlace));
        int zoneSize = AttaxConstants.BaseConst.BoardSize / zoneCount;

        var zones = new List<(int zx, int zy)>();
        for (int zx = 0; zx < zoneCount; zx++) {
            for (int zy = 0; zy < zoneCount; zy++) {
                zones.Add((zx, zy));
            }
        }
        zones = zones.AsValueEnumerable().OrderBy(_ => rng.Next()).ToList();

        while (blockedPositions.Count < blocksToPlace && attempts < maxAttempts) {
            foreach (var zone in zones) {
                if (blockedPositions.Count >= blocksToPlace) break;

                int minX = zone.zx * zoneSize;
                int maxX = Math.Min((zone.zx + 1) * zoneSize, AttaxConstants.BaseConst.BoardSize);
                int minY = zone.zy * zoneSize;
                int maxY = Math.Min((zone.zy + 1) * zoneSize, AttaxConstants.BaseConst.BoardSize);

                // Find a random position within this zone.
                int x = rng.Next(minX, maxX);
                int y = rng.Next(minY, maxY);
                var pos = (x, y);                
                
                ulong posBit = 1UL << GetBitIndex(x, y);
                if ((emptySquares & posBit) != 0 && !blockedPositions.Contains(pos)) {
                    blockedPositions.Add(pos);
                }
            }
            attempts++;
        }

        return blockedPositions;
    }
    #endregion

    #region core 
    public Move GetBestMove(PlayerColor player, bool isHumanSimulation = false) {
        try {            
            this.currentSearchAge++; // Increment age for this new search
            var debug = false;
            var clock = System.Diagnostics.Stopwatch.StartNew();

            if (debug) {
                string initialBoardStateForGetAllValidMoves = GetBoardStateString();
                var (initialRed, initialBlue) = GetRedAndBlueCounts(Board,player);                
                ataxxLogger.WriteTolog($"GetBestMove START for {player}: Board String: {initialBoardStateForGetAllValidMoves}, Counts: R{initialRed}B{initialBlue}; LogCount: {logCoordinator.GlobalLog.Count}");
            }

            var allMoves = GetAllValidMoves(Board, player);
            if (allMoves.Count == 0) return default;

            int dynamicDepth = aiDepth;
            var (redP, blueP) = GetRedAndBlueCounts(Board,player);
            int totalPieces = redP + blueP;           
            if (allMoves.Count > 40) {
                // If the position is very complex (many moves), reduce depth to avoid long waits.
                dynamicDepth = aiDepth ; //-1
            } else if (allMoves.Count < AttaxConstants.ChipsCountConst.earlyGame) {
                // If the position is simple (few moves), we can afford to search deeper.
                dynamicDepth = aiDepth + 2;
            }
            // Ensure depth doesn't go below a minimum reasonable level.
            if (dynamicDepth < 2) dynamicDepth = 2;
            bool shouldUseQuiescence = dynamicDepth >= 3;

            // Use thread-safe collections for storing results from parallel tasks
            var scored = new ConcurrentBag<(Move move, int score)>();
            var evaluationDetails = new ConcurrentDictionary<Move, int>();
            var aiMoveCandidatesDict = new ConcurrentDictionary<int, AIMoveCandidates>();
            int logIdCounter = -1; // Used with Interlocked.Increment for thread-safe unique IDs
            

            // ### PARALLEL EXECUTION BLOCK ###
            Parallel.ForEach(allMoves, move => {
                //foreach (var move in allMoves) { 
                var ataxxThreadHelper = new AtaxxThreadHelper(AttaxConstants.BaseConst.BoardSize, AttaxConstants.BaseConst.KillerMoveMaxDepth);
                ataxxThreadHelper.boardForThread = Board.Clone();                
                // Calculate root heuristics using the original board state                
                bool isClone = IsCloneMove(move);
                int flipped = CountFlippedEnemies(Board, move, player);
                int positionalBonus = 0;
                if (totalPieces <= AttaxConstants.ChipsCountConst.lateGame) {
                    positionalBonus = GetPositionalValue(move.ToX, move.ToY) * AttaxConstants.GetBestMoveConst.centerWeightMultiplier;
                }

                MakeMove(ataxxThreadHelper.boardForThread, move, player);                
                int opponentFlipRisk = CalculateMaxOpponentFlips(ataxxThreadHelper.boardForThread, player);
                
                int evalScoreFromAlphaBeta = AlphaBeta(ataxxThreadHelper, SwitchPlayer(player), dynamicDepth - 1, int.MinValue, int.MaxValue, 1, true, shouldUseQuiescence);
                int strategicScore = -evalScoreFromAlphaBeta;


                // Calculate the final score for the move
                int finalScore = strategicScore * 100;
                if (isClone) { finalScore += AttaxConstants.GetDynamicCloneBonus(totalPieces); }
                finalScore += flipped * AttaxConstants.GetBestMoveConst.flipWeight;
                finalScore -= opponentFlipRisk * AttaxConstants.GetBestMoveConst.riskAversionFactor;
                finalScore += positionalBonus;

                // Check for an immediate win on the thread's local board state
                var (currentRed, currentBlue) = GetRedAndBlueCounts(ataxxThreadHelper.boardForThread,player);
                bool winsNow = (player == AtaxxAIEngine.PlayerColor.Red && currentBlue == 0) || (player == AtaxxAIEngine.PlayerColor.Blue && currentRed == 0);
                if (winsNow) finalScore = int.MaxValue - 1000; // Adjusted to prevent overflow

                // Store logging details in thread-safe collections
                int logId = System.Threading.Interlocked.Increment(ref logIdCounter);
                aiMoveCandidatesDict.TryAdd(logId, new AIMoveCandidates {
                    move = move,
                    isClone = isClone,
                    flipsCount = flipped,
                    opponentFlipRiskCount = opponentFlipRisk,
                    evalScore = strategicScore,
                    positionalBonusScore = positionalBonus,
                    cloneBonusScore = isClone ? AttaxConstants.GetBestMoveConst.earlyGameCloneBonus : 0,
                    flippedScore = flipped * AttaxConstants.GetBestMoveConst.flipWeight,
                    opponentFlipRiskScore = opponentFlipRisk * AttaxConstants.GetBestMoveConst.riskAversionFactor,
                    finalScore = finalScore
                });
                evaluationDetails.TryAdd(move, logId);

                // Add the final scored move to the thread-safe result collection
                scored.Add((move, finalScore));
            }
            );
            // ### END OF PARALLEL EXECUTION BLOCK ###

            if (scored.IsEmpty) return default;

            // On the main thread, convert the results to a list for sorting
            var scoredList = scored.ToList();
            scoredList.Sort((a, b) => b.score.CompareTo(a.score));

            var bestMove = scoredList[0].move;
            clock.Stop();
            
            if (logCoordinator != null) {
                // Log board state AFTER loop, before final logging
                if (debug) {
                    string boardStateBeforeLogging = GetBoardStateString();
                    var (finalRedPreLog, finalBluePreLog) = GetRedAndBlueCounts(Board,player);
                    ataxxLogger.WriteTolog($"GetBestMove END for {player}: Board String FOR LOGGING: {boardStateBeforeLogging}, Counts FOR LOGGING: R{finalRedPreLog}B{finalBluePreLog}, Best Move: ({bestMove.FromX},{bestMove.FromY})->({bestMove.ToX},{bestMove.ToY})");
                    var (numericRedForLog, numericBlueForLog) = GetRedAndBlueCounts(Board,player);
                    string boardStringForLog = GetBoardStateString();
                    ataxxLogger.WriteTolog($"FINAL LOG PREP for {player}: BestMove: ({bestMove.FromX},{bestMove.FromY})->({bestMove.ToX},{bestMove.ToY})\nNumericCounts: R{numericRedForLog}B{numericBlueForLog}\nBoardString: {boardStringForLog}");
                }

                var finalAiMoveCandidatesDict = new Dictionary<int, AIMoveCandidates>(aiMoveCandidatesDict);
                AIMoveDetails aIMoveDetails = new AIMoveDetails(evaluationDetails[bestMove], (int)clock.ElapsedMilliseconds, dynamicDepth, finalAiMoveCandidatesDict);

                var (redCount, blueCount) = GetRedAndBlueCounts(Board,player); // Counts on original board state
                MoveDetails moveDetails = new MoveDetails(bestMove, player, finalAiMoveCandidatesDict[evaluationDetails[bestMove]].isClone, finalAiMoveCandidatesDict[evaluationDetails[bestMove]].flipsCount, GetBoardStateString(), false, aIMoveDetails);
                moveDetails.redCount = redCount;
                moveDetails.blueCount = blueCount;

                if (isHumanSimulation) {
                    TurnLog turnLog = new TurnLog(useOrthogonalOnlyCapture);
                    turnLog.playerMove = moveDetails;
                    logCoordinator.GlobalLog.Add(logCoordinator.TurnIndex, turnLog);
                } else {
                    logCoordinator.GlobalLog[logCoordinator.TurnIndex].opponentMove = moveDetails;
                    logCoordinator.TurnIndex++;
                }
            }
            return bestMove;
        } catch (Exception ex) {
            ataxxLogger.WriteTolog(ex.ToString());
            return default;
        }
    }


    // This function uses a "negamax" approach, which is concise and robust way to implement minimax with alpha-beta pruning.
    // It always evaluates the score from the perspective of the current 'player'.
    private int AlphaBeta(AtaxxThreadHelper ataxxThreadHelper, PlayerColor player, int depth, int alpha, int beta, int ply, bool allowNullMove, bool shouldUseQuiescence) {
        int originalAlpha = alpha;
        ulong hash = ataxxThreadHelper.boardForThread.ZobristHash;
        
        if (transpositionTable.TryGetValue(hash, out TTEntry entry) && entry.depth >= depth) {
            if (entry.nodeType == NodeType.Exact) {
                return entry.score;
            }
            if (entry.nodeType == NodeType.LowerBound && entry.score >= beta) {
                return entry.score;
            }
            if (entry.nodeType == NodeType.UpperBound && entry.score <= alpha) {
                return entry.score;
            }
        }

        if (IsGameOver(ataxxThreadHelper.boardForThread)) {
            return Evaluate(ataxxThreadHelper.boardForThread, player);
        }

        if (depth <= 0) {
            return Quiescence(ataxxThreadHelper.boardForThread, player, alpha, beta, 2);
        }

        if (allowNullMove && depth >= 3 && GetAllValidMoves(ataxxThreadHelper.boardForThread, player).Count >= 1) {
            int staticEval = Evaluate(ataxxThreadHelper.boardForThread, player);
            if (staticEval >= beta) {
                ataxxThreadHelper.boardForThread.ZobristHash ^= zobristSideToMove;
                int nullMoveReduction = 2;
                int score = -AlphaBeta(ataxxThreadHelper, SwitchPlayer(player), depth - 1 - nullMoveReduction, -beta, -beta + 1, ply + 1, false, shouldUseQuiescence);
                ataxxThreadHelper.boardForThread.ZobristHash ^= zobristSideToMove;
                if (score >= beta) {
                    return beta;
                }
            }
        }

        var moves = GetOrderedMoves(ataxxThreadHelper.boardForThread, player, ply, ataxxThreadHelper.killerMovesForThread);
        if (moves.Count == 0) return Evaluate(ataxxThreadHelper.boardForThread, player);

        int bestScore = int.MinValue;

        for (int i = 0; i < moves.Count; i++) {
            var move = moves[i];
            var moveInfo = MakeMove(ataxxThreadHelper.boardForThread, move, player);

            int score;
            if (i == 0) {
                score = -AlphaBeta(ataxxThreadHelper, SwitchPlayer(player), depth - 1, -beta, -alpha, ply + 1, true, shouldUseQuiescence);
            } else {
                score = -AlphaBeta(ataxxThreadHelper, SwitchPlayer(player), depth - 1, -alpha - 1, -alpha, ply + 1, true, shouldUseQuiescence);
                if (score > alpha && score < beta) {
                    score = -AlphaBeta(ataxxThreadHelper, SwitchPlayer(player), depth - 1, -beta, -alpha, ply + 1, true, shouldUseQuiescence);
                }
            }
            UnmakeMove(ataxxThreadHelper.boardForThread, move, player, moveInfo.UndoInfo);

            if (score > bestScore) bestScore = score;
            if (score > alpha) alpha = score;

            if (alpha >= beta) {
                bool isCapture = moveInfo.UndoInfo.FlippedPiecesMask != 0;
                if (!isCapture && ply < AttaxConstants.BaseConst.KillerMoveMaxDepth) {
                    ataxxThreadHelper.killerMovesForThread[ply, 1] = ataxxThreadHelper.killerMovesForThread[ply, 0];
                    ataxxThreadHelper.killerMovesForThread[ply, 0] = move;
                }
                break;
            }
        }

        NodeType nodeTypeToStore;
        if (bestScore <= originalAlpha) {
            nodeTypeToStore = NodeType.UpperBound;
        } else if (bestScore >= beta) {
            nodeTypeToStore = NodeType.LowerBound;
        } else {
            nodeTypeToStore = NodeType.Exact;
        }

        // Create the new entry to be stored
        var newEntry = new TTEntry {
            score = bestScore,
            depth = depth,
            nodeType = nodeTypeToStore,
            age = this.currentSearchAge
        };


        // Only overwrite an existing entry if the new one is from a deeper search
        // or if the entry is from a previous search cycle (old age).
        if (!transpositionTable.TryGetValue(hash, out TTEntry oldEntry) || depth >= oldEntry.depth || this.currentSearchAge != oldEntry.age) {
            transpositionTable[hash] = newEntry;
        }

        return bestScore;
    }

    /// <summary>
    /// Applies a move to the given bitboard state, updating player pieces
    /// and the Zobrist hash incrementally.
    /// </summary>
    /// <param name="boardState">The bitboard state to modify.</param>
    /// <param name="move">The move to apply.</param>
    /// <param name="player">The player making the move.</param>
    public MoveResult MakeMove(BitboardState boardState, Move move, PlayerColor player) {
        try {
            var moveResult = new MoveResult {
                MoveMade = move,
                FlippedPieces = new List<(int, int)>(),
            };
            var undoInfo = new UndoMoveInfo { PreviousZobristHash = boardState.ZobristHash };

            // Get bitmasks for the 'from' and 'to' squares of the move.
            int fromIndex = GetBitIndex(move.FromX, move.FromY);
            int toIndex = GetBitIndex(move.ToX, move.ToY);
            ulong fromMask = 1UL << fromIndex;
            ulong toMask = 1UL << toIndex;

            if ((boardState.BlockedSquares & toMask) != 0) throw new InvalidOperationException("Moving to blocked square");

            // Identify which bitboards we are working with.
            // Using 'ref' locals allows us to modify the bitboards in boardState directly.
            ref ulong playerPieces = ref (player == PlayerColor.Red ? ref boardState.RedPieces : ref boardState.BluePieces);
            ref ulong opponentPieces = ref (player == PlayerColor.Red ? ref boardState.BluePieces : ref boardState.RedPieces);

            // Update Zobrist hash for the player who is about to move.
            // We always flip the side-to-move key.
            boardState.ZobristHash ^= zobristSideToMove;

            // Handle the move itself (clone vs. jump).
            bool isClone = IsCloneMove(move);
            if (!isClone) // It's a jump
            {
                playerPieces &= ~fromMask; // Remove piece from the 'from' square.
                boardState.ZobristHash ^= zobristTable[move.FromX, move.FromY, GetPieceTypeIndex(player)]; // XOR out the old piece position
            }

            playerPieces |= toMask; // Place piece at the 'to' square.
            boardState.ZobristHash ^= zobristTable[move.ToX, move.ToY, GetPieceTypeIndex(player)]; // XOR in the new piece position

            // Handle all captures at once.
            // Select the correct attack mask based on the capture rule.
            ulong attackMask = useOrthogonalOnlyCapture ? OrthogonalStepMoves[toIndex] : SingleStepMoves[toIndex];

            // Find all opponent pieces to be flipped in a single operation.
            ulong flippedPieces = opponentPieces & attackMask;
            undoInfo.FlippedPiecesMask = flippedPieces;

            if (flippedPieces > 0) {
                playerPieces |= flippedPieces;      // Add the flipped pieces to our bitboard.
                opponentPieces &= ~flippedPieces;   // Remove the flipped pieces from the opponent's bitboard.

                // Update Zobrist hash for each piece that was flipped.
                ulong remainingFlipped = flippedPieces;
                while (remainingFlipped > 0) {
                    int flippedIndex = TrailingZeroCount(remainingFlipped);
                    moveResult.FlippedPieces.Add((flippedIndex % AttaxConstants.BaseConst.BoardSize,flippedIndex / AttaxConstants.BaseConst.BoardSize));
                    int flippedX = flippedIndex % AttaxConstants.BaseConst.BoardSize;
                    int flippedY = flippedIndex / AttaxConstants.BaseConst.BoardSize;

                    // XOR out the key for the opponent piece being removed.
                    boardState.ZobristHash ^= zobristTable[flippedX, flippedY, GetPieceTypeIndex(SwitchPlayer(player))];
                    // XOR in the key for our piece being added in its place.
                    boardState.ZobristHash ^= zobristTable[flippedX, flippedY, GetPieceTypeIndex(player)];

                    remainingFlipped &= remainingFlipped - 1;
                }
            }
            moveResult.UndoInfo = undoInfo;
            return moveResult;
        } catch (Exception ex) {
            ataxxLogger.WriteTolog(ex.ToString()); // Using ToString() for more details
            return default;
        }
    }
    public void UpdateZobristForMove(BitboardState boardState, Move move, PlayerColor player) {
        // Flip the side-to-move key
        boardState.ZobristHash ^= zobristSideToMove;

        // Handle the moving piece
        bool isClone = IsCloneMove(move);
        if (!isClone) // It's a jump, so remove the piece from the 'from' square
        {
            boardState.ZobristHash ^= zobristTable[move.FromX, move.FromY, GetPieceTypeIndex(player)];
        }
        // Place piece at the 'to' square
        boardState.ZobristHash ^= zobristTable[move.ToX, move.ToY, GetPieceTypeIndex(player)];

        // Handle captures
        int toIndex = GetBitIndex(move.ToX, move.ToY);
        ulong attackMask = useOrthogonalOnlyCapture ? OrthogonalStepMoves[toIndex] : SingleStepMoves[toIndex];

        // We must re-calculate flippedPieces here to know which hash keys to flip
        ref ulong opponentPieces = ref (player == PlayerColor.Red ? ref boardState.BluePieces : ref boardState.RedPieces);
        ulong flippedPieces = opponentPieces & attackMask;

        if (flippedPieces > 0) {
            ulong remainingFlipped = flippedPieces;
            while (remainingFlipped > 0) {
                int flippedIndex = TrailingZeroCount(remainingFlipped);
                int flippedX = flippedIndex % AttaxConstants.BaseConst.BoardSize;
                int flippedY = flippedIndex / AttaxConstants.BaseConst.BoardSize;

                // XOR out the key for the opponent piece.
                boardState.ZobristHash ^= zobristTable[flippedX, flippedY, GetPieceTypeIndex(SwitchPlayer(player))];
                // XOR in the key for our piece that replaces it.
                boardState.ZobristHash ^= zobristTable[flippedX, flippedY, GetPieceTypeIndex(player)];

                remainingFlipped &= remainingFlipped - 1;
            }
        }
    }
    public void UnmakeMove(BitboardState boardState, Move move, PlayerColor player, UndoMoveInfo undoInfo) {
        // Restore the Zobrist hash to its exact previous state. This reverts ALL hash changes at once.
        boardState.ZobristHash = undoInfo.PreviousZobristHash;

        // Identify the bitboards.
        ref ulong playerPieces = ref (player == PlayerColor.Red ? ref boardState.RedPieces : ref boardState.BluePieces);
        ref ulong opponentPieces = ref (player == PlayerColor.Red ? ref boardState.BluePieces : ref boardState.RedPieces);

        // Un-flip any captured pieces using the stored mask.
        if (undoInfo.FlippedPiecesMask > 0) {
            playerPieces &= ~undoInfo.FlippedPiecesMask;    // Remove the pieces from our side.
            opponentPieces |= undoInfo.FlippedPiecesMask; // Give them back to the opponent.
        }

        // Revert the primary move.
        ulong toMask = 1UL << GetBitIndex(move.ToX, move.ToY);
        playerPieces &= ~toMask; // Remove the piece from the destination square.

        // If it was a jump move, put the piece back on its starting square.
        if (!IsCloneMove(move)) {
            ulong fromMask = 1UL << GetBitIndex(move.FromX, move.FromY);
            playerPieces |= fromMask; // Restore the piece.
        }
    }
    private int Evaluate(BitboardState board, PlayerColor player) {
        // Check the flag to decide which evaluation method to use.        
        if (this.UseMLEvaluation) {            
           return EvaluateML(board, player);
            
        } else {
            // Fallback to the classic heuristic evaluation.
            return EvaluateHeuristic(board, player);
        }
    }
    private int EvaluateML(BitboardState board, PlayerColor player) {

        PredictionService predictionService = new PredictionService();
        if (predictionService.IsModelLoaded) {
            FeatureExtractor featureExtractor = new FeatureExtractor();
            // Get the structured feature set.
            MLFeatureSet mlFeatureSet = featureExtractor.CreateBaseFeatureSet(board, player);

            // Convert it to the flat array the model expects.
            BoardStateInput featureInput = new BoardStateInput { Features = mlFeatureSet.ToFloatList().ToArray() };


            // Use the PredictionService to get a score from the model.
            float predictedScore = predictionService.Predict(featureInput);

            // Scale the score for the Alpha-Beta search.
            return (int)(predictedScore * 1000);
        } else { return 0; }            
        
    }
    // Evaluate is from the perspective of 'player'. Higher is better for 'player'.
    private int EvaluateHeuristic(BitboardState board, PlayerColor player) { 
        PlayerColor enemy = SwitchPlayer(player);
        int finalEvaluationScore = 0;

        var (playerPieces, enemyPieces) = GetRedAndBlueCounts(board, player);        
        int totalPieces = playerPieces + enemyPieces;

        // The raw piece difference is now multiplied by a tunable weight. This correctly values material advantage relative to positional features, as per your analysis.
        finalEvaluationScore += (playerPieces - enemyPieces) * AttaxConstants.GetScaledMaterialWeight(totalPieces);

        // Corner and edge control is valuable at all stages of the game.
        //finalEvaluationScore += GetCornerEdgeBonusEvaluation(board, player, enemy);

        // --- Heuristic Scores ---
        int mobility = 0, centerControl = 0, stability = 0;

        List<Move> playerMovesEval = GetAllValidMoves(board, player);
        List<Move> enemyMovesEval = GetAllValidMoves(board, enemy);

        /*if (totalPieces <= AttaxConstants.ChipsCountConst.lateGame) {            
            mobility = playerMovesEval.Count - enemyMovesEval.Count;
            finalEvaluationScore += mobility * AttaxConstants.GetScaledMobilityWeight(totalPieces);
        }*/

        
        // comparing the player's potential against the enemy's, 
        /*if (totalPieces <= AttaxConstants.ChipsCountConst.lateGame) {
            //  Calculate potential mobility for the current player.
            int playerPotential = GetPotentialMobility(board, playerMovesEval);

            // Get enemy's potential mobility for comparison.
            int enemyPotential = GetPotentialMobility(board, enemyMovesEval);

            // The final score is now based on the *difference*, rewarding the player
            // for having more future opportunities than the opponent.            
            finalEvaluationScore += (playerPotential - enemyPotential) * AttaxConstants.EvaluateHeuristicConst.PotentialMobilityWeight;
        }*/

        /*if (totalPieces <= AttaxConstants.ChipsCountConst.earlyGame) {
            centerControl = GetCenterControl(board, player) - GetCenterControl(board, enemy);
            finalEvaluationScore += centerControl * AttaxConstants.EvaluateHeuristicConst.CenterControlWeight;
        }*/

                
        /*if (totalPieces >= AttaxConstants.ChipsCountConst.lateGame) {
            var playerStabilityBonus = GetStabilityBonus(board, player);
            var enemyStabilityBonus = GetStabilityBonus(board, enemy);

            // This correctly calculates the relative advantage in stability.
            stability = playerStabilityBonus - enemyStabilityBonus;

            //finalEvaluationScore +=  stability * (aiVsAigame ? AttaxConstants.EvaluateHeuristicConst.StabilityWeight : AttaxConstants.GetDynamicStabilityMult(totalPieces));        
            finalEvaluationScore += stability * AttaxConstants.GetDynamicStabilityMult(totalPieces);
        }        */

        return finalEvaluationScore;
    }
    // Quiescence search: 'player' is whose turn it is, 'maximizing' is if 'player' is maximizing their own score.
    // this function has negamax style, matching the changes made in the main AlphaBeta function.
    private int Quiescence(BitboardState board, PlayerColor player, int alpha, int beta, int depth) {
        // First, get the evaluation of the current "stand-pat" position.
        int standPatScore = Evaluate(board, player);

        // Check for an immediate beta-cutoff. If the static score is already
        // better than or equal to beta, the opponent won't allow this position.
        if (standPatScore >= beta) {
            return beta; // Fail-high
        }

        // If the stand-pat score is better than alpha, update alpha.
        if (standPatScore > alpha) {
            alpha = standPatScore;
        }

        // If we are at max depth or the game is over, we can't search further.
        if (depth <= 0 || IsGameOver(board)) {
            return standPatScore;
        }

        // Generate only "noisy" moves (captures) to resolve tactical situations.
        var noisyMoves = GetNoisyMoves(board, player);

        foreach (var move in noisyMoves) {
            var moveInfo = MakeMove(board, move, player);
            int score = -Quiescence(board, SwitchPlayer(player), -beta, -alpha, depth - 1);
            UnmakeMove(board, move, player, moveInfo.UndoInfo);

            if (score >= beta) {
                return beta; // Beta-cutoff, this move is too good.
            }
            if (score > alpha) {
                alpha = score; // Found a new best move in this quiescent state.
            }
        }

        // Return the best score found, which is alpha.
        return alpha;
    }

    #endregion



    #region helpers 
    private List<Move> GetNoisyMoves(BitboardState boardState, PlayerColor player) {
        var allMoves = GetAllValidMoves(boardState, player);
        var noisyMoves = new List<Move>();
        foreach (var move in allMoves) {
            // A move is "noisy" if it's a capture.
            if (CountFlippedEnemies(boardState, move, player) > 0) {
                noisyMoves.Add(move);
            }
        }
        return noisyMoves;
    }
    private bool IsCaptureMove(BitboardState boardState, Move move) {
        // A move is a capture if it flips at least one enemy piece.
        ulong opponentPieces = boardState.RedPieces | boardState.BluePieces; // Check against all pieces
        int toIndex = GetBitIndex(move.ToX, move.ToY);
        return (SingleStepMoves[toIndex] & opponentPieces) != 0;
    }
    public int PopCount(ulong value) {
        int count = 0;
        while (value > 0) {
            // This operation clears the least significant set bit.
            value &= (value - 1);
            count++;
        }
        return count;
    }
    private int TrailingZeroCount(ulong value) {
        if (value == 0) return 64;
        // Isolates the lowest set bit, subtracts one to create a mask
        // of all lower bits, and then counts them to find the index.
        return PopCount((value & (0 - value)) - 1);
    }
    public bool IsOrthogonal(int x1, int y1, int x2, int y2) => (x1 == x2 && Math.Abs(y1 - y2) == 1) || (y1 == y2 && Math.Abs(x1 - x2) == 1);
    public bool IsAdjacent(int x1, int y1, int x2, int y2) => Math.Abs(x1 - x2) <= 1 && Math.Abs(y1 - y2) <= 1 && !(x1 == x2 && y1 == y2);
    public PlayerColor SwitchPlayer(PlayerColor current) => current == AtaxxAIEngine.PlayerColor.Red ? AtaxxAIEngine.PlayerColor.Blue : current == AtaxxAIEngine.PlayerColor.Blue ? AtaxxAIEngine.PlayerColor.Red : AtaxxAIEngine.PlayerColor.None;

    private int GetPieceTypeIndex(PlayerColor color) {
        switch (color) {
            case PlayerColor.Red: return 0;
            case PlayerColor.Blue: return 1;
            case PlayerColor.Blocked: return 2;
            default: return -1; // Should not happen for actual pieces
        }
    }
    private void InitZobrist(int? seed) {
        var rng = seed.HasValue ? new Random(seed.Value) : new Random();
        // We now loop 3 times for the 3 actual piece types (Red, Blue, Blocked)
        for (int p = 0; p < 3; p++) {
            for (int y = 0; y < AttaxConstants.BaseConst.BoardSize; y++) {
                for (int x = 0; x < AttaxConstants.BaseConst.BoardSize; x++) {
                    byte[] buffer = new byte[8];
                    rng.NextBytes(buffer);            
                    zobristTable[x, y, p] = BitConverter.ToUInt64(buffer, 0);
                }
            }
        }
        
        byte[] sideBytes = new byte[8];
        rng.NextBytes(sideBytes);
        zobristSideToMove = BitConverter.ToUInt64(sideBytes, 0);        
    }
    public void ResetEngine() {
        // Reset logging state (this logic remains the same)
        if (logCoordinator != null) {
            logCoordinator.GlobalLog = new Dictionary<int, TurnLog>();
            logCoordinator.TurnIndex = 0;
        }
        // Set up the board to its initial starting state using our new bitboard method.
        // This single call replaces the entire for-loop block and correctly places starting pieces.
        SetupBoard(SwitchPlayer(AIPlayerColor));
        // Clear search-related caches from the previous game.
        if (transpositionTable != null) {
            transpositionTable.Clear();
        }
    }
    public bool IsCloneMove(Move move) => Math.Abs(move.ToX - move.FromX) <= 1 && Math.Abs(move.ToY - move.FromY) <= 1;

    /// <summary>
    /// Computes a Zobrist hash from a bitboard state from scratch.
    /// This is used to get the hash for the initial board position.
    /// </summary>
    public ulong ComputeZobristHash(BitboardState boardState, PlayerColor currentPlayer) {
        ulong hash = 0;
        ulong tempBoard;

        // XOR keys for all Red pieces
        tempBoard = boardState.RedPieces;
        while (tempBoard > 0) {
            int index = TrailingZeroCount(tempBoard);
            int x = index % AttaxConstants.BaseConst.BoardSize;
            int y = index / AttaxConstants.BaseConst.BoardSize;
            hash ^= zobristTable[x, y, GetPieceTypeIndex(PlayerColor.Red)];
            tempBoard &= tempBoard - 1; // Clear this bit
        }

        // XOR keys for all Blue pieces
        tempBoard = boardState.BluePieces;
        while (tempBoard > 0) {
            int index = TrailingZeroCount(tempBoard);
            int x = index % AttaxConstants.BaseConst.BoardSize;
            int y = index / AttaxConstants.BaseConst.BoardSize;
            hash ^= zobristTable[x, y, GetPieceTypeIndex(PlayerColor.Blue)];
            tempBoard &= tempBoard - 1; // Clear this bit
        }

        // XOR keys for all Blocked squares
        tempBoard = boardState.BlockedSquares;
        while (tempBoard > 0) {
            int index = TrailingZeroCount(tempBoard);
            int x = index % AttaxConstants.BaseConst.BoardSize;
            int y = index / AttaxConstants.BaseConst.BoardSize;
            hash ^= zobristTable[x, y, GetPieceTypeIndex(PlayerColor.Blocked)];
            tempBoard &= tempBoard - 1; // Clear this bit
        }

        // XOR the key for the side to move
        if (currentPlayer == PlayerColor.Blue) {
            hash ^= zobristSideToMove;
        }

        return hash;
    }
    /*
    private bool IsValidMove(BitboardState boardState, Move move, PlayerColor player) {        
      
        if (move.FromX < 0 || move.FromX >= AttaxConstants.BaseConst.BoardSize ||
            move.FromY < 0 || move.FromY >= AttaxConstants.BaseConst.BoardSize ||
            move.ToX < 0 || move.ToX >= AttaxConstants.BaseConst.BoardSize ||
            move.ToY < 0 || move.ToY >= AttaxConstants.BaseConst.BoardSize) {
            return false;
        }


        int dx = Math.Abs(move.ToX - move.FromX);
        int dy = Math.Abs(move.ToY - move.FromY);
        int distance = Math.Max(dx, dy);
        if (distance == 0 || distance > 2) {
            return false; // Not a valid clone or jump distance.
        }


        int fromIndex = GetBitIndex(move.FromX, move.FromY);
        int toIndex = GetBitIndex(move.ToX, move.ToY);
        ulong fromMask = 1UL << fromIndex;
        ulong toMask = 1UL << toIndex;


        ulong playerPieces = (player == PlayerColor.Red) ? boardState.RedPieces : boardState.BluePieces;
        if ((playerPieces & fromMask) == 0) {
            return false; // The player does not have a piece at the source square.
        }


        ulong emptySquares = boardState.EmptySquares();
        if ((emptySquares & toMask) == 0) {
            return false; // The destination square is not empty (it's occupied or blocked).
        }

        // If all checks pass, the move is valid.
        return true;
    }*/
    #endregion

    #region aihelpers (Evaluation Components - modified for toggles)
    
    public List<Move> GetOrderedMoves(BitboardState bitboardState, PlayerColor player, int currentPly, Move[,] killerMoves) {
        // Only calculate flips for captures
        List<Move> allMoves = GetAllValidMoves(bitboardState, player);
        if (allMoves.Count <= 1) return allMoves;

        // Calculate flips once and store in an anonymous type
        var movesWithScores = allMoves
            .Select(m => new {
                Move = m,
                Flips = CountFlippedEnemies(bitboardState, m, player)
            });

        var captureMoves = movesWithScores
            .Where(x => x.Flips > 0)
            .OrderByDescending(x => x.Flips)
            .Select(x => x.Move);

        // Get the remaining moves more efficiently
        var captureMoveSet = new HashSet<Move>(captureMoves);
        var nonCaptureMoves = allMoves
            .Where(m => !captureMoveSet.Contains(m))
            .OrderByDescending(m => {
                // A more robust killer move check
                if (currentPly >= AttaxConstants.BaseConst.KillerMoveMaxDepth) return false;
                return killerMoves[currentPly, 0].Equals(m) || killerMoves[currentPly, 1].Equals(m);
            });

        return captureMoves.Concat(nonCaptureMoves).ToList();
    }
    private int GetPositionalValue(int x, int y) { 
        int[,] positionWeight = { 
            {1,2,3,3,3,2,1},{2,3,4,4,4,3,2},{3,4,5,6,5,4,3},
            {3,4,6,7,6,4,3},{3,4,5,6,5,4,3},{2,3,4,4,4,3,2},{1,2,3,3,3,2,1}
        };        
        if (y >= 0 && y < AttaxConstants.BaseConst.BoardSize && x >= 0 && x < AttaxConstants.BaseConst.BoardSize) return positionWeight[y, x];
        return 0;
    }

    /// <summary>
    /// Calculates potential mobility from a bitboard of destination squares.
    /// </summary>
    /// <param name="boardState">The current board state.</param>
    /// <param name="destinationSquares">A bitboard where each set bit is a potential landing square.</param>
    /// <returns>The number of unique, empty squares adjacent to any of the destination squares.</returns>
    private int GetPotentialMobilityInternal(BitboardState boardState, ulong destinationSquares) {
        ulong emptySquares = boardState.EmptySquares();
        ulong potentialMobilitySquares = 0UL;

        // "Dilate" the destination bitboard by one step in all directions to find all adjacent squares.
        ulong remainingDestinations = destinationSquares;
        while (remainingDestinations > 0) {
            int destIndex = TrailingZeroCount(remainingDestinations);

            // For each destination, get its 8 neighbors and add them to our set using bitwise OR.
            // The OR operation naturally handles uniqueness, replacing the need for a HashSet.
            potentialMobilitySquares |= SingleStepMoves[destIndex];

            remainingDestinations &= remainingDestinations - 1;
        }

        // The final area is the intersection of these adjacent squares and the actually empty squares.
        ulong finalMobilityArea = potentialMobilitySquares & emptySquares;

        // The score is the number of squares in this final area.
        return PopCount(finalMobilityArea);
    }
    /// <summary>
    /// Calculates potential mobility for a given list of moves.
    /// </summary>
    /// <param name="boardState">The current board state.</param>
    /// <param name="moves">The list of moves whose destinations will be analyzed.</param>
    /// <returns>The potential mobility score.</returns>
    public int GetPotentialMobility(BitboardState boardState, List<Move> moves) {
        // Convert the list of moves into a single bitboard of destination squares.
        ulong destinationSquares = 0UL;
        foreach (var move in moves) {
            destinationSquares |= (1UL << GetBitIndex(move.ToX, move.ToY));
        }        
        return GetPotentialMobilityInternal(boardState, destinationSquares);
    }

    /// <summary>
    /// Checks if there is at least one empty square within a 2-tile radius of a given coordinate.
    /// </summary>
    /// <param name="boardState">The current bitboard state of the game.</param>
    /// <param name="x">The x-coordinate of the center of the check.</param>
    /// <param name="y">The y-coordinate of the center of the check.</param>
    /// <returns>True if at least one empty square exists in the 5x5 area, otherwise false.</returns>
    private bool HasAdjacentEmpty(BitboardState boardState, int x, int y) {
        // Get a bitboard of all empty squares.
        ulong emptySquares = boardState.EmptySquares();

        // Get the index of the square we are checking around.
        int checkIndex = GetBitIndex(x, y);

        // Get the pre-calculated masks for all squares within a 2-tile radius.
        // We combine the 1-step (clone) and 2-step (jump) masks.
        ulong allNeighborsMask = SingleStepMoves[checkIndex] | TwoStepMoves[checkIndex];

        // Check for any intersection between the neighboring squares and the empty squares.
        // If the result is not zero, it means at least one bit was set in both,
        // indicating an adjacent empty square was found.
        return (allNeighborsMask & emptySquares) != 0;
    }

    /// <summary>
    /// Checks if a piece at a given coordinate is stable, using bitboards.
    /// A piece is considered stable if no adjacent opponent piece has any empty squares around it to move to.
    /// </summary>
    /// <param name="boardState">The current bitboard state of the game.</param>
    /// <param name="x">The x-coordinate of the piece to check.</param>
    /// <param name="y">The y-coordinate of the piece to check.</param>
    /// <param name="player">The color of the piece to check.</param>
    /// <returns>True if the piece is considered stable, otherwise false.</returns>
    private bool IsStable(BitboardState boardState, int x, int y, PlayerColor player) {
        // Corner pieces are always stable. This coordinate-based check is fast and remains.
        bool isCorner = (x == 0 || x == AttaxConstants.BaseConst.BoardSize - 1) &&
                        (y == 0 || y == AttaxConstants.BaseConst.BoardSize - 1);
        if (isCorner) return true;

        // Get the pre-calculated mask of the 8 neighbors for the piece we are checking.
        int pieceIndex = GetBitIndex(x, y);
        ulong pieceNeighborsMask = SingleStepMoves[pieceIndex];

        // Find which of those neighbors are occupied by the opponent in a single operation.
        PlayerColor opponentColor = SwitchPlayer(player);
        ulong opponentPieces = (opponentColor == PlayerColor.Red) ? boardState.RedPieces : boardState.BluePieces;
        ulong adjacentOpponents = pieceNeighborsMask & opponentPieces;

        // If there are no adjacent opponents, the piece cannot be captured and is stable.
        if (adjacentOpponents == 0) return true;

        // For each adjacent opponent we found, check if IT has any mobility.
        ulong remainingOpponents = adjacentOpponents;
        while (remainingOpponents > 0) {
            int opponentIndex = TrailingZeroCount(remainingOpponents);
            int opponentX = opponentIndex % AttaxConstants.BaseConst.BoardSize;
            int opponentY = opponentIndex / AttaxConstants.BaseConst.BoardSize;
           
            if (HasAdjacentEmpty(boardState, opponentX, opponentY)) {                .
                // According to this heuristic, this makes our piece unstable.
                return false;
            }

            // Clear this opponent's bit and check the next one in the bitboard.
            remainingOpponents &= remainingOpponents - 1;
        }

        // If we checked all adjacent opponents and none of them had any mobility,
        // our piece is considered stable.
        return true;
    }

    /// <summary>
    /// Calculates the stability bonus for a player using bitboards.
    /// It iterates only over the player's pieces and uses the refactored IsStable check.
    /// </summary>
    /// <param name="boardState">The current bitboard state of the game.</param>
    /// <param name="player">The player whose stable pieces are to be counted.</param>
    /// <returns>A stability bonus score, typically the count of stable pieces.</returns>
    public int GetStabilityBonus(BitboardState boardState, PlayerColor player) {
        int bonus = 0;
        ulong playerPieces = (player == PlayerColor.Red) ? boardState.RedPieces : boardState.BluePieces;

        // Use a fast bit-twiddling loop to iterate ONLY over the player's pieces,
        // not all 49 squares of the board.
        ulong remainingPieces = playerPieces;
        while (remainingPieces > 0) {
            int pieceIndex = TrailingZeroCount(remainingPieces);
            int x = pieceIndex % AttaxConstants.BaseConst.BoardSize;
            int y = pieceIndex / AttaxConstants.BaseConst.BoardSize;
            
            if (IsStable(boardState, x, y, player)) {
                bonus++;
            }

            // Clear the bit for the piece we just processed and move to the next one.
            remainingPieces &= remainingPieces - 1;
        }

        return bonus;
    }

    /// <summary>
    /// Calculates a weighted score for center control using bitboards and a pre-calculated weight map.
    /// </summary>
    /// <param name="boardState">The current bitboard state of the game.</param>
    /// <param name="player">The player whose center control is being evaluated.</param>
    /// <returns>A weighted score for controlling the center.</returns>
    public int GetCenterControl(BitboardState boardState, PlayerColor player) {
        int controlScore = 0;
        ulong playerPieces = (player == PlayerColor.Red) ? boardState.RedPieces : boardState.BluePieces;

        // Use a fast bit-twiddling loop to iterate ONLY over the player's pieces.
        ulong remainingPieces = playerPieces;
        while (remainingPieces > 0) {
            int pieceIndex = TrailingZeroCount(remainingPieces);

            // Add the pre-calculated weight for this square.
            // If the piece is not in the center, its weight in the array will be 0.
            controlScore += CenterControlWeights[pieceIndex];

            // Clear the bit for the piece we just processed and move to the next one.
            remainingPieces &= remainingPieces - 1;
        }

        return controlScore;
    }
    /// <summary>
    /// Calculates the maximum number of pieces the opponent can flip on their next turn.
    /// iterates through all possible opponent moves efficiently using bitboards.
    /// </summary>
    /// <param name="boardStateAfterMove">The bitboard state AFTER the original player's move.</param>
    /// <param name="originalPlayer">The player who made the move to get to this state.</param>
    /// <returns>The maximum number of pieces the opponent can capture in a single move.</returns>
    private int CalculateMaxOpponentFlips(BitboardState boardStateAfterMove, PlayerColor originalPlayer) {
        PlayerColor opponent = SwitchPlayer(originalPlayer);
        ulong opponentPieces = (opponent == PlayerColor.Red) ? boardStateAfterMove.RedPieces : boardStateAfterMove.BluePieces;
        ulong emptySquares = boardStateAfterMove.EmptySquares();

        int maxFlip = 0;

        // Iterate through each of the opponent's pieces using a fast bit-twiddling loop
        ulong remainingPieces = opponentPieces;
        while (remainingPieces > 0) {
            int fromIndex = TrailingZeroCount(remainingPieces);
            int fromX = fromIndex % AttaxConstants.BaseConst.BoardSize;
            int fromY = fromIndex / AttaxConstants.BaseConst.BoardSize;

            // Find all possible landing squares (both clone and jump) for this single piece
            ulong allDestinations = (SingleStepMoves[fromIndex] | TwoStepMoves[fromIndex]) & emptySquares;

            // Now, for each possible destination, calculate the number of flips
            ulong remainingDestinations = allDestinations;
            while (remainingDestinations > 0) {
                int toIndex = TrailingZeroCount(remainingDestinations);
                int toX = toIndex % AttaxConstants.BaseConst.BoardSize;
                int toY = toIndex / AttaxConstants.BaseConst.BoardSize;

                // We need a temporary Move object to pass to our existing CountFlippedEnemies method.
                Move tempOpponentMove = new Move(fromX, fromY, toX, toY);

                // Use our already-refactored, bitboard-native CountFlippedEnemies method
                int flips = CountFlippedEnemies(boardStateAfterMove, tempOpponentMove, opponent);

                if (flips > maxFlip) {
                    maxFlip = flips;
                }

                remainingDestinations &= remainingDestinations - 1; // Move to the next destination
            }

            remainingPieces &= remainingPieces - 1; // Move to the next opponent piece
        }

        return maxFlip;
    }


    #endregion
   

}