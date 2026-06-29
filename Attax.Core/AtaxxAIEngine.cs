using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ZLinq;
using static Attax.Core.AILogCoordinator;

namespace Attax.Core {
    public class AtaxxAIEngine : IDisposable {

        #region Configurable AI Features    
        public bool UseTimeManagement { get; set; } = false;
        public int TimeManagedExtraDepth { get; set; } = 4;
        public long SearchTimeLimitMs { get; set; } = 5000;
        public long? MaxNodes { get; set; } = null;
        public int? seed = null; //optional seed parameter
        #endregion

        #region Configurables
        public ILogSink ataxxLogger;
        public AtaxxThreadHelper ataxxHelper { get; set; }
        public bool useOrthogonalOnlyCapture { get; set; }
        public int aiDepth { get; set; }
        public AILogCoordinator logCoordinator { get; set; }

        private IValueEvaluator evaluator;
        private IValueEvaluator searchEvaluator;
        private float evaluationScale;
        private bool trainingMode;
        private bool disableParallelRootSearch;
        private bool useMLRootOnly;
        private bool disableQuiescenceSearch;
        private bool disableNullMovePruning;
        private bool logGenMode;
        private readonly Random rng;
        // Pre-allocated parallel helper pool. Lazily initialized on first parallel search;
        // reused across all subsequent calls so the 32 MB TT per helper is only allocated once.
        private AtaxxThreadHelper[] _parallelHelpers;
        #endregion

        #region Engine Constants
        private const float DefaultEvaluationScale = 10000.0f;
        private const int AspirationWindowMultiplier = 50;
        private const double TemperatureDivisor = 10.0;
        private const int InfinityMargin = 10000;
        private const int ImmediateWinScore = int.MaxValue - 1000;
        private const int ForcedWinEarlyExitScore = int.MaxValue - 2000;
        // Decisive score for a true terminal (game-over) node. Larger than any heuristic leaf
        // eval (heuristic * evaluationScale is at most ~30M) so wins/losses always dominate, and
        // ply-adjusted so the search prefers faster wins / slower losses.
        private const int TerminalWinScore = 1_000_000_000;
        private const int MaxRandomBlockAttempts = 1000;
        #endregion

        #region enums

        public struct TTEntry {
            public ulong key;
            public Move bestMove;
            public byte depth;
            public int score;
            public byte flag;
            public byte age;
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
        public struct Move : IEquatable<Move> {
            public int FromX, FromY, ToX, ToY;
            public Move(int fx, int fy, int tx, int ty) {
                FromX = fx; FromY = fy; ToX = tx; ToY = ty;
            }

            public bool Equals(Move other) {
                return FromX == other.FromX && FromY == other.FromY && ToX == other.ToX && ToY == other.ToY;
            }

