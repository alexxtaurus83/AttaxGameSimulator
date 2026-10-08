using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Attax.Core.Utils;
using ZLinq;
using static Attax.Core.AILogCoordinator;

namespace Attax.Core {
    public partial class AtaxxAIEngine : IDisposable {

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

        public int aiDepth { get; set; }
        public AILogCoordinator logCoordinator { get; set; }

        private IValueEvaluator evaluator;
        private float evaluationScale;
        private bool trainingMode;
        private bool disableParallelRootSearch;
        private bool disableQuiescenceSearch;
        private bool disableNullMovePruning;
        private bool disableRandomRootTies;
        //private bool logGenMode;
        private bool iterativeDeepeningInTraining;
        private double rootBonusScale = 1.0;
        private double temperatureUnit = TemperatureDivisor;
        private int? aspirationWindowOverride;
        private bool throwOnSearchError;
        private CompiledEngineParams prm;
        private readonly Random rng;
        // Separate stream for root tie-breaks: drawing from it never shifts the main rng (blocked cells, temperature sampling), and a fixed Seed stays reproducible.
        private readonly Random tieRng;
        // Pre-allocated parallel helper pool. Lazily initialized on first parallel search;
        // reused across all subsequent calls so the 32 MB TT per helper is only allocated once.
        private AtaxxThreadHelper[] _parallelHelpers;
        // Private serial-search helper, owned by this engine only (see GetBestMove).
        private AtaxxThreadHelper _serialHelper;
        // Hint searches (Search with options) own separate helpers, so they never read, clear or fill the AI's persistent search memory.
        // Lazily allocated (about 32 MB per helper, like the AI's), released by Dispose or ReleaseHintMemory.
        private AtaxxThreadHelper[] _hintHelpers;
        private AtaxxThreadHelper _hintSerialHelper;

        // Returns the helper pool for the AI (hint == false) or for hint searches, growing it when needed. Growing replaces the whole pool.
        private AtaxxThreadHelper[] EnsureHelperPool(bool hint, int needed) {
            var pool = hint ? _hintHelpers : _parallelHelpers;
            if (pool == null || pool.Length < needed) {
                pool = new AtaxxThreadHelper[needed];
                for (int pi = 0; pi < needed; pi++)
                    pool[pi] = new AtaxxThreadHelper(AttaxConstants.BaseConst.BoardSize, AttaxConstants.BaseConst.KillerMoveMaxDepth);
                if (hint) _hintHelpers = pool; else _parallelHelpers = pool;
            }
            return pool;
        }

        /// <summary>Frees the memory held by hint searches. Not thread-safe: call only while no search is running.</summary>
        public void ReleaseHintMemory() {
            _hintHelpers = null;
            _hintSerialHelper = null;
        }
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

        public enum NodeType { Exact, LowerBound, UpperBound }

        public struct AIEngineConfig {
            public bool UseTimeManagement;
            public int TimeManagedExtraDepth;
            public long SearchTimeLimitMs;
            public int? Seed;

            public int AiDepth;
            public bool TrainingMode;
            public bool UseMLEvaluation;
            public float EvaluationScale;
            public bool DisableParallelRootSearch;
            public bool DisableQuiescenceSearch;
            public bool DisableNullMovePruning;
            // Root ties: when several root moves share the best score, the engine plays one of them at random (default) instead of always the first
            // generated one. They are equally good for the search, so this only varies play. Set true for a deterministic engine (tests, teacher labels,
            // A/B runs). The draw has its own stream seeded from Seed, so it never shifts blocked-cell placement or temperature sampling.
            public bool DisableRandomRootTies;
            //public bool LogGenMode;
            // Opt-in (default false keeps legacy behavior): in TrainingMode, iterate depth 1..AiDepth.
            public bool IterativeDeepeningInTraining;

            // --- Score-scale overrides for model-value search. All null = legacy classic behaviour. ---
            // Multiplies every root bonus component (clone/flip/risk/positional). Legacy 1.0. The bonuses are
            // authored in heuristic points (opening clone bonus 6 => 60,000 at scale 10,000), which dwarfs a
            // model value in [-1,1] (+-10,000), so model search should pass a calibrated value (0 disables).
            public float? RootBonusScale;
            // Score units per unit of temperature in root sampling: weight = exp((score - max) / (temp * unit)).
            // Legacy 10.0 (a heuristic-point scale). For model search use ~EvaluationScale so temp is in value units.
            public float? SelectionTemperatureUnit;
            // Aspiration half-window in score units. Legacy EvaluationScale * 50 (effectively unbounded for a
            // model whose whole range is EvaluationScale).
            public int? AspirationWindow;
            // When true, an exception inside GetBestMove is rethrown instead of being logged and turned into
            // "no move" (default(Move)), which callers treat as a pass / random move. Required for data generation
            // so a broken model can never silently produce training data.
            public bool ThrowOnSearchError;

        }

        /// <summary>Per-root-move scores of the last completed search iteration (see CollectRootScores).</summary>
        public struct RootScore {
            public Move Move;
            /// <summary>Score from the search/evaluator alone, in engine score units.</summary>
            public int Strategic;
            /// <summary>Root bonus in engine score units (after RootBonusScale).</summary>
            public int Bonus;
            /// <summary>Strategic + Bonus (or ImmediateWinScore), the value moves are ranked by.</summary>
            public int Final;
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
        /// <summary>
        /// There is no engine without parameters: <paramref name="engineParams"/> (the deserialised engine-params.json) is mandatory, validated
        /// here (ArgumentException lists every problem) and copied, so later changes to the object do not affect a running engine.
        /// </summary>
        /// <param name="evaluator">Leaf evaluator. null = the heuristic evaluator built from <paramref name="engineParams"/>. A model evaluator
        /// (IValueEvaluator, optionally IBatchValueEvaluator) REPLACES the heuristic at every leaf; the root and search groups of the parameters
        /// still apply. Passing a HeuristicEvaluator is rejected: pass null, so the evaluator and the root/search groups cannot disagree.</param>
        public AtaxxAIEngine(
            EngineParams engineParams,
            AIEngineConfig config = default,
            IValueEvaluator evaluator = null,
            ILogSink ataxxLogger = null,
            AILogCoordinator coordinator = null
        ) {
            if (engineParams == null) throw new ArgumentNullException(nameof(engineParams), "The engine needs its parameters: load engine-params.json.");
            this.prm = engineParams.Compile();
            if (evaluator is HeuristicEvaluator)
                throw new ArgumentException("Pass null as the evaluator to use the heuristic evaluator (it is built from the engine parameters).", nameof(evaluator));
            this.evaluator = evaluator ?? new HeuristicEvaluator(this.prm);
            this.ataxxLogger = ataxxLogger;
            this.logCoordinator = coordinator;

            // Apply config
            this.UseTimeManagement = config.UseTimeManagement;
            this.TimeManagedExtraDepth = config.TimeManagedExtraDepth;
            this.SearchTimeLimitMs = config.SearchTimeLimitMs;
            this.seed = config.Seed;

            this.aiDepth = config.AiDepth;
            this.trainingMode = config.TrainingMode;
            this.evaluationScale = config.EvaluationScale != 0 ? config.EvaluationScale : DefaultEvaluationScale;
            this.disableParallelRootSearch = config.DisableParallelRootSearch;
            this.disableQuiescenceSearch = config.DisableQuiescenceSearch;
            this.disableNullMovePruning = config.DisableNullMovePruning;
            this.disableRandomRootTies = config.DisableRandomRootTies;
            //this.logGenMode = config.LogGenMode;
            this.iterativeDeepeningInTraining = config.IterativeDeepeningInTraining;
            if (config.RootBonusScale.HasValue) {
                if (float.IsNaN(config.RootBonusScale.Value) || config.RootBonusScale.Value < 0f)
                    throw new ArgumentException("RootBonusScale must be >= 0.", nameof(config));
                this.rootBonusScale = config.RootBonusScale.Value;
            }
            if (config.SelectionTemperatureUnit.HasValue) {
                if (!(config.SelectionTemperatureUnit.Value > 0f))
                    throw new ArgumentException("SelectionTemperatureUnit must be > 0.", nameof(config));
                this.temperatureUnit = config.SelectionTemperatureUnit.Value;
            }
            if (config.AspirationWindow.HasValue) {
                if (config.AspirationWindow.Value <= 0)
                    throw new ArgumentException("AspirationWindow must be > 0.", nameof(config));
                this.aspirationWindowOverride = config.AspirationWindow.Value;
            }
            this.throwOnSearchError = config.ThrowOnSearchError;
            this.rng = config.Seed.HasValue ? new Random(config.Seed.Value) : new Random();
            this.tieRng = config.Seed.HasValue ? new Random(unchecked(config.Seed.Value * 31 + 17)) : new Random();

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
        public bool IsGameOver(BitboardState boardState) => AttaxRules.IsGameOver(boardState);

        /// <summary>
        /// Single source of truth for the game result.
        /// </summary>
        public TerminalResult GetTerminalResult(BitboardState boardState) => AttaxRules.GetTerminalResult(boardState);

        /// <summary>Result from the given player's perspective: +1 win, -1 loss, 0 draw or not over.</summary>
        public int GetTerminalOutcomeFor(BitboardState boardState, PlayerColor player) => AttaxRules.GetTerminalOutcomeFor(boardState, player);

        /// <summary>
        /// Counts how many enemy pieces are adjacent to a destination square, using bitboards.
        /// </summary>
        public int CountFlippedEnemies(BitboardState boardState, Move move, PlayerColor player) => AttaxRules.CountFlippedEnemies(boardState, move, player);



        #endregion


        #region inital chips setup
        public Dictionary<PlayerColor, List<(int x, int y)>> GetInitialChipPositions(bool randomize, int? seed) {
            var positions = new Dictionary<PlayerColor, List<(int x, int y)>> {
                [PlayerColor.Red] = new List<(int x, int y)>(),
                [PlayerColor.Blue] = new List<(int x, int y)>()
            };

            int size = AttaxConstants.BaseConst.BoardSize;

            if (!randomize) {
                // Default corner positions inherently satisfy the distance rule on a 7x7 board.
                positions[PlayerColor.Red].Add((0, 0));
                positions[PlayerColor.Red].Add((size - 1, size - 1));
                positions[PlayerColor.Blue].Add((0, size - 1));
                positions[PlayerColor.Blue].Add((size - 1, 0));
                return positions;
            }

            // --- Randomized positions with distance rule ---
            var rnd = seed.HasValue ? new Random(seed.Value) : new Random();
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
                var pos = validSquares[rnd.Next(validSquares.Count)];
                positions[PlayerColor.Red].Add(pos);
                allPlacedChips.Add(pos);
            }

            // Place 2 Blue pieces, ensuring they are distant from the Red pieces
            for (int i = 0; i < 2; i++) {
                // Find all squares that are not yet occupied AND are far enough from all enemy (Red) pieces.
                var validSquares = allSquares
                    .AsValueEnumerable().Except(allPlacedChips)
                    .Where(sq => positions[PlayerColor.Red].AsValueEnumerable().All(redPos => IsSufficientlyDistant(sq, redPos, 3)))
                    .ToList();

                if (!validSquares.AsValueEnumerable().Any()) {
                    // This is a rare fallback case if no valid spot can be found (e.g., on a very small board).
                    // It will just place the piece on any remaining empty square.
                    validSquares = allSquares.AsValueEnumerable().Except(allPlacedChips).ToList();
                    if (!validSquares.AsValueEnumerable().Any()) throw new InvalidOperationException("Board is full, cannot place Blue piece.");
                }

                var pos = validSquares[rnd.Next(validSquares.Count)];
                positions[PlayerColor.Blue].Add(pos);
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

            var rnd = seed.HasValue ? new Random(seed.Value) : new Random();
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
            zones = zones.AsValueEnumerable().OrderBy(_ => rnd.Next()).ToList();

            while (blockedPositions.Count < blocksToPlace && attempts < maxAttempts) {
                foreach (var zone in zones) {
                    if (blockedPositions.Count >= blocksToPlace) break;

                    int minX = zone.zx * zoneSize;
                    int maxX = Math.Min((zone.zx + 1) * zoneSize, AttaxConstants.BaseConst.BoardSize);
                    int minY = zone.zy * zoneSize;
                    int maxY = Math.Min((zone.zy + 1) * zoneSize, AttaxConstants.BaseConst.BoardSize);

                    // Find a random position within this zone.
                    int x = rnd.Next(minX, maxX);
                    int y = rnd.Next(minY, maxY);
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

        /// <summary>Deepest fully completed iteration of the last GetBestMove call (0 if none).</summary>
        public int LastCompletedDepth { get; private set; }
        /// <summary>True if the last GetBestMove ran out of nodes/time before any iteration completed.</summary>
        public bool LastSearchUsedFallback { get; private set; }
        /// <summary>Nodes spent by the unlimited depth-1 fallback pass (included in LastSearchStats, not in the budget).</summary>
        public long LastFallbackNodes { get; private set; }
        /// <summary>True if the last GetBestMove ended because the node budget or time limit was hit.</summary>
        public bool LastSearchWasInterrupted { get; private set; }
        /// <summary>
        /// Opt-in (default false, zero cost): record every root move's score for the last completed iteration in
        /// <see cref="LastRootScores"/>. Used for teacher labels, calibration and root-score diagnostics.
        /// </summary>
        public bool CollectRootScores { get; set; }
        /// <summary>Root moves of the last completed iteration sorted by Final descending. Null unless CollectRootScores.</summary>
        public IReadOnlyList<RootScore> LastRootScores { get; private set; }

        /// <summary>
        /// Optional cooperative cancellation of <see cref="GetBestMove"/> (default token = never cancelled, behavior unchanged).
        /// A cancelled search unwinds at the next node and returns default(Move): no depth-1 fallback, no log coordinator write.
        /// Set it before the search starts (from the thread that starts it); hint searches use <see cref="SearchOptions.Cancellation"/> instead.
        /// </summary>
        public System.Threading.CancellationToken SearchCancellation { get; set; }

        public Move GetBestMove(PlayerColor player, bool isHumanSimulation = false, float temperature = 0f, int topK = 1)
            => SearchCore(player, isHumanSimulation, temperature, topK, null, null);

        // Shared implementation of GetBestMove (options == null: behaviour unchanged) and Search (options != null: isolated hint search,
        // see AtaxxSearchHint.cs). Options searches: no logging coordinator writes, no random draws, no time/node limits, cancellable.
        private Move SearchCore(PlayerColor player, bool isHumanSimulation, float temperature, int topK, SearchOptions options, SearchResult result) {
            bool optionsSearch = options != null;
            var cancelToken = optionsSearch ? options.Cancellation : SearchCancellation;
            bool cancelled = false;

            try {
                if (!optionsSearch) this.currentSearchAge++; // Increment age for this new search
                LastSearchStats = new SearchStats();
                LastRootScores = null;
                //var debug = false;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                long? effectiveTimeLimitMs = (!optionsSearch && UseTimeManagement) ? (long?)SearchTimeLimitMs : null;
                BitboardState searchRootBoard = (optionsSearch && options.Root != null ? options.Root : Board).Clone();
                // The root hash is recomputed for options searches: the caller may pass any snapshot and either side to move,
                // and a stale side-to-move bit would collide with entries of the other side.
                if (optionsSearch) searchRootBoard.ZobristHash = ComputeZobristHash(searchRootBoard, player);
                bool serialRoot = optionsSearch && options.ParallelRoot.HasValue ? !options.ParallelRoot.Value : disableParallelRootSearch;
                bool noQuiescence = optionsSearch && options.DisableQuiescence.HasValue ? options.DisableQuiescence.Value : disableQuiescenceSearch;

                var allMoves = (optionsSearch && options.FromX.HasValue)
                    ? GetValidMovesFrom(searchRootBoard, player, options.FromX.Value, options.FromY.Value)
                    : GetAllValidMoves(searchRootBoard, player);
                if (allMoves.Count == 0) return default;

                // Root-only "comeback" aggression, computed once from the AI's (fixed root)
                // perspective. >1 when the AI is behind. Used to modulate the root bonus only.
                var (aiRootCount, oppRootCount) = GetRedAndBlueCounts(searchRootBoard, player);
                double rootAggression = prm.Aggression(aiRootCount - oppRootCount);

                Move bestMoveOverall = allMoves[0];
                int previousScore = 0;
                int maxDepthToSearch = optionsSearch ? (options.Depth ?? aiDepth) : (UseTimeManagement ? aiDepth + TimeManagedExtraDepth : aiDepth);
                // One shared node allowance for the whole move (all iterations, retries and root workers).
                SearchBudget budget = (!optionsSearch && MaxNodes.HasValue) ? new SearchBudget(MaxNodes.Value) : null;
                SearchBudget activeBudget = budget;
                long? searchTimeLimitMs = effectiveTimeLimitMs;
                // Set when no iteration could finish inside the budget/time limit: a depth-1 pass that
                // ignores both limits gives a sane move instead of a partial, untrustworthy ranking.
                bool fallbackUsed = false;
                long fallbackStartNodes = 0;
                int finalDepthReached = 0;
                // Training mode normally jumps straight to the target depth. With IterativeDeepeningInTraining
                // it deepens 1..N like normal play, so a small node budget still leaves a finished shallower result.
                int startingDepth = (trainingMode && !iterativeDeepeningInTraining) ? maxDepthToSearch : 1;
                bool shouldCollectLogDetails = logCoordinator != null && !optionsSearch;
                Dictionary<Move, int> finalEvaluationDetails = null;
                Dictionary<int, AIMoveCandidates> finalAiMoveCandidatesDict = null;
                // Options searches rank deterministically (equal scores ordered by move coordinates); GetBestMove keeps its exact legacy comparer.
                Comparison<(Move move, int score)> byScoreDesc = optionsSearch
                    ? (Comparison<(Move move, int score)>)((a, b) => {
                        int c = b.score.CompareTo(a.score);
                        if (c != 0) return c;
                        const int n = AttaxConstants.BaseConst.BoardSize;
                        int ka = ((a.move.FromY * n + a.move.FromX) * (n * n)) + a.move.ToY * n + a.move.ToX;
                        int kb = ((b.move.FromY * n + b.move.FromX) * (n * n)) + b.move.ToY * n + b.move.ToX;
                        return ka.CompareTo(kb);
                    })
                    : ((a, b) => b.score.CompareTo(a.score));

                if (optionsSearch && !serialRoot) {
                    // Parallel hint search: dedicated hint helpers, cleared once so every hint starts cold and deterministic.
                    // The AI's helpers are never touched, so its persistent table survives hint requests.
                    int hintParallelism = Math.Min(allMoves.Count, Environment.ProcessorCount);
                    var hintPool = EnsureHelperPool(true, hintParallelism);
                    for (int pi = 0; pi < hintParallelism; pi++) hintPool[pi].ClearSearchMemory();
                }

                for (int currentDepth = startingDepth; currentDepth <= maxDepthToSearch; currentDepth++) {
                    if (cancelToken.IsCancellationRequested) { cancelled = true; break; }
                    if (searchTimeLimitMs.HasValue && clock.ElapsedMilliseconds > searchTimeLimitMs.Value && finalDepthReached > 0) break;
                    if (activeBudget != null && activeBudget.Exhausted && finalDepthReached > 0) break;
                    bool interrupted = false;
                    int interruptedFlag = 0;
                    if (!fallbackUsed && finalDepthReached == 0 &&
                        ((activeBudget != null && activeBudget.Exhausted) ||
                         (searchTimeLimitMs.HasValue && clock.ElapsedMilliseconds > searchTimeLimitMs.Value))) {
                        // Already out of budget before any iteration completed: go straight to the fallback pass.
                        fallbackUsed = true;
                        fallbackStartNodes = LastSearchStats.Nodes + LastSearchStats.QNodes;
                        activeBudget = null;
                        searchTimeLimitMs = null;
                        currentDepth = 0;
                        continue;
                    }

                    // Aspiration Windows
                    // Keep alpha above int.MinValue so negamax window inversion (-alpha) cannot overflow.
                    int alpha = int.MinValue + 1;
                    int beta = int.MaxValue;
                    int windowSize = aspirationWindowOverride ?? (int)(evaluationScale * AspirationWindowMultiplier); // E.g., 500,000 if evaluationScale is 10000
                    if (currentDepth > 1) {
                        long calcAlpha = (long)previousScore - windowSize;
                        alpha = calcAlpha < int.MinValue + 1 ? int.MinValue + 1 : (int)calcAlpha;
                        
                        long calcBeta = (long)previousScore + windowSize;
                        beta = calcBeta > int.MaxValue ? int.MaxValue : (int)calcBeta;
                    }

                    var scored = new ConcurrentBag<(Move move, int score)>();
                    ConcurrentDictionary<Move, int> evaluationDetails = null;
                    ConcurrentDictionary<int, AIMoveCandidates> aiMoveCandidatesDict = null;
                    // Only allocated when CollectRootScores is on: move -> (strategic score, root bonus).
                    ConcurrentDictionary<Move, (int strat, int bonus)> rootDetail = null;
                    int logIdCounter = -1;

                    // Root search with aspiration window
                    void PerformSearch(int currentAlpha, int currentBeta) {
                        scored = new ConcurrentBag<(Move move, int score)>();
                        rootDetail = (CollectRootScores || optionsSearch) ? new ConcurrentDictionary<Move, (int strat, int bonus)>() : null;
                        if (shouldCollectLogDetails) {
                            evaluationDetails = new ConcurrentDictionary<Move, int>();
                            aiMoveCandidatesDict = new ConcurrentDictionary<int, AIMoveCandidates>();
                            logIdCounter = -1;
                        }

                        if (serialRoot) {
                            // Serial root mode: avoid root parallel overhead and any timeout behavior.
                            // Decoupled from UseTimeManagement so fixed-depth callers (e.g. arena on GPU)
                            // can opt into parallel root search to overlap evaluator calls. Root moves are
                            // searched with the same window (alpha is not raised between siblings), so serial
                            // and parallel explore the same node set; parallelism is purely a speedup.
                            // One private helper per engine, reused across moves (was a new 1M-entry table per
                            // search and per aspiration retry). ResetForSearch + ClearSearchMemory make it
                            // behave exactly like a freshly constructed helper. Never shared between engines.
                            var helper = optionsSearch
                                ? (_hintSerialHelper ?? (_hintSerialHelper = new AtaxxThreadHelper(AttaxConstants.BaseConst.BoardSize, AttaxConstants.BaseConst.KillerMoveMaxDepth)))
                                : (_serialHelper ?? (_serialHelper = new AtaxxThreadHelper(AttaxConstants.BaseConst.BoardSize, AttaxConstants.BaseConst.KillerMoveMaxDepth)));
                            helper.ResetForSearch();
                            helper.ClearSearchMemory();
                            helper.Budget = activeBudget;
                            helper.Cancellation = cancelToken;

                            float[] rootBatchScores = null;
                            var batchEvaluator = evaluator as IBatchValueEvaluator;
                            if (batchEvaluator != null && currentDepth == 1 && allMoves.Count > 1) {
                                var rootBoards = new List<BitboardState>(allMoves.Count);
                                for (int i = 0; i < allMoves.Count; i++) {
                                    var boardAfterMove = searchRootBoard.Clone();
                                    MakeMove(boardAfterMove, allMoves[i], player);
                                    rootBoards.Add(boardAfterMove);
                                }

                                var batchScores = batchEvaluator.EvaluateBatch(rootBoards, SwitchPlayer(player));
                                if (batchScores != null && batchScores.Length == allMoves.Count) {
                                    rootBatchScores = batchScores;
                                    helper.Evals += rootBatchScores.Length;
                                }
                            }

                            int[] rootMoveOrder = null;
                            if (rootBatchScores != null) {
                                rootMoveOrder = ValueEnumerable.Range(0, allMoves.Count)
                                    .OrderByDescending(i => rootBatchScores[i])
                                    .ToArray();
                            }

                            for (int orderedIndex = 0; orderedIndex < allMoves.Count; orderedIndex++) {
                                int moveIndex = rootMoveOrder != null ? rootMoveOrder[orderedIndex] : orderedIndex;
                                var move = allMoves[moveIndex];
                                if (cancelToken.IsCancellationRequested) { interrupted = true; break; }
                                helper.boardForThread = searchRootBoard.Clone();

                                bool isClone = IsCloneMove(move);
                                int flipped = CountFlippedEnemies(searchRootBoard, move, player);
                                int positionalBonus = 0;
                                int totalPieces = PopCount(searchRootBoard.RedPieces | searchRootBoard.BluePieces);
                                if (totalPieces <= prm.PositionalMaxPieces) {
                                    positionalBonus = GetPositionalValue(move.ToX, move.ToY);
                                }

                                MakeMove(helper.boardForThread, move, player);
                                int opponentFlipRisk = CalculateMaxOpponentFlips(helper.boardForThread, player);

                                // Root bonus is on the same scale as strategicScore (heuristic * evaluationScale).
                                RootBonusBreakdown bonus = ComputeRootBonus(isClone, flipped, opponentFlipRisk, positionalBonus, totalPieces, rootAggression);

                                int strategicScore;
                                if (rootBatchScores != null && currentDepth == 1) {
                                    // A model is never trained on game-over positions, so a terminal child must be scored by the
                                    // game rule, exactly as AlphaBeta does at ply 1, not by the model's opinion of it.
                                    // (Wins are overridden to ImmediateWinScore below; this covers a move that leaves the mover
                                    // stuck or ends the game as a loss/draw.)
                                    int childOutcome = IsGameOver(helper.boardForThread) ? GetTerminalOutcomeFor(helper.boardForThread, player) : 2;
                                    if (childOutcome == 2) strategicScore = -(int)(rootBatchScores[moveIndex] * evaluationScale);
                                    else if (childOutcome > 0) strategicScore = TerminalWinScore - 1;
                                    else if (childOutcome < 0) strategicScore = -(TerminalWinScore - 1);
                                    else strategicScore = 0;
                                } else {
                                    bool allowNullMoveForSearch = !trainingMode;
                                    bool shouldUseQuiescenceForSearch = !noQuiescence && !trainingMode && currentDepth >= prm.QuiescenceMinRootDepth;

                                    long shiftedAlpha = (long)currentAlpha - bonus.total;
                                    long shiftedBeta = (long)currentBeta - bonus.total;
                                    int stratAlpha = currentAlpha <= int.MinValue + InfinityMargin ? int.MinValue + 1 : (int)Math.Max(shiftedAlpha, (long)int.MinValue + 1);
                                    int stratBeta = currentBeta >= int.MaxValue - InfinityMargin ? int.MaxValue : (int)Math.Min(shiftedBeta, (long)int.MaxValue);

                                    int evalScoreFromAlphaBeta = AlphaBeta(helper, SwitchPlayer(player), currentDepth - 1, -stratBeta, -stratAlpha, 1, allowNullMoveForSearch, shouldUseQuiescenceForSearch, searchTimeLimitMs, clock);
                                    if (helper.Interrupted) {
                                        // Budget or time ran out inside this subtree: this iteration is incomplete.
                                        interrupted = true;
                                        break;
                                    }
                                    strategicScore = -evalScoreFromAlphaBeta;
                                }

                                int finalScore = (int)Math.Clamp((long)strategicScore + bonus.total, (long)int.MinValue + 1, int.MaxValue);

                                // Immediate win for either color: the opponent is wiped out or left without a move.
                                bool winsNow = GetTerminalOutcomeFor(helper.boardForThread, player) > 0;
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
                                rootDetail?.TryAdd(move, (strategicScore, bonus.total));
                            }

                            var stats = LastSearchStats;
                            stats.Add(helper);
                            LastSearchStats = stats;
                            return;
                        }

                        int parallelism = Math.Min(allMoves.Count, Environment.ProcessorCount);
                        var helperPool = EnsureHelperPool(optionsSearch, parallelism);

                        // Exclusive leases: a helper is owned by exactly one live worker at a time.
                        // Parallel.ForEach may start more tasks than MaxDegreeOfParallelism over its lifetime,
                        // so ownership is handed out from a pool instead of by a counter.
                        var leasePool = new ConcurrentBag<AtaxxThreadHelper>();
                        for (int pi = 0; pi < parallelism; pi++) leasePool.Add(helperPool[pi]);

                        using var cts = new System.Threading.CancellationTokenSource();
                        var parallelOptions = new ParallelOptions { CancellationToken = cts.Token, MaxDegreeOfParallelism = parallelism };

                        try {
                            Parallel.ForEach(
                                allMoves,
                                parallelOptions,
                                () => {
                                    if (!leasePool.TryTake(out var h)) {
                                        // Cannot happen with MaxDegreeOfParallelism == pool size; never share a helper.
                                        h = new AtaxxThreadHelper(AttaxConstants.BaseConst.BoardSize, AttaxConstants.BaseConst.KillerMoveMaxDepth);
                                    }
                                    h.ResetForSearch();
                                    h.Budget = activeBudget;
                                    h.Cancellation = cancelToken;
                                    return h;
                                },
                                (move, state, ataxxThreadHelper) => {
                                    if (ataxxThreadHelper.Interrupted || (activeBudget != null && activeBudget.Exhausted)
                                        || cancelToken.IsCancellationRequested
                                        || (searchTimeLimitMs.HasValue && clock.ElapsedMilliseconds > searchTimeLimitMs.Value)) {
                                        System.Threading.Volatile.Write(ref interruptedFlag, 1);
                                        state.Stop();
                                        return ataxxThreadHelper;
                                    }

                                    ataxxThreadHelper.boardForThread = searchRootBoard.Clone();

                                    bool isClone = IsCloneMove(move);
                                    int flipped = CountFlippedEnemies(searchRootBoard, move, player);
                                    int positionalBonus = 0;
                                    int totalPieces = PopCount(searchRootBoard.RedPieces | searchRootBoard.BluePieces);
                                    if (totalPieces <= prm.PositionalMaxPieces) {
                                        positionalBonus = GetPositionalValue(move.ToX, move.ToY);
                                    }

                                    MakeMove(ataxxThreadHelper.boardForThread, move, player);
                                    int opponentFlipRisk = CalculateMaxOpponentFlips(ataxxThreadHelper.boardForThread, player);

                                    RootBonusBreakdown bonus = ComputeRootBonus(isClone, flipped, opponentFlipRisk, positionalBonus, totalPieces, rootAggression);

                                    bool allowNullMoveForSearch = !trainingMode;
                                    bool shouldUseQuiescenceForSearch = !noQuiescence && !trainingMode && currentDepth >= prm.QuiescenceMinRootDepth;

                                    long shiftedAlpha = (long)currentAlpha - bonus.total;
                                    long shiftedBeta = (long)currentBeta - bonus.total;
                                    int stratAlpha = currentAlpha <= int.MinValue + InfinityMargin ? int.MinValue + 1 : (int)Math.Max(shiftedAlpha, (long)int.MinValue + 1);
                                    int stratBeta = currentBeta >= int.MaxValue - InfinityMargin ? int.MaxValue : (int)Math.Min(shiftedBeta, (long)int.MaxValue);

                                    int evalScoreFromAlphaBeta = AlphaBeta(ataxxThreadHelper, SwitchPlayer(player), currentDepth - 1, -stratBeta, -stratAlpha, 1, allowNullMoveForSearch, shouldUseQuiescenceForSearch, searchTimeLimitMs, clock);
                                    if (ataxxThreadHelper.Interrupted) {
                                        System.Threading.Volatile.Write(ref interruptedFlag, 1);
                                        state.Stop();
                                        return ataxxThreadHelper;
                                    }
                                    int strategicScore = -evalScoreFromAlphaBeta;

                                    int finalScore = (int)Math.Clamp((long)strategicScore + bonus.total, (long)int.MinValue + 1, int.MaxValue);

                                    bool winsNow = GetTerminalOutcomeFor(ataxxThreadHelper.boardForThread, player) > 0;
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
                                    rootDetail?.TryAdd(move, (strategicScore, bonus.total));
                                    return ataxxThreadHelper;
                                },
                                (ataxxThreadHelper) => {
                                    lock (this) {
                                        var stats = LastSearchStats;
                                        stats.Add(ataxxThreadHelper);
                                        LastSearchStats = stats;
                                    }
                                    ataxxThreadHelper.Budget = null;
                                    ataxxThreadHelper.Cancellation = default;
                                    leasePool.Add(ataxxThreadHelper);
                                }
                            );
                        } catch (OperationCanceledException) {
                            System.Threading.Volatile.Write(ref interruptedFlag, 1);
                        }
                        if (System.Threading.Volatile.Read(ref interruptedFlag) != 0) interrupted = true;
                    }

                    PerformSearch(alpha, beta);

                    if (!interrupted && scored.IsEmpty) break;

                    List<(Move move, int score)> currentScoredList = null;
                    int bestScoreThisDepth = 0;
                    if (!interrupted) {
                        currentScoredList = scored.AsValueEnumerable().ToList();
                        currentScoredList.Sort(byScoreDesc);
                        bestScoreThisDepth = currentScoredList[0].score;

                        // Check if search failed outside aspiration window
                        if (currentDepth > 1 && (bestScoreThisDepth <= alpha || bestScoreThisDepth >= beta)) {
                            interrupted = false;
                            interruptedFlag = 0;
                            PerformSearch(int.MinValue + 1, int.MaxValue);
                            if (!interrupted) {
                                if (scored.IsEmpty) break;
                                currentScoredList = scored.AsValueEnumerable().ToList();
                                currentScoredList.Sort(byScoreDesc);
                                bestScoreThisDepth = currentScoredList[0].score;
                            }
                        }
                    }

                    if (interrupted) {
                        // Hint searches have no budget or time limit, so an interruption is a cancellation: no depth-1 fallback.
                        // The same holds for a cancelled AI search (SearchCancellation), even if the budget ran out at the same time.
                        if (optionsSearch || cancelToken.IsCancellationRequested) { cancelled = true; break; }
                        // Out of nodes or time mid-iteration. Discard the partial ranking and keep the last
                        // fully completed iteration. If none completed, run one depth-1 pass without limits.
                        if (finalDepthReached == 0 && !fallbackUsed) {
                            fallbackUsed = true;
                            fallbackStartNodes = LastSearchStats.Nodes + LastSearchStats.QNodes;
                            activeBudget = null;
                            searchTimeLimitMs = null;
                            currentDepth = 0;
                            continue;
                        }
                        break;
                    }

                    if (fallbackUsed && currentDepth >= 1) {
                        // Fallback pass is a single depth-1 iteration.
                        maxDepthToSearch = Math.Min(maxDepthToSearch, 1);
                    }

                    // Root ties: the list is sorted by score, so the moves tied for the best score are the first tiedCount entries.
                    // Only counted when randomising, so DisableRandomRootTies does no extra work and draws nothing from tieRng.
                    int tiedCount = 1;
                    if (!disableRandomRootTies && !optionsSearch) {
                        while (tiedCount < currentScoredList.Count && currentScoredList[tiedCount].score == bestScoreThisDepth) tiedCount++;
                    }
                    bestMoveOverall = tiedCount > 1 ? currentScoredList[tieRng.Next(tiedCount)].move : currentScoredList[0].move;
                    previousScore = bestScoreThisDepth;
                    finalDepthReached = currentDepth;

                    if (rootDetail != null) {
                        var rootScores = new List<RootScore>(currentScoredList.Count);
                        foreach (var (mv, fin) in currentScoredList) {
                            rootDetail.TryGetValue(mv, out var d);
                            rootScores.Add(new RootScore { Move = mv, Strategic = d.strat, Bonus = d.bonus, Final = fin });
                        }
                        LastRootScores = rootScores;
                        if (optionsSearch) result.Ranked = rootScores;
                    }

                    if (temperature > 0 && currentDepth == maxDepthToSearch) {
                        // Apply sampling at the final depth
                        var candidates = currentScoredList.AsValueEnumerable().Take(Math.Max(1, topK)).ToList();
                        if (candidates.Count > 1) {
                            double maxScore = candidates.AsValueEnumerable().Max(c => c.score);
                            var weights = candidates.AsValueEnumerable().Select(c => Math.Exp((c.score - maxScore) / (temperature * temperatureUnit))).ToList();

                            double totalWeight = weights.AsValueEnumerable().Sum();
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
                        finalEvaluationDetails = evaluationDetails.AsValueEnumerable().ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                        finalAiMoveCandidatesDict = aiMoveCandidatesDict.AsValueEnumerable().ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                    }

                    if (ataxxLogger != null && ataxxLogger.IsEnabled && !(optionsSearch && options.SuppressLogging)) {
                        ataxxLogger.LogInformation(
                            $"Depth {currentDepth} completed. Best move: {bestMoveOverall.FromX},{bestMoveOverall.FromY}->{bestMoveOverall.ToX},{bestMoveOverall.ToY} Score: {bestScoreThisDepth} " +
                            $"Elapsed: {clock.ElapsedMilliseconds}ms, TargetDepth: {aiDepth}, MaxDepth: {maxDepthToSearch}, TimeManagement: {UseTimeManagement}, TimeLimitMs: {(effectiveTimeLimitMs.HasValue ? effectiveTimeLimitMs.Value : -1)}");
                    }

                    // If we found a winning move, no need to search deeper
                    if (bestScoreThisDepth > ForcedWinEarlyExitScore) break;
                }

                clock.Stop();
                LastCompletedDepth = finalDepthReached;
                LastSearchUsedFallback = fallbackUsed;
                LastFallbackNodes = fallbackUsed ? (LastSearchStats.Nodes + LastSearchStats.QNodes) - fallbackStartNodes : 0;
                LastSearchWasInterrupted = fallbackUsed || (budget != null && budget.Exhausted);

                if (cancelled) {
                    // Cancelled searches never log and never return a partial move.
                    if (optionsSearch) {
                        result.CompletedDepth = finalDepthReached;
                        result.Cancelled = true;
                        result.HasMove = false;
                        result.Ranked = Array.Empty<RootScore>();
                    }
                    return default;
                }

                if (optionsSearch) {
                    // Hint searches skip the log coordinator entirely and report through the SearchResult.
                    result.CompletedDepth = finalDepthReached;
                    result.Best = bestMoveOverall;
                    result.HasMove = finalDepthReached > 0;
                    return bestMoveOverall;
                }

                if (ataxxLogger != null) {
                    ataxxLogger.LogInformation(
                        $"Search completed in {clock.ElapsedMilliseconds}ms. {LastSearchStats}, TotalNodes: {LastSearchStats.Nodes + LastSearchStats.QNodes}, " +
                        $"NodeBudget: {(MaxNodes.HasValue ? MaxNodes.Value.ToString() : "none")}, NodesRemaining: {(budget != null ? budget.Remaining.ToString() : "n/a")}, " +
                        $"CompletedDepth: {finalDepthReached}, Fallback: {fallbackUsed}");
                }

                if (logCoordinator != null) {
                    if (finalEvaluationDetails == null) finalEvaluationDetails = new Dictionary<Move, int>();
                    if (finalAiMoveCandidatesDict == null) finalAiMoveCandidatesDict = new Dictionary<int, AIMoveCandidates>();

                    if (!finalEvaluationDetails.TryGetValue(bestMoveOverall, out int selectedMoveId) || !finalAiMoveCandidatesDict.ContainsKey(selectedMoveId)) {
                        bool isClone = IsCloneMove(bestMoveOverall);
                        int flipped = CountFlippedEnemies(searchRootBoard, bestMoveOverall, player);
                        int totalPieces = PopCount(searchRootBoard.RedPieces | searchRootBoard.BluePieces);
                        int positionalBonus = totalPieces <= prm.PositionalMaxPieces
                            ? GetPositionalValue(bestMoveOverall.ToX, bestMoveOverall.ToY)
                            : 0;

                        int opponentFlipRisk = 0;
                        var boardAfterMove = searchRootBoard.Clone();
                        MakeMove(boardAfterMove, bestMoveOverall, player);
                        opponentFlipRisk = CalculateMaxOpponentFlips(boardAfterMove, player);

                        RootBonusBreakdown bonus = ComputeRootBonus(isClone, flipped, opponentFlipRisk, positionalBonus, totalPieces, rootAggression);

                        selectedMoveId = finalAiMoveCandidatesDict.Count == 0 ? 0 : ((IEnumerable<int>)finalAiMoveCandidatesDict.Keys).AsValueEnumerable().Max() + 1;
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
                        currentTurn = new TurnLog();
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
                // Data-generation callers opt in to fail loudly: a swallowed model error becomes default(Move),
                // which self-play treats as a pass / random move and would silently corrupt the dataset.
                if (throwOnSearchError) throw;
                return default;
            } finally {
                if (optionsSearch) {
                    // Hints use dedicated helpers (never the AI's), so the AI's persistent table is untouched. Only drop the token reference.
                    // Locals: Dispose() may null the fields from another thread while a cancelled worker is still unwinding.
                    var hintSerial = _hintSerialHelper;
                    if (hintSerial != null) hintSerial.Cancellation = default;
                    var hintPool = _hintHelpers;
                    if (hintPool != null) {
                        for (int pi = 0; pi < hintPool.Length; pi++) { var h = hintPool[pi]; if (h != null) h.Cancellation = default; }
                    }
                } else {
                    var serial = _serialHelper;
                    if (serial != null) serial.Cancellation = default;       // drop the (possibly cancelled) AI token reference
                }
            }
        }


        // This function uses a "negamax" approach, which is concise and robust way to implement minimax with alpha-beta pruning.
        // It always evaluates the score from the perspective of the current 'player'.
        private int AlphaBeta(AtaxxThreadHelper ataxxThreadHelper, PlayerColor player, int depth, int alpha, int beta, int ply, bool allowNullMove, bool shouldUseQuiescence, long? timeLimitMs, System.Diagnostics.Stopwatch clock) {
            // Interrupted (budget/time): unwind immediately. The returned value is meaningless; callers
            // check helper.Interrupted and must not use or cache anything derived from it.
            if (ataxxThreadHelper.Interrupted) return 0;
            if (ataxxThreadHelper.Cancellation.IsCancellationRequested) {
                ataxxThreadHelper.Interrupted = true;
                return 0;
            }
            if (ataxxThreadHelper.Budget != null && !ataxxThreadHelper.Budget.TryTake()) {
                ataxxThreadHelper.Interrupted = true;
                return 0;
            }
            if (timeLimitMs.HasValue && clock != null && clock.ElapsedMilliseconds > timeLimitMs.Value) {
                ataxxThreadHelper.Interrupted = true;
                return 0;
            }
            ataxxThreadHelper.Nodes++;
            if (ply >= AtaxxThreadHelper.MaxPly - 1) {
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
                    // Terminal scores are stored relative to the stored position; restore this node's ply.
                    int ttScore = FromTTScore(entry.score, ply);
                    if (entry.flag == (byte)NodeType.Exact) {
                        return ttScore;
                    }
                    if (entry.flag == (byte)NodeType.LowerBound && ttScore >= beta) {
                        return ttScore;
                    }
                    if (entry.flag == (byte)NodeType.UpperBound && ttScore <= alpha) {
                        return ttScore;
                    }
                }
            }

            if (IsGameOver(ataxxThreadHelper.boardForThread)) {
                // Decisive terminal scoring: win/loss dominates any heuristic, and ply-adjusted
                // so the search prefers faster wins / slower losses. Rule: a stuck side loses
                // regardless of piece counts (see GetTerminalResult).
                int outcome = GetTerminalOutcomeFor(ataxxThreadHelper.boardForThread, player);
                if (outcome > 0) return TerminalWinScore - ply;
                if (outcome < 0) return -TerminalWinScore + ply;
                return 0;
            }

            if (depth <= 0) {
                if (shouldUseQuiescence)
                    return Quiescence(ataxxThreadHelper, player, alpha, beta, prm.QuiescenceDepth, ply, timeLimitMs, clock);
                else
                    return Evaluate(ataxxThreadHelper.boardForThread, player, ataxxThreadHelper);
            }

            // Null-move pruning is unsound in Ataxx endgames (zugzwang: being forced to move can
            // be strictly bad), so restrict it to the midgame with a non-trivial piece count.
            // Gated by disableNullMovePruning so depth-N play can be A/B tested without recompiling.
            ulong playerPiecesForNull = (player == PlayerColor.Red) ? ataxxThreadHelper.boardForThread.RedPieces : ataxxThreadHelper.boardForThread.BluePieces;
            bool nullMoveSafe = !disableNullMovePruning
                && PopCount(ataxxThreadHelper.boardForThread.EmptySquares()) > prm.NullMoveMinEmpty
                && PopCount(playerPiecesForNull) >= prm.NullMoveMinPieces;
            if (allowNullMove && nullMoveSafe && depth >= prm.NullMoveMinDepth && HasAnyLegalMove(ataxxThreadHelper.boardForThread, player)) {
                int staticEval = Evaluate(ataxxThreadHelper.boardForThread, player, ataxxThreadHelper);
                if (staticEval >= beta) {
                    ataxxThreadHelper.boardForThread.ZobristHash ^= ZobristHasher.GetSideToMoveKey();
                    int nullMoveReduction = prm.NullMoveReduction;
                    int score = -AlphaBeta(ataxxThreadHelper, SwitchPlayer(player), depth - 1 - nullMoveReduction, -beta, -beta + 1, ply + 1, false, shouldUseQuiescence, timeLimitMs, clock);
                    ataxxThreadHelper.boardForThread.ZobristHash ^= ZobristHasher.GetSideToMoveKey();
                    if (ataxxThreadHelper.Interrupted) return 0;
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
                // Board and hash are restored above; an interrupted subtree must not influence anything.
                if (ataxxThreadHelper.Interrupted) return 0;

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
                score = ToTTScore(bestScore, ply),
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

        // Terminal scores are TerminalWinScore - ply of the terminal node, i.e. relative to the search root.
        // A transposition reached at a different ply needs the distance re-based, so the table stores
        // them relative to the stored node and every probe re-adds its own ply.
        private const int TerminalScoreThreshold = 900_000_000;

        private static int ToTTScore(int score, int ply) {
            if (score > TerminalScoreThreshold) return score + ply;
            if (score < -TerminalScoreThreshold) return score - ply;
            return score;
        }

        private static int FromTTScore(int score, int ply) {
            if (score > TerminalScoreThreshold) return score - ply;
            if (score < -TerminalScoreThreshold) return score + ply;
            return score;
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

            ulong attackMask = BoardLookup.SingleStepMoves[toIndex];
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
                return AttaxRules.MakeMove(boardState, move, player);
            } catch (Exception ex) {
                ataxxLogger?.LogError(ex.ToString());
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
            ulong attackMask = BoardLookup.SingleStepMoves[toIndex];

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
            return (int)(evaluator.Evaluate(board, player) * evaluationScale);
        }

        // Quiescence search using negamax. Uses per-ply buffers — zero GC allocation.
        // 'ply' is threaded through so each recursive level writes to its own buffer slot.
        private int Quiescence(AtaxxThreadHelper helper, PlayerColor player, int alpha, int beta, int depth, int ply, long? timeLimitMs = null, System.Diagnostics.Stopwatch clock = null) {
            if (helper.Interrupted) return 0;
            if (helper.Cancellation.IsCancellationRequested) {
                helper.Interrupted = true;
                return 0;
            }
            if (helper.Budget != null && !helper.Budget.TryTake()) {
                helper.Interrupted = true;
                return 0;
            }
            if (timeLimitMs.HasValue && clock != null && clock.ElapsedMilliseconds > timeLimitMs.Value) {
                helper.Interrupted = true;
                return 0;
            }
            helper.QNodes++;
            if (ply >= AtaxxThreadHelper.MaxPly - 1) return Evaluate(helper.boardForThread, player, helper);

            if (IsGameOver(helper.boardForThread)) {
                int outcome = GetTerminalOutcomeFor(helper.boardForThread, player);
                if (outcome > 0) return TerminalWinScore - ply;
                if (outcome < 0) return -TerminalWinScore + ply;
                return 0;
            }

            int standPatScore = Evaluate(helper.boardForThread, player, helper);
            if (standPatScore >= beta) return beta;
            if (standPatScore > alpha) alpha = standPatScore;
            if (depth <= 0) return standPatScore;

            int noisyCount = GetNoisyMovesIntoBuffer(helper, player, ply);
            for (int i = 0; i < noisyCount; i++) {
                var move = helper.perPlyMoveBuffer[ply, i];
                var undoInfo = MakeMoveFast(helper.boardForThread, move, player);
                int score = -Quiescence(helper, SwitchPlayer(player), -beta, -alpha, depth - 1, ply + 1, timeLimitMs, clock);
                UnmakeMove(helper.boardForThread, move, player, undoInfo);
                if (helper.Interrupted) return 0;
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
        /*private bool IsCaptureMove(BitboardState boardState, Move move, PlayerColor player) {
            ulong opponentPieces = (player == PlayerColor.Red) ? boardState.BluePieces : boardState.RedPieces;
            int toIndex = GetBitIndex(move.ToX, move.ToY);
            ulong attackMask = BoardLookup.SingleStepMoves[toIndex];
            return (attackMask & opponentPieces) != 0;
        }*/
               


        public static int PopCount(ulong value) => BitboardOps.PopCount(value);
        //private ulong GetMoveDestinationsBitboard(BitboardState board, PlayerColor player)  => BitboardFeatures.GetMoveDestinationsBitboard(board, player);
        private static int TrailingZeroCount(ulong value) => BitboardOps.TrailingZeroCount(value);

        public bool IsAdjacent(int x1, int y1, int x2, int y2) => Math.Abs(x1 - x2) <= 1 && Math.Abs(y1 - y2) <= 1 && !(x1 == x2 && y1 == y2);
        public static PlayerColor SwitchPlayer(PlayerColor current) => current == PlayerColor.Red ? PlayerColor.Blue : current == PlayerColor.Blue ? PlayerColor.Red : PlayerColor.None;

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
            double clonePts = isClone ? prm.CloneBonus(totalPieces) / aggression : 0.0;
            // A jump that neither captures nor grows is almost always inferior to a clone; discourage
            // it on the growth axis so the engine prefers clones/captures over pure repositioning.
            if (!isClone && flipped == 0) clonePts -= prm.NonCapturingJumpPenalty;
            double flipPts = flipped * prm.FlipPoints * aggression;
            double riskPts = opponentFlipRisk * prm.RiskPoints / aggression;
            double posPts = positionalBonus * prm.PositionalPoints;

            // rootBonusScale is exactly 1.0 unless the caller opted in, and x * 1.0 == x in IEEE arithmetic,
            // so classic behaviour is bit-identical.
            var b = new RootBonusBreakdown {
                cloneScaled = (int)(clonePts * evaluationScale * rootBonusScale),
                flipScaled = (int)(flipPts * evaluationScale * rootBonusScale),
                riskScaled = (int)(riskPts * evaluationScale * rootBonusScale),
                posScaled = (int)(posPts * evaluationScale * rootBonusScale)
            };
            b.total = b.cloneScaled + b.flipScaled - b.riskScaled + b.posScaled;
            return b;
        }

        // Positional value of a destination square (root bonus input); the table lives in the engine params.
        private int GetPositionalValue(int x, int y) => prm.PositionalValue(x, y);

        //private int GetPotentialMobilityInternal(BitboardState boardState, ulong destinationSquares) => BitboardFeatures.GetPotentialMobilityInternal(boardState, destinationSquares);

        /// <summary>
        /// Calculates potential mobility for a given list of moves.
        /// </summary>
        /// <param name="boardState">The current board state.</param>
        /// <param name="moves">The list of moves whose destinations will be analyzed.</param>
        /// <returns>The potential mobility score.</returns>
        /*public int GetPotentialMobility(BitboardState boardState, List<Move> moves) {
            // Convert the list of moves into a single bitboard of destination squares.
            ulong destinationSquares = 0UL;
            foreach (var move in moves) {
                destinationSquares |= (1UL << GetBitIndex(move.ToX, move.ToY));
            }
            return GetPotentialMobilityInternal(boardState, destinationSquares);
        }*/

        //private bool HasAdjacentEmpty(BitboardState boardState, int x, int y) => BitboardFeatures.HasAdjacentEmpty(boardState, x, y);

        //private bool IsStable(BitboardState boardState, int x, int y, PlayerColor player) => BitboardFeatures.IsStable(boardState, x, y, player);

        //public int GetStabilityBonus(BitboardState boardState, PlayerColor player)  => BitboardFeatures.GetStabilityBonus(boardState, player);

        //public int GetCenterControl(BitboardState boardState, PlayerColor player)  => BitboardFeatures.GetCenterControl(boardState, player);

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
                ulong attackMask = BoardLookup.SingleStepMoves[toIndex];
                
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
            _parallelHelpers = null; // release 32 MB x N LOH TT arrays back to GC
            _serialHelper = null;
            _hintHelpers = null;
            _hintSerialHelper = null;
            if (evaluator is IDisposable disposableEvaluator) {
                disposableEvaluator.Dispose();
            }
        }
    }
}