            public override bool Equals(object obj) {
                return obj is Move other && Equals(other);
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

        public struct AIEngineConfig {
            public bool UseTimeManagement;
            public int TimeManagedExtraDepth;
            public long SearchTimeLimitMs;
            public int? Seed;
            public bool UseOrthogonalOnlyCapture;
            public int AiDepth;
            public bool TrainingMode;
            public bool UseMLEvaluation;
            public bool UseMLRootOnly;
            public float EvaluationScale;
            public bool DisableParallelRootSearch;
            public bool DisableQuiescenceSearch;
            public bool DisableNullMovePruning;
            public bool LogGenMode;
        }

        #endregion

        #region publics

        public PlayerColor AIPlayerColor;
        public BitboardState Board { get; set; }
        // When true, GetBestMove must be called from Unity's main thread because the evaluator
        // uses GPU APIs (e.g. ComputeBuffer in Sentis GPUCompute). Set by the Unity-side caller
        // after construction. Defaults to false; the console app ignores this property.
        public bool EvaluatorRequiresMainThread { get; set; }
        #endregion

        #region constructor
        public AtaxxAIEngine(
            IValueEvaluator evaluator,
            ILogSink ataxxLogger = null,
            AILogCoordinator coordinator = null,
            AIEngineConfig config = default
        ) {
            this.evaluator = evaluator;
            this.ataxxLogger = ataxxLogger;
            this.logCoordinator = coordinator;

            // Apply config
            this.UseTimeManagement = config.UseTimeManagement;
            this.TimeManagedExtraDepth = config.TimeManagedExtraDepth;
            this.SearchTimeLimitMs = config.SearchTimeLimitMs;
            this.seed = config.Seed;
            this.useOrthogonalOnlyCapture = config.UseOrthogonalOnlyCapture;
            this.aiDepth = config.AiDepth;
            this.trainingMode = config.TrainingMode;
            this.evaluationScale = config.EvaluationScale != 0 ? config.EvaluationScale : DefaultEvaluationScale;
            this.disableParallelRootSearch = config.DisableParallelRootSearch;
            this.useMLRootOnly = config.UseMLRootOnly;
            this.disableQuiescenceSearch = config.DisableQuiescenceSearch;
            this.disableNullMovePruning = config.DisableNullMovePruning;
            this.logGenMode = config.LogGenMode;
            this.rng = config.Seed.HasValue ? new Random(config.Seed.Value) : new Random();
            this.searchEvaluator = this.useMLRootOnly ? new HeuristicEvaluator() : this.evaluator;

            this.Board = new BitboardState();
            SetupBoard(SwitchPlayer(AIPlayerColor));

        }
        #endregion
        #region board helpers
        public static int GetBitIndex(int x, int y) {
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
                Board.ZobristHash ^= ZobristHasher.GetPieceKey(x, y, GetPieceTypeIndex(currentColor));
            }

            // If we are placing a new piece (not making it empty),
            // XOR its value IN to the hash to add it.
            if (color != PlayerColor.None) {
                Board.ZobristHash ^= ZobristHasher.GetPieceKey(x, y, GetPieceTypeIndex(color));
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
            // BoardLookup is static and initialized automatically
            this.Board.ZobristHash = ComputeZobristHash(this.Board, startPlayer);
        }

        /// <summary>
        /// Recomputes the full Zobrist hash for the current board using the shared hasher.
        /// Hash convention: side-to-move key is XOR'd when side-to-move is Blue.
        /// </summary>
        public void RecomputeHashForCurrentBoard(PlayerColor sideToMove) {
            Board.ZobristHash = ComputeZobristHash(Board, sideToMove);
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
        public List<Move> GetAllValidMoves(BitboardState boardState, PlayerColor player, AtaxxThreadHelper helper = null) {
            if (helper != null) helper.GetAllValidMovesCalls++;
            var moves = new List<Move>();
            ulong playerPieces = (player == PlayerColor.Red) ? boardState.RedPieces : boardState.BluePieces;
            ulong emptySquares = boardState.EmptySquares();
            ulong seenCloneDestinations = 0UL;

            // Loop through each of the player's pieces using a fast bit-twiddling technique
            ulong remainingPieces = playerPieces;
            while (remainingPieces > 0) {
                // Get the index of the current piece using our new helper method
                int fromIndex = TrailingZeroCount(remainingPieces);
                int fromX = fromIndex % AttaxConstants.BaseConst.BoardSize;
                int fromY = fromIndex / AttaxConstants.BaseConst.BoardSize;

                // Generate Clone Moves
                // Pruning: cloning to a specific destination yields the same resulting board state,
                // regardless of which adjacent piece performed the clone.
                ulong validCloneMoves = BoardLookup.SingleStepMoves[fromIndex] & emptySquares & ~seenCloneDestinations;

                ulong remainingClones = validCloneMoves;
                while (remainingClones > 0) {
                    int toIndex = TrailingZeroCount(remainingClones);
                    moves.Add(new Move(fromX, fromY, toIndex % AttaxConstants.BaseConst.BoardSize, toIndex / AttaxConstants.BaseConst.BoardSize));
                    seenCloneDestinations |= (1UL << toIndex);
                    remainingClones &= remainingClones - 1;
                }

                // Generate Jump Moves (intentionally NOT canonicalized; each jump origin matters)
                ulong validJumpMoves = BoardLookup.TwoStepMoves[fromIndex] & emptySquares;

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

        // Hot-path move generator: identical logic to GetAllValidMoves but writes directly into a
        // pre-allocated buffer instead of creating a List<Move>. Zero GC allocation.
        private int FillMoves(BitboardState boardState, PlayerColor player, Move[] buffer, AtaxxThreadHelper helper = null) {
            if (helper != null) helper.GetAllValidMovesCalls++;
            int count = 0;
            ulong playerPieces = (player == PlayerColor.Red) ? boardState.RedPieces : boardState.BluePieces;
            ulong emptySquares = boardState.EmptySquares();
            ulong seenCloneDestinations = 0UL;
            ulong remainingPieces = playerPieces;
            while (remainingPieces > 0) {
                int fromIndex = TrailingZeroCount(remainingPieces);
                int fromX = fromIndex % AttaxConstants.BaseConst.BoardSize;
                int fromY = fromIndex / AttaxConstants.BaseConst.BoardSize;
                ulong validCloneMoves = BoardLookup.SingleStepMoves[fromIndex] & emptySquares & ~seenCloneDestinations;
                ulong remainingClones = validCloneMoves;
                while (remainingClones > 0) {
                    int toIndex = TrailingZeroCount(remainingClones);
                    buffer[count++] = new Move(fromX, fromY, toIndex % AttaxConstants.BaseConst.BoardSize, toIndex / AttaxConstants.BaseConst.BoardSize);
                    seenCloneDestinations |= (1UL << toIndex);
                    remainingClones &= remainingClones - 1;
                }
                ulong validJumpMoves = BoardLookup.TwoStepMoves[fromIndex] & emptySquares;
                ulong remainingJumps = validJumpMoves;
                while (remainingJumps > 0) {
                    int toIndex = TrailingZeroCount(remainingJumps);
                    buffer[count++] = new Move(fromX, fromY, toIndex % AttaxConstants.BaseConst.BoardSize, toIndex / AttaxConstants.BaseConst.BoardSize);
                    remainingJumps &= remainingJumps - 1;
                }
                remainingPieces &= remainingPieces - 1;
            }
            return count;
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
        public static (int p1Count, int p2Count) GetRedAndBlueCounts(BitboardState boardState, PlayerColor p1Color)
            => BitboardFeatures.GetRedAndBlueCounts(boardState, p1Color);
        private bool HasAnyLegalMove(BitboardState board, PlayerColor player) {
            ulong playerPieces = (player == PlayerColor.Red) ? board.RedPieces : board.BluePieces;
            ulong emptySquares = board.EmptySquares();
            if (emptySquares == 0) return false;

            while (playerPieces > 0) {
                int fromIndex = TrailingZeroCount(playerPieces);
                // Check if any single-step or two-step move lands on an empty square
                if (((BoardLookup.SingleStepMoves[fromIndex] | BoardLookup.TwoStepMoves[fromIndex]) & emptySquares) != 0) {
                    return true;
                }
                playerPieces &= playerPieces - 1;
            }
            return false;
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
            if (!HasAnyLegalMove(boardState, PlayerColor.Red) ||
                !HasAnyLegalMove(boardState, PlayerColor.Blue)) {
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
                ? BoardLookup.OrthogonalStepMoves[toIndex]
                : BoardLookup.SingleStepMoves[toIndex];

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
            int maxAttempts = MaxRandomBlockAttempts;
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
        public struct SearchStats {
            public long Nodes;
            public long QNodes;
            public long Evals;
            public long GetAllValidMovesCalls;
            public long TTProbes;
            public long TTHits;

            public void Add(AtaxxThreadHelper helper) {
                Nodes += helper.Nodes;
                QNodes += helper.QNodes;
                Evals += helper.Evals;
                GetAllValidMovesCalls += helper.GetAllValidMovesCalls;
                TTProbes += helper.TTProbes;
                TTHits += helper.TTHits;
            }

            public override string ToString() {
                return $"Nodes: {Nodes}, QNodes: {QNodes}, Evals: {Evals}, MovesGen: {GetAllValidMovesCalls}, TT: {TTHits}/{TTProbes} ({(TTProbes > 0 ? (TTHits * 100.0 / TTProbes).ToString("F1") : "0")}%)";
            }
        }

        public SearchStats LastSearchStats { get; private set; }

        public Move GetBestMove(PlayerColor player, bool isHumanSimulation = false, float temperature = 0f, int topK = 1) {
            try {
                this.currentSearchAge++; // Increment age for this new search
                LastSearchStats = new SearchStats();
                var debug = false;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                long? effectiveTimeLimitMs = UseTimeManagement ? (long?)SearchTimeLimitMs : null;
                BitboardState searchRootBoard = Board.Clone();

                var allMoves = GetAllValidMoves(searchRootBoard, player);
                if (allMoves.Count == 0) return default;

                // Root-only "comeback" aggression, computed once from the AI's (fixed root)
                // perspective. >1 when the AI is behind. Used to modulate the root bonus only.
                var (aiRootCount, oppRootCount) = GetRedAndBlueCounts(searchRootBoard, player);
                double rootAggression = AttaxConstants.GetAggressionFactor(aiRootCount - oppRootCount);

                Move bestMoveOverall = allMoves[0];
                int previousScore = 0;
                int maxDepthToSearch = UseTimeManagement ? aiDepth + TimeManagedExtraDepth : aiDepth;
                long nodesRemainingForSearch = MaxNodes ?? -1;
                int finalDepthReached = 0;
                int startingDepth = trainingMode ? maxDepthToSearch : 1;
                bool shouldCollectLogDetails = logCoordinator != null;
                Dictionary<Move, int> finalEvaluationDetails = null;
                Dictionary<int, AIMoveCandidates> finalAiMoveCandidatesDict = null;

                for (int currentDepth = startingDepth; currentDepth <= maxDepthToSearch; currentDepth++) {
                    if (effectiveTimeLimitMs.HasValue && clock.ElapsedMilliseconds > effectiveTimeLimitMs.Value) break;
                    if (MaxNodes.HasValue && (LastSearchStats.Nodes + LastSearchStats.QNodes) >= MaxNodes.Value) break;
                    if (MaxNodes.HasValue && nodesRemainingForSearch <= 0) break;

                    // Aspiration Windows
                    // Keep alpha above int.MinValue so negamax window inversion (-alpha) cannot overflow.
                    int alpha = int.MinValue + 1;
                    int beta = int.MaxValue;
                    int windowSize = (int)(evaluationScale * AspirationWindowMultiplier); // E.g., 500,000 if evaluationScale is 10000
                    if (currentDepth > 1) {
                        long calcAlpha = (long)previousScore - windowSize;
                        alpha = calcAlpha < int.MinValue + 1 ? int.MinValue + 1 : (int)calcAlpha;
                        
                        long calcBeta = (long)previousScore + windowSize;
                        beta = calcBeta > int.MaxValue ? int.MaxValue : (int)calcBeta;
                    }

                    var scored = new ConcurrentBag<(Move move, int score)>();
                    ConcurrentDictionary<Move, int> evaluationDetails = null;
                    ConcurrentDictionary<int, AIMoveCandidates> aiMoveCandidatesDict = null;
                    int logIdCounter = -1;

                    // Root search with aspiration window
                    void PerformSearch(int currentAlpha, int currentBeta) {
                        scored = new ConcurrentBag<(Move move, int score)>();
                        if (shouldCollectLogDetails) {
                            evaluationDetails = new ConcurrentDictionary<Move, int>();
                            aiMoveCandidatesDict = new ConcurrentDictionary<int, AIMoveCandidates>();
                            logIdCounter = -1;
                        }

                        if (disableParallelRootSearch) {
                            // Serial root mode: avoid root parallel overhead and any timeout behavior.
                            // Decoupled from UseTimeManagement so fixed-depth callers (e.g. arena on GPU)
                            // can opt into parallel root search to overlap evaluator calls. Root moves are
                            // searched with the same window (alpha is not raised between siblings), so serial
                            // and parallel explore the same node set; parallelism is purely a speedup.
                            var helper = new AtaxxThreadHelper(AttaxConstants.BaseConst.BoardSize, AttaxConstants.BaseConst.KillerMoveMaxDepth) {
                                NodesRemaining = nodesRemainingForSearch
                            };

                            float[] rootBatchScores = null;
                            var batchEvaluator = evaluator as IBatchValueEvaluator;
                            if (batchEvaluator != null && (currentDepth == 1 || useMLRootOnly) && allMoves.Count > 1) {
                                var rootBoards = new List<BitboardState>(allMoves.Count);
                                for (int i = 0; i < allMoves.Count; i++) {
                                    var boardAfterMove = searchRootBoard.Clone();
                                    MakeMove(boardAfterMove, allMoves[i], player);
                                    rootBoards.Add(boardAfterMove);
                                }

                                var batchScores = batchEvaluator.EvaluateBatch(rootBoards, SwitchPlayer(player), 0);
                                if (batchScores != null && batchScores.Length == allMoves.Count) {
                                    rootBatchScores = batchScores;
                                    helper.Evals += rootBatchScores.Length;
                                }
                            }

                            int[] rootMoveOrder = null;
                            if (rootBatchScores != null) {
                                rootMoveOrder = Enumerable.Range(0, allMoves.Count)
                                    .OrderByDescending(i => rootBatchScores[i])
                                    .ToArray();
                            }

                            for (int orderedIndex = 0; orderedIndex < allMoves.Count; orderedIndex++) {
                                int moveIndex = rootMoveOrder != null ? rootMoveOrder[orderedIndex] : orderedIndex;
                                var move = allMoves[moveIndex];
                                helper.boardForThread = searchRootBoard.Clone();

                                bool isClone = IsCloneMove(move);
                                int flipped = CountFlippedEnemies(searchRootBoard, move, player);
                                int positionalBonus = 0;
                                int totalPieces = PopCount(searchRootBoard.RedPieces | searchRootBoard.BluePieces);
                                if (totalPieces <= AttaxConstants.ChipsCountConst.lateGame) {
                                    positionalBonus = GetPositionalValue(move.ToX, move.ToY) * AttaxConstants.GetBestMoveConst.centerWeightMultiplier;
                                }

                                MakeMove(helper.boardForThread, move, player);
                                int opponentFlipRisk = CalculateMaxOpponentFlips(helper.boardForThread, player);

                                // Root bonus is on the same scale as strategicScore (heuristic * evaluationScale).
                                RootBonusBreakdown bonus = ComputeRootBonus(isClone, flipped, opponentFlipRisk, positionalBonus, totalPieces, rootAggression);

                                int strategicScore;
                                if (rootBatchScores != null && currentDepth == 1) {
                                    strategicScore = -(int)(rootBatchScores[moveIndex] * evaluationScale);
                                } else {
                                    bool allowNullMoveForSearch = !trainingMode;
                                    bool shouldUseQuiescenceForSearch = !disableQuiescenceSearch && !trainingMode && currentDepth >= 3;

                                    long shiftedAlpha = (long)currentAlpha - bonus.total;
                                    long shiftedBeta = (long)currentBeta - bonus.total;
                                    int stratAlpha = currentAlpha <= int.MinValue + InfinityMargin ? int.MinValue + 1 : (int)Math.Max(shiftedAlpha, (long)int.MinValue + 1);
                                    int stratBeta = currentBeta >= int.MaxValue - InfinityMargin ? int.MaxValue : (int)Math.Min(shiftedBeta, (long)int.MaxValue);

                                    int evalScoreFromAlphaBeta = AlphaBeta(helper, SwitchPlayer(player), currentDepth - 1, -stratBeta, -stratAlpha, 1, allowNullMoveForSearch, shouldUseQuiescenceForSearch, effectiveTimeLimitMs, clock);
                                    strategicScore = -evalScoreFromAlphaBeta;
                                }

                                int finalScore = (int)Math.Clamp((long)strategicScore + bonus.total, (long)int.MinValue + 1, int.MaxValue);

                                var (currentRed, currentBlue) = GetRedAndBlueCounts(helper.boardForThread, player);
                                bool winsNow = (player == AtaxxAIEngine.PlayerColor.Red && currentBlue == 0) || (player == AtaxxAIEngine.PlayerColor.Blue && currentRed == 0);
                                if (winsNow) finalScore = ImmediateWinScore;

                                if (shouldCollectLogDetails) {
                                    int logId = ++logIdCounter;
                                    aiMoveCandidatesDict.TryAdd(logId, new AIMoveCandidates {
                                        move = move,
                                        isClone = isClone,
                                        flipsCount = flipped,
                                        opponentFlipRiskCount = opponentFlipRisk,
                                        evalScore = strategicScore,
                                        positionalBonusScore = bonus.posScaled,
                                        cloneBonusScore = bonus.cloneScaled,
                                        flippedScore = bonus.flipScaled,
                                        opponentFlipRiskScore = bonus.riskScaled,
                                        finalScore = finalScore
                                    });
                                    evaluationDetails.TryAdd(move, logId);
                                }

                                scored.Add((move, finalScore));
                            }

                            var stats = LastSearchStats;
                            stats.Add(helper);
                            LastSearchStats = stats;
                            if (MaxNodes.HasValue) {
                                nodesRemainingForSearch = helper.NodesRemaining;
                            }
                            return;
                        }

                        int parallelism = Math.Min(allMoves.Count, Environment.ProcessorCount);
                        if (_parallelHelpers == null || _parallelHelpers.Length < parallelism) {
                            _parallelHelpers = new AtaxxThreadHelper[parallelism];
                            for (int pi = 0; pi < parallelism; pi++)
                                _parallelHelpers[pi] = new AtaxxThreadHelper(
                                    AttaxConstants.BaseConst.BoardSize,
                                    AttaxConstants.BaseConst.KillerMoveMaxDepth);
                        }
                        int nextHelperIndex = -1;

                        using var cts = new System.Threading.CancellationTokenSource();
                        var parallelOptions = new ParallelOptions { CancellationToken = cts.Token };

                        try {
                            Parallel.ForEach(
                                allMoves,
                                parallelOptions,
                                () => {
                                    var h = _parallelHelpers[
                                        System.Threading.Interlocked.Increment(ref nextHelperIndex) % _parallelHelpers.Length];
                                    h.Nodes = 0; h.QNodes = 0; h.Evals = 0;
                                    h.GetAllValidMovesCalls = 0; h.TTProbes = 0; h.TTHits = 0;
                                    h.NodesRemaining = nodesRemainingForSearch;
                                    return h;
                                },
                                (move, state, ataxxThreadHelper) => {
                                    if (effectiveTimeLimitMs.HasValue && clock.ElapsedMilliseconds > effectiveTimeLimitMs.Value) {
                                        cts.Cancel();
                                        state.Stop();
                                        return ataxxThreadHelper;
                                    }

                                    ataxxThreadHelper.boardForThread = searchRootBoard.Clone();

                                    bool isClone = IsCloneMove(move);
                                    int flipped = CountFlippedEnemies(searchRootBoard, move, player);
                                    int positionalBonus = 0;
                                    int totalPieces = PopCount(searchRootBoard.RedPieces | searchRootBoard.BluePieces);
                                    if (totalPieces <= AttaxConstants.ChipsCountConst.lateGame) {
                                        positionalBonus = GetPositionalValue(move.ToX, move.ToY) * AttaxConstants.GetBestMoveConst.centerWeightMultiplier;
                                    }

                                    MakeMove(ataxxThreadHelper.boardForThread, move, player);
                                    int opponentFlipRisk = CalculateMaxOpponentFlips(ataxxThreadHelper.boardForThread, player);

                                    RootBonusBreakdown bonus = ComputeRootBonus(isClone, flipped, opponentFlipRisk, positionalBonus, totalPieces, rootAggression);

                                    bool allowNullMoveForSearch = !trainingMode;
                                    bool shouldUseQuiescenceForSearch = !disableQuiescenceSearch && !trainingMode && currentDepth >= 3;

                                    long shiftedAlpha = (long)currentAlpha - bonus.total;
                                    long shiftedBeta = (long)currentBeta - bonus.total;
                                    int stratAlpha = currentAlpha <= int.MinValue + InfinityMargin ? int.MinValue + 1 : (int)Math.Max(shiftedAlpha, (long)int.MinValue + 1);
                                    int stratBeta = currentBeta >= int.MaxValue - InfinityMargin ? int.MaxValue : (int)Math.Min(shiftedBeta, (long)int.MaxValue);

                                    int evalScoreFromAlphaBeta = AlphaBeta(ataxxThreadHelper, SwitchPlayer(player), currentDepth - 1, -stratBeta, -stratAlpha, 1, allowNullMoveForSearch, shouldUseQuiescenceForSearch, effectiveTimeLimitMs, clock);
                                    int strategicScore = -evalScoreFromAlphaBeta;

                                    int finalScore = (int)Math.Clamp((long)strategicScore + bonus.total, (long)int.MinValue + 1, int.MaxValue);

                                    var (currentRed, currentBlue) = GetRedAndBlueCounts(ataxxThreadHelper.boardForThread, player);
                                    bool winsNow = (player == AtaxxAIEngine.PlayerColor.Red && currentBlue == 0) || (player == AtaxxAIEngine.PlayerColor.Blue && currentRed == 0);
                                    if (winsNow) finalScore = ImmediateWinScore;

                                    if (shouldCollectLogDetails) {
                                        int logId = System.Threading.Interlocked.Increment(ref logIdCounter);
                                        aiMoveCandidatesDict.TryAdd(logId, new AIMoveCandidates {
                                            move = move,
                                            isClone = isClone,
                                            flipsCount = flipped,
                                            opponentFlipRiskCount = opponentFlipRisk,
                                            evalScore = strategicScore,
                                            positionalBonusScore = bonus.posScaled,
                                            cloneBonusScore = bonus.cloneScaled,
                                            flippedScore = bonus.flipScaled,
                                            opponentFlipRiskScore = bonus.riskScaled,
                                            finalScore = finalScore
                                        });
                                        evaluationDetails.TryAdd(move, logId);
                                    }

                                    scored.Add((move, finalScore));
                                    return ataxxThreadHelper;
                                },
                                (ataxxThreadHelper) => {
                                    lock (this) {
                                        var stats = LastSearchStats;
                                        stats.Add(ataxxThreadHelper);
                                        LastSearchStats = stats;
                                    }
                                }
                            );
                        } catch (OperationCanceledException) {
                            // Expected when the time limit is reached.
                        }
                    }

                    PerformSearch(alpha, beta);

                    if (scored.IsEmpty) break;

                    var currentScoredList = scored.ToList();
                    currentScoredList.Sort((a, b) => b.score.CompareTo(a.score));
                    int bestScoreThisDepth = currentScoredList[0].score;

                    // Check if search failed outside aspiration window
                    if (currentDepth > 1 && (bestScoreThisDepth <= alpha || bestScoreThisDepth >= beta)) {
                        PerformSearch(int.MinValue + 1, int.MaxValue);
                        if (scored.IsEmpty) break;
                        currentScoredList = scored.ToList();
                        currentScoredList.Sort((a, b) => b.score.CompareTo(a.score));
                        bestScoreThisDepth = currentScoredList[0].score;
                    }

                    bestMoveOverall = currentScoredList[0].move;
                    previousScore = bestScoreThisDepth;
                    finalDepthReached = currentDepth;

                    if (temperature > 0 && currentDepth == maxDepthToSearch) {
                        // Apply sampling at the final depth
                        var candidates = currentScoredList.Take(Math.Max(1, topK)).ToList();
                        if (candidates.Count > 1) {
                            double maxScore = candidates.Max(c => c.score);
                            var weights = candidates.Select(c => Math.Exp((c.score - maxScore) / (temperature * TemperatureDivisor))).ToList();

                            double totalWeight = weights.Sum();
                            double r = rng.NextDouble() * totalWeight;
                            double currentWeight = 0;
                            for (int i = 0; i < candidates.Count; i++) {
                                currentWeight += weights[i];
                                if (r <= currentWeight) {
                                    bestMoveOverall = candidates[i].move;
                                    previousScore = candidates[i].score;
                                    break;
                                }
                            }
                        }
                    }

                    if (shouldCollectLogDetails && evaluationDetails != null && aiMoveCandidatesDict != null) {
                        finalEvaluationDetails = evaluationDetails.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                        finalAiMoveCandidatesDict = aiMoveCandidatesDict.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                    }

                    if (ataxxLogger != null) {
                        ataxxLogger.LogInformation(
                            $"Depth {currentDepth} completed. Best move: {bestMoveOverall.FromX},{bestMoveOverall.FromY}->{bestMoveOverall.ToX},{bestMoveOverall.ToY} Score: {bestScoreThisDepth} " +
                            $"Elapsed: {clock.ElapsedMilliseconds}ms, TargetDepth: {aiDepth}, MaxDepth: {maxDepthToSearch}, TimeManagement: {UseTimeManagement}, TimeLimitMs: {(effectiveTimeLimitMs.HasValue ? effectiveTimeLimitMs.Value : -1)}");
                    }

                    // If we found a winning move, no need to search deeper
                    if (bestScoreThisDepth > ForcedWinEarlyExitScore) break;
                }

                clock.Stop();
                if (ataxxLogger != null) {
                    ataxxLogger.LogInformation(
                        $"Search completed in {clock.ElapsedMilliseconds}ms. {LastSearchStats}, TotalNodes: {LastSearchStats.Nodes + LastSearchStats.QNodes}, " +
                        $"NodeBudget: {(MaxNodes.HasValue ? MaxNodes.Value.ToString() : "none")}, NodesRemaining: {(MaxNodes.HasValue ? Math.Max(0, nodesRemainingForSearch).ToString() : "n/a")}");
                }

                if (logCoordinator != null) {
                    if (finalEvaluationDetails == null) finalEvaluationDetails = new Dictionary<Move, int>();
                    if (finalAiMoveCandidatesDict == null) finalAiMoveCandidatesDict = new Dictionary<int, AIMoveCandidates>();

                    if (!finalEvaluationDetails.TryGetValue(bestMoveOverall, out int selectedMoveId) || !finalAiMoveCandidatesDict.ContainsKey(selectedMoveId)) {
                        bool isClone = IsCloneMove(bestMoveOverall);
                        int flipped = CountFlippedEnemies(searchRootBoard, bestMoveOverall, player);
                        int totalPieces = PopCount(searchRootBoard.RedPieces | searchRootBoard.BluePieces);
                        int positionalBonus = totalPieces <= AttaxConstants.ChipsCountConst.lateGame
                            ? GetPositionalValue(bestMoveOverall.ToX, bestMoveOverall.ToY) * AttaxConstants.GetBestMoveConst.centerWeightMultiplier
                            : 0;

                        int opponentFlipRisk = 0;
                        var boardAfterMove = searchRootBoard.Clone();
                        MakeMove(boardAfterMove, bestMoveOverall, player);
                        opponentFlipRisk = CalculateMaxOpponentFlips(boardAfterMove, player);

                        RootBonusBreakdown bonus = ComputeRootBonus(isClone, flipped, opponentFlipRisk, positionalBonus, totalPieces, rootAggression);

                        selectedMoveId = finalAiMoveCandidatesDict.Count == 0 ? 0 : finalAiMoveCandidatesDict.Keys.Max() + 1;
                        finalAiMoveCandidatesDict[selectedMoveId] = new AIMoveCandidates {
                            move = bestMoveOverall,
                            isClone = isClone,
                            flipsCount = flipped,
                            opponentFlipRiskCount = opponentFlipRisk,
                            evalScore = 0,
                            positionalBonusScore = bonus.posScaled,
                            cloneBonusScore = bonus.cloneScaled,
                            flippedScore = bonus.flipScaled,
                            opponentFlipRiskScore = bonus.riskScaled,
                            finalScore = previousScore
                        };
                        finalEvaluationDetails[bestMoveOverall] = selectedMoveId;
                    }

                    var (redCount, blueCount) = GetRedAndBlueCounts(searchRootBoard, PlayerColor.Red);
                    var aiMoveDetails = new AIMoveDetails(
                        finalEvaluationDetails[bestMoveOverall],
                        (int)clock.ElapsedMilliseconds,
                        finalDepthReached,
                        finalAiMoveCandidatesDict
                    );

                    var moveDetails = new MoveDetails(
                        bestMoveOverall,
                        player,
                        IsCloneMove(bestMoveOverall),
                        CountFlippedEnemies(searchRootBoard, bestMoveOverall, player),
                        GetBoardStateString(),
                        isHumanSimulation,
                        aiMoveDetails
                    ) {
                        redCount = redCount,
                        blueCount = blueCount
                    };

                    if (!logCoordinator.GlobalLog.TryGetValue(logCoordinator.TurnIndex, out TurnLog currentTurn)) {
                        currentTurn = new TurnLog(useOrthogonalOnlyCapture);
                        logCoordinator.GlobalLog[logCoordinator.TurnIndex] = currentTurn;
                    }

                    if (isHumanSimulation) {
                        currentTurn.playerMove = moveDetails;
                    } else {
                        currentTurn.opponentMove = moveDetails;
                        logCoordinator.TurnIndex++;
                    }
                }

                return bestMoveOverall;
            } catch (Exception ex) {
                ataxxLogger?.LogError(ex.ToString());
                return default;
            }
        }


        // This function uses a "negamax" approach, which is concise and robust way to implement minimax with alpha-beta pruning.
        // It always evaluates the score from the perspective of the current 'player'.
        private int AlphaBeta(AtaxxThreadHelper ataxxThreadHelper, PlayerColor player, int depth, int alpha, int beta, int ply, bool allowNullMove, bool shouldUseQuiescence, long? timeLimitMs, System.Diagnostics.Stopwatch clock) {
            ataxxThreadHelper.Nodes++;
            if (ataxxThreadHelper.NodesRemaining >= 0) {
                ataxxThreadHelper.NodesRemaining--;
                if (ataxxThreadHelper.NodesRemaining <= 0) {
                    return Evaluate(ataxxThreadHelper.boardForThread, player, ataxxThreadHelper);
                }
            }
            if (timeLimitMs.HasValue && clock != null && clock.ElapsedMilliseconds > timeLimitMs.Value) {
                return Evaluate(ataxxThreadHelper.boardForThread, player, ataxxThreadHelper);
            }
            int originalAlpha = alpha;
            ulong hash = ataxxThreadHelper.boardForThread.ZobristHash;

            ataxxThreadHelper.TTProbes++;
            int ttIndex = (int)(hash % (ulong)AtaxxThreadHelper.TTSize);
            TTEntry entry = ataxxThreadHelper.transpositionTable[ttIndex];
            Move ttBestMove = default;

            if (entry.key == hash) {
                ataxxThreadHelper.TTHits++;
                ttBestMove = entry.bestMove;
                if (entry.depth >= depth) {
                    if (entry.flag == (byte)NodeType.Exact) {
                        return entry.score;
                    }
                    if (entry.flag == (byte)NodeType.LowerBound && entry.score >= beta) {
                        return entry.score;
                    }
                    if (entry.flag == (byte)NodeType.UpperBound && entry.score <= alpha) {
                        return entry.score;
                    }
                }
            }

            if (IsGameOver(ataxxThreadHelper.boardForThread)) {
                // Decisive terminal scoring: win/loss dominates any heuristic, and ply-adjusted
                // so the search prefers faster wins / slower losses. Models the Ataxx end rule
                // that a stuck side surrenders its remaining empties to the side that can move.
                var board = ataxxThreadHelper.boardForThread;
                var (me, opp) = GetRedAndBlueCounts(board, player);
                int emptyNow = PopCount(board.EmptySquares());
                bool meCanMove = HasAnyLegalMove(board, player);
                bool oppCanMove = HasAnyLegalMove(board, SwitchPlayer(player));
                if (!meCanMove && oppCanMove) opp += emptyNow;
                else if (meCanMove && !oppCanMove) me += emptyNow;

                if (me > opp) return TerminalWinScore - ply;
                if (me < opp) return -TerminalWinScore + ply;
                return 0;
            }

            if (depth <= 0) {
                if (shouldUseQuiescence)
                    return Quiescence(ataxxThreadHelper, player, alpha, beta, 2, ply, timeLimitMs, clock);
                else
                    return Evaluate(ataxxThreadHelper.boardForThread, player, ataxxThreadHelper);
            }

            // Null-move pruning is unsound in Ataxx endgames (zugzwang: being forced to move can
            // be strictly bad), so restrict it to the midgame with a non-trivial piece count.
            // Gated by disableNullMovePruning so depth-N play can be A/B tested without recompiling.
            ulong playerPiecesForNull = (player == PlayerColor.Red) ? ataxxThreadHelper.boardForThread.RedPieces : ataxxThreadHelper.boardForThread.BluePieces;
            bool nullMoveSafe = !disableNullMovePruning
                && PopCount(ataxxThreadHelper.boardForThread.EmptySquares()) > 14
                && PopCount(playerPiecesForNull) >= 3;
            if (allowNullMove && nullMoveSafe && depth >= 3 && HasAnyLegalMove(ataxxThreadHelper.boardForThread, player)) {
                int staticEval = Evaluate(ataxxThreadHelper.boardForThread, player, ataxxThreadHelper);
                if (staticEval >= beta) {
                    ataxxThreadHelper.boardForThread.ZobristHash ^= ZobristHasher.GetSideToMoveKey();
                    int nullMoveReduction = 2;
                    int score = -AlphaBeta(ataxxThreadHelper, SwitchPlayer(player), depth - 1 - nullMoveReduction, -beta, -beta + 1, ply + 1, false, shouldUseQuiescence, timeLimitMs, clock);
                    ataxxThreadHelper.boardForThread.ZobristHash ^= ZobristHasher.GetSideToMoveKey();
                    if (score >= beta) {
                        return beta;
                    }
                }
            }

            int moveCount = GetOrderedMoves(ataxxThreadHelper, player, ply, ataxxThreadHelper.killerMovesForThread, ttBestMove);
            if (moveCount == 0) return Evaluate(ataxxThreadHelper.boardForThread, player, ataxxThreadHelper);

            int bestScore = int.MinValue;
            Move bestMove = default;

            for (int i = 0; i < moveCount; i++) {
                var move = ataxxThreadHelper.perPlyMoveBuffer[ply, i];
                var undoInfo = MakeMoveFast(ataxxThreadHelper.boardForThread, move, player);

                int score;
                bool recursiveAllowNullMove = trainingMode ? allowNullMove : true;
                if (i == 0) {
                    score = -AlphaBeta(ataxxThreadHelper, SwitchPlayer(player), depth - 1, -beta, -alpha, ply + 1, recursiveAllowNullMove, shouldUseQuiescence, timeLimitMs, clock);
                } else {
                    score = -AlphaBeta(ataxxThreadHelper, SwitchPlayer(player), depth - 1, -alpha - 1, -alpha, ply + 1, recursiveAllowNullMove, shouldUseQuiescence, timeLimitMs, clock);
                    if (score > alpha && score < beta) {
                        score = -AlphaBeta(ataxxThreadHelper, SwitchPlayer(player), depth - 1, -beta, -alpha, ply + 1, recursiveAllowNullMove, shouldUseQuiescence, timeLimitMs, clock);
                    }
                }
                UnmakeMove(ataxxThreadHelper.boardForThread, move, player, undoInfo);

                if (score > bestScore) {
                    bestScore = score;
                    bestMove = move;
                }
                if (score > alpha) alpha = score;

                if (alpha >= beta) {
                    bool isCapture = undoInfo.FlippedPiecesMask != 0;
                    if (!isCapture) {
                        if (ply < AttaxConstants.BaseConst.KillerMoveMaxDepth) {
                            ataxxThreadHelper.killerMovesForThread[ply, 1] = ataxxThreadHelper.killerMovesForThread[ply, 0];
                            ataxxThreadHelper.killerMovesForThread[ply, 0] = move;
                        }
                        ataxxThreadHelper.historyHeuristic[GetBitIndex(move.FromX, move.FromY), GetBitIndex(move.ToX, move.ToY)] += depth * depth;
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
                key = hash,
                bestMove = bestMove,
                score = bestScore,
                depth = (byte)depth,
                flag = (byte)nodeTypeToStore,
                age = (byte)this.currentSearchAge
            };

            // Replacement strategy: depth-preferred
            if (entry.key != hash || depth >= entry.depth || entry.age != this.currentSearchAge) {
                ataxxThreadHelper.transpositionTable[ttIndex] = newEntry;
            }

            return bestScore;
        }

        public UndoMoveInfo MakeMoveFast(BitboardState boardState, Move move, PlayerColor player) {
            var undoInfo = new UndoMoveInfo { PreviousZobristHash = boardState.ZobristHash };

            int fromIndex = GetBitIndex(move.FromX, move.FromY);
            int toIndex = GetBitIndex(move.ToX, move.ToY);
            ulong fromMask = 1UL << fromIndex;
            ulong toMask = 1UL << toIndex;

            ref ulong playerPieces = ref (player == PlayerColor.Red ? ref boardState.RedPieces : ref boardState.BluePieces);
            ref ulong opponentPieces = ref (player == PlayerColor.Red ? ref boardState.BluePieces : ref boardState.RedPieces);

            boardState.ZobristHash ^= ZobristHasher.GetSideToMoveKey();

            bool isClone = IsCloneMove(move);
            if (!isClone) {
                playerPieces &= ~fromMask;
                boardState.ZobristHash ^= ZobristHasher.GetPieceKey(move.FromX, move.FromY, GetPieceTypeIndex(player));
            }

            playerPieces |= toMask;
            boardState.ZobristHash ^= ZobristHasher.GetPieceKey(move.ToX, move.ToY, GetPieceTypeIndex(player));

            ulong attackMask = useOrthogonalOnlyCapture ? BoardLookup.OrthogonalStepMoves[toIndex] : BoardLookup.SingleStepMoves[toIndex];
            ulong flippedPieces = opponentPieces & attackMask;
            undoInfo.FlippedPiecesMask = flippedPieces;

            if (flippedPieces > 0) {
                playerPieces |= flippedPieces;
                opponentPieces &= ~flippedPieces;

                ulong remainingFlipped = flippedPieces;
                while (remainingFlipped > 0) {
                    int flippedIndex = TrailingZeroCount(remainingFlipped);
                    int flippedX = flippedIndex % AttaxConstants.BaseConst.BoardSize;
                    int flippedY = flippedIndex / AttaxConstants.BaseConst.BoardSize;

                    boardState.ZobristHash ^= ZobristHasher.GetPieceKey(flippedX, flippedY, GetPieceTypeIndex(SwitchPlayer(player)));
                    boardState.ZobristHash ^= ZobristHasher.GetPieceKey(flippedX, flippedY, GetPieceTypeIndex(player));

                    remainingFlipped &= remainingFlipped - 1;
                }
            }
            return undoInfo;
        }

        /// <summary>
        /// Applies a move to the given bitboard state, updating player pieces
        /// and the Zobrist hash incrementally.
        /// </summary>
        /// <param name=\"boardState\">The bitboard state to modify.</param>
        /// <param name=\"move\">The move to apply.</param>
        /// <param name=\"player\">The player making the move.</param>
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
                boardState.ZobristHash ^= ZobristHasher.GetSideToMoveKey();

                // Handle the move itself (clone vs. jump).
                bool isClone = IsCloneMove(move);
                if (!isClone) // It's a jump
                {
                    playerPieces &= ~fromMask; // Remove piece from the 'from' square.
                    boardState.ZobristHash ^= ZobristHasher.GetPieceKey(move.FromX, move.FromY, GetPieceTypeIndex(player)); // XOR out the old piece position
                }

                playerPieces |= toMask; // Place piece at the 'to' square.
                boardState.ZobristHash ^= ZobristHasher.GetPieceKey(move.ToX, move.ToY, GetPieceTypeIndex(player)); // XOR in the new piece position

                // Handle all captures at once.
                // Select the correct attack mask based on the capture rule.
                ulong attackMask = useOrthogonalOnlyCapture ? BoardLookup.OrthogonalStepMoves[toIndex] : BoardLookup.SingleStepMoves[toIndex];

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
                        moveResult.FlippedPieces.Add((flippedIndex % AttaxConstants.BaseConst.BoardSize, flippedIndex / AttaxConstants.BaseConst.BoardSize));
                        int flippedX = flippedIndex % AttaxConstants.BaseConst.BoardSize;
                        int flippedY = flippedIndex / AttaxConstants.BaseConst.BoardSize;

                        // XOR out the key for the opponent piece being removed.
                        boardState.ZobristHash ^= ZobristHasher.GetPieceKey(flippedX, flippedY, GetPieceTypeIndex(SwitchPlayer(player)));
                        // XOR in the key for our piece being added in its place.
                        boardState.ZobristHash ^= ZobristHasher.GetPieceKey(flippedX, flippedY, GetPieceTypeIndex(player));

                        remainingFlipped &= remainingFlipped - 1;
                    }
                }
                moveResult.UndoInfo = undoInfo;
                return moveResult;
            } catch (Exception ex) {
                ataxxLogger?.LogError(ex.ToString()); // Using ToString() for more details
                return default;
            }
        }
        public void UpdateZobristForMove(BitboardState boardState, Move move, PlayerColor player) {
            // Flip the side-to-move key
            boardState.ZobristHash ^= ZobristHasher.GetSideToMoveKey();

            // Handle the moving piece
            bool isClone = IsCloneMove(move);
            if (!isClone) // It's a jump, so remove the piece from the 'from' square
            {
                boardState.ZobristHash ^= ZobristHasher.GetPieceKey(move.FromX, move.FromY, GetPieceTypeIndex(player));
            }
            // Place piece at the 'to' square
            boardState.ZobristHash ^= ZobristHasher.GetPieceKey(move.ToX, move.ToY, GetPieceTypeIndex(player));

            // Handle captures
            int toIndex = GetBitIndex(move.ToX, move.ToY);
            ulong attackMask = useOrthogonalOnlyCapture ? BoardLookup.OrthogonalStepMoves[toIndex] : BoardLookup.SingleStepMoves[toIndex];

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
                    boardState.ZobristHash ^= ZobristHasher.GetPieceKey(flippedX, flippedY, GetPieceTypeIndex(SwitchPlayer(player)));
                    // XOR in the key for our piece that replaces it.
                    boardState.ZobristHash ^= ZobristHasher.GetPieceKey(flippedX, flippedY, GetPieceTypeIndex(player));

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
        private int Evaluate(BitboardState board, PlayerColor player, AtaxxThreadHelper helper = null) {
            if (helper != null) helper.Evals++;
            // Use root-only heuristic evaluator in search when configured.
            return (int)(searchEvaluator.Evaluate(board, player, 0) * evaluationScale);
        }

        // Quiescence search using negamax. Uses per-ply buffers — zero GC allocation.
        // 'ply' is threaded through so each recursive level writes to its own buffer slot.
        private int Quiescence(AtaxxThreadHelper helper, PlayerColor player, int alpha, int beta, int depth, int ply, long? timeLimitMs = null, System.Diagnostics.Stopwatch clock = null) {
            helper.QNodes++;
            if (helper.NodesRemaining >= 0) {
                helper.NodesRemaining--;
                if (helper.NodesRemaining <= 0)
                    return Evaluate(helper.boardForThread, player, helper);
            }
            if (timeLimitMs.HasValue && clock != null && clock.ElapsedMilliseconds > timeLimitMs.Value)
                return Evaluate(helper.boardForThread, player, helper);

            int standPatScore = Evaluate(helper.boardForThread, player, helper);
            if (standPatScore >= beta) return beta;
            if (standPatScore > alpha) alpha = standPatScore;
            if (depth <= 0 || IsGameOver(helper.boardForThread)) return standPatScore;

            int noisyCount = GetNoisyMovesIntoBuffer(helper, player, ply);
            for (int i = 0; i < noisyCount; i++) {
                var move = helper.perPlyMoveBuffer[ply, i];
                var undoInfo = MakeMoveFast(helper.boardForThread, move, player);
                int score = -Quiescence(helper, SwitchPlayer(player), -beta, -alpha, depth - 1, ply + 1, timeLimitMs, clock);
                UnmakeMove(helper.boardForThread, move, player, undoInfo);
                if (score >= beta) return beta;
                if (score > alpha) alpha = score;
            }
            return alpha;
        }

        #endregion



        #region helpers 
        // Fills helper.perPlyMoveBuffer[ply] with only capturing (noisy) moves. Returns count.
        private int GetNoisyMovesIntoBuffer(AtaxxThreadHelper helper, PlayerColor player, int ply) {
            int rawCount = FillMoves(helper.boardForThread, player, helper.rawMoveBuffer, helper);
            int noisyCount = 0;
            for (int i = 0; i < rawCount; i++) {
                if (CountFlippedEnemies(helper.boardForThread, helper.rawMoveBuffer[i], player) > 0)
                    helper.perPlyMoveBuffer[ply, noisyCount++] = helper.rawMoveBuffer[i];
            }
            helper.perPlyMoveCount[ply] = noisyCount;
            return noisyCount;
        }
        private bool IsCaptureMove(BitboardState boardState, Move move, PlayerColor player) {
            ulong opponentPieces = (player == PlayerColor.Red) ? boardState.BluePieces : boardState.RedPieces;
            int toIndex = GetBitIndex(move.ToX, move.ToY);
            ulong attackMask = useOrthogonalOnlyCapture ? BoardLookup.OrthogonalStepMoves[toIndex] : BoardLookup.SingleStepMoves[toIndex];
            return (attackMask & opponentPieces) != 0;
        }

        // De Bruijn lookup table for 64-bit TrailingZeroCount.


        public static int PopCount(ulong value) => BitboardOps.PopCount(value);
        private ulong GetMoveDestinationsBitboard(BitboardState board, PlayerColor player)
            => BitboardFeatures.GetMoveDestinationsBitboard(board, player);
        private static int TrailingZeroCount(ulong value) => BitboardOps.TrailingZeroCount(value);
        public bool IsOrthogonal(int x1, int y1, int x2, int y2) => (x1 == x2 && Math.Abs(y1 - y2) == 1) || (y1 == y2 && Math.Abs(x1 - x2) == 1);
        public bool IsAdjacent(int x1, int y1, int x2, int y2) => Math.Abs(x1 - x2) <= 1 && Math.Abs(y1 - y2) <= 1 && !(x1 == x2 && y1 == y2);
        public static PlayerColor SwitchPlayer(PlayerColor current) => current == AtaxxAIEngine.PlayerColor.Red ? AtaxxAIEngine.PlayerColor.Blue : current == AtaxxAIEngine.PlayerColor.Blue ? AtaxxAIEngine.PlayerColor.Red : AtaxxAIEngine.PlayerColor.None;

        private int GetPieceTypeIndex(PlayerColor color) {
            switch (color) {
                case PlayerColor.Red: return 0;
                case PlayerColor.Blue: return 1;
                case PlayerColor.Blocked: return 2;
                default: return -1; // Should not happen for actual pieces
            }
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
        }
        public bool IsCloneMove(Move move) => Math.Abs(move.ToX - move.FromX) <= 1 && Math.Abs(move.ToY - move.FromY) <= 1;

        /// <summary>
        /// Computes a Zobrist hash from a bitboard state from scratch.
        /// This is used to get the hash for the initial board position.
        /// </summary>
        public ulong ComputeZobristHash(BitboardState boardState, PlayerColor currentPlayer) {
            return ZobristHasher.ComputeHash(boardState, currentPlayer);
        }
        #endregion

        #region aihelpers (Evaluation Components - modified for toggles)

        // Writes ordered moves for 'ply' into helper.perPlyMoveBuffer[ply]. Returns count.
        // Replaces the old List-based GetOrderedMoves; zero GC allocation on the hot search path.
        private int GetOrderedMoves(AtaxxThreadHelper helper, PlayerColor player, int ply, Move[,] killerMoves, Move ttBestMove) {
            int rawCount = FillMoves(helper.boardForThread, player, helper.rawMoveBuffer, helper);
            if (rawCount == 0) { helper.perPlyMoveCount[ply] = 0; return 0; }
            if (rawCount == 1) {
                helper.perPlyMoveBuffer[ply, 0] = helper.rawMoveBuffer[0];
                helper.perPlyMoveCount[ply] = 1;
                return 1;
            }

            // Pass 1: compute flips for every raw move and store (flips, rawIndex) in sortScratch.
            for (int i = 0; i < rawCount; i++)
                helper.sortScratch[i] = (CountFlippedEnemies(helper.boardForThread, helper.rawMoveBuffer[i], player), i);

            // Partition sortScratch: captures (flips > 0) to the front, non-captures after.
            int capCount = 0;
            for (int i = 0; i < rawCount; i++) {
                if (helper.sortScratch[i].flips > 0) {
                    var tmp = helper.sortScratch[capCount];
                    helper.sortScratch[capCount] = helper.sortScratch[i];
                    helper.sortScratch[i] = tmp;
                    capCount++;
                }
            }
            // Insertion-sort the capture region by flip count descending (usually ≤ 8 entries).
            for (int i = 1; i < capCount; i++) {
                var key = helper.sortScratch[i];
                int j = i - 1;
                while (j >= 0 && helper.sortScratch[j].flips < key.flips) {
                    helper.sortScratch[j + 1] = helper.sortScratch[j];
                    j--;
                }
                helper.sortScratch[j + 1] = key;
            }

            Move k1 = ply < AttaxConstants.BaseConst.KillerMoveMaxDepth ? killerMoves[ply, 0] : default;
            Move k2 = ply < AttaxConstants.BaseConst.KillerMoveMaxDepth ? killerMoves[ply, 1] : default;
            bool hasTTMove = ttBestMove != default;

            // Pass 2: write ordered moves into perPlyMoveBuffer[ply].
            int destCount = 0;

            // 1. TT best move first (if it exists in the raw list).
            if (hasTTMove) {
                for (int i = 0; i < rawCount; i++) {
                    if (helper.rawMoveBuffer[i] == ttBestMove) {
                        helper.perPlyMoveBuffer[ply, destCount++] = ttBestMove;
                        break;
                    }
                }
            }

            // 2. Captures (sorted by flip count desc).
            for (int i = 0; i < capCount; i++) {
                Move m = helper.rawMoveBuffer[helper.sortScratch[i].moveIdx];
                if (hasTTMove && m == ttBestMove) continue;
                helper.perPlyMoveBuffer[ply, destCount++] = m;
            }

            // 3. Killer moves (non-capture).
            for (int i = capCount; i < rawCount; i++) {
                Move m = helper.rawMoveBuffer[helper.sortScratch[i].moveIdx];
                if (hasTTMove && m == ttBestMove) continue;
                if (m == k1 || m == k2)
                    helper.perPlyMoveBuffer[ply, destCount++] = m;
            }

            // 4. History moves (non-capture, not killer, history score > 0).
            for (int i = capCount; i < rawCount; i++) {
                Move m = helper.rawMoveBuffer[helper.sortScratch[i].moveIdx];
                if (hasTTMove && m == ttBestMove) continue;
                if (m == k1 || m == k2) continue;
                if (helper.historyHeuristic[GetBitIndex(m.FromX, m.FromY), GetBitIndex(m.ToX, m.ToY)] > 0)
                    helper.perPlyMoveBuffer[ply, destCount++] = m;
            }

            // 5. Remaining quiet moves.
            for (int i = capCount; i < rawCount; i++) {
                Move m = helper.rawMoveBuffer[helper.sortScratch[i].moveIdx];
                if (hasTTMove && m == ttBestMove) continue;
                if (m == k1 || m == k2) continue;
                if (helper.historyHeuristic[GetBitIndex(m.FromX, m.FromY), GetBitIndex(m.ToX, m.ToY)] > 0) continue;
                helper.perPlyMoveBuffer[ply, destCount++] = m;
            }

            helper.perPlyMoveCount[ply] = destCount;
            return destCount;
        }
        // Scaled breakdown of the root-only move-selection bonus. All components are already
        // multiplied by evaluationScale so they share the scale of strategicScore, and 'total'
        // satisfies the identity total == clone + flip - risk + pos (used for log reconciliation).
        private struct RootBonusBreakdown {
            public int cloneScaled, flipScaled, riskScaled, posScaled, total;
        }

        // Builds the root bonus in heuristic points (then *evaluationScale). 'aggression' (>=1)
        // is the AI-perspective comeback factor: when behind, captures matter more, risk less,
        // and passive clones less. This lives ONLY at the root, so it cannot break the negamax
        // antisymmetry of the leaf evaluator.
        private RootBonusBreakdown ComputeRootBonus(bool isClone, int flipped, int opponentFlipRisk, int positionalBonus, int totalPieces, double aggression) {
            double clonePts = isClone ? AttaxConstants.GetCloneBonusPoints(totalPieces) / aggression : 0.0;
            // A jump that neither captures nor grows is almost always inferior to a clone; discourage
            // it on the growth axis so the engine prefers clones/captures over pure repositioning.
            if (!isClone && flipped == 0) clonePts -= AttaxConstants.RootBonusConst.nonCapturingJumpPenalty;
            double flipPts = flipped * AttaxConstants.RootBonusConst.flipPoints * aggression;
            double riskPts = opponentFlipRisk * AttaxConstants.RootBonusConst.riskPoints / aggression;
            double posPts = positionalBonus * AttaxConstants.RootBonusConst.positionalPoints;

            var b = new RootBonusBreakdown {
                cloneScaled = (int)(clonePts * evaluationScale),
                flipScaled = (int)(flipPts * evaluationScale),
                riskScaled = (int)(riskPts * evaluationScale),
                posScaled = (int)(posPts * evaluationScale)
            };
            b.total = b.cloneScaled + b.flipScaled - b.riskScaled + b.posScaled;
            return b;
        }

        private int GetPositionalValue(int x, int y) {
            int[,] positionWeight = {
            {1,2,3,3,3,2,1},{2,3,4,4,4,3,2},{3,4,5,6,5,4,3},
            {3,4,6,7,6,4,3},{3,4,5,6,5,4,3},{2,3,4,4,4,3,2},{1,2,3,3,3,2,1}
        };
            if (y >= 0 && y < AttaxConstants.BaseConst.BoardSize && x >= 0 && x < AttaxConstants.BaseConst.BoardSize) return positionWeight[y, x];
            return 0;
        }

        private int GetPotentialMobilityInternal(BitboardState boardState, ulong destinationSquares)
            => BitboardFeatures.GetPotentialMobilityInternal(boardState, destinationSquares);
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

       /* private bool HasAdjacentEmpty(BitboardState boardState, int x, int y)
            => BitboardFeatures.HasAdjacentEmpty(boardState, x, y);

        private bool IsStable(BitboardState boardState, int x, int y, PlayerColor player)
            => BitboardFeatures.IsStable(boardState, x, y, player);*/

        public int GetStabilityBonus(BitboardState boardState, PlayerColor player)
            => BitboardFeatures.GetStabilityBonus(boardState, player);

        public int GetCenterControl(BitboardState boardState, PlayerColor player)
            => BitboardFeatures.GetCenterControl(boardState, player);
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
            ulong originalPlayerPieces = (originalPlayer == PlayerColor.Red) ? boardStateAfterMove.RedPieces : boardStateAfterMove.BluePieces;
            ulong emptySquares = boardStateAfterMove.EmptySquares();

            ulong allDestinations = 0;
            ulong remainingPieces = opponentPieces;
            while (remainingPieces > 0) {
                int fromIndex = TrailingZeroCount(remainingPieces);
                allDestinations |= (BoardLookup.SingleStepMoves[fromIndex] | BoardLookup.TwoStepMoves[fromIndex]);
                remainingPieces &= remainingPieces - 1;
            }

            int maxFlip = 0;
            ulong validDestinations = allDestinations & emptySquares;
            while (validDestinations > 0) {
                int toIndex = TrailingZeroCount(validDestinations);
                ulong attackMask = useOrthogonalOnlyCapture ? BoardLookup.OrthogonalStepMoves[toIndex] : BoardLookup.SingleStepMoves[toIndex];
                
                int flips = PopCount(originalPlayerPieces & attackMask);
                if (flips > maxFlip) {
                    maxFlip = flips;
                }
                validDestinations &= validDestinations - 1;
            }
            
            return maxFlip;
        }


        #endregion


        public void Dispose() {
            _parallelHelpers = null; // release 32 MB × N LOH TT arrays back to GC
            if (searchEvaluator != null && !ReferenceEquals(searchEvaluator, evaluator) && searchEvaluator is IDisposable disposableSearchEvaluator) {
                disposableSearchEvaluator.Dispose();
            }

            if (evaluator is IDisposable disposableEvaluator) {
                disposableEvaluator.Dispose();
            }
        }
    }
}
