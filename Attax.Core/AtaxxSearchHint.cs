#nullable disable
using System;
using System.Collections.Generic;
using System.Threading;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core {
    /// <summary>
    /// Options for <see cref="AtaxxAIEngine.Search"/>. Every member is optional; a default instance searches the engine's own board
    /// with the engine's own depth/quiescence/parallel settings, but isolated from the AI (no logging, no persistent transposition table,
    /// deterministic ties, no engine statistics left behind).
    /// </summary>
    public sealed class SearchOptions {
        /// <summary>Caller-supplied snapshot of the position. null = a clone of <see cref="AtaxxAIEngine.Board"/>. It is cloned, never mutated.</summary>
        public BitboardState Root;
        /// <summary>Search depth override (>= 1). null = the engine's aiDepth. Time management and the node budget never apply to options searches.</summary>
        public int? Depth;
        /// <summary>true = no quiescence, false = quiescence (still subject to the engine's training mode). null = the engine's setting.</summary>
        public bool? DisableQuiescence;
        /// <summary>true = parallel root search, false = serial. null = the engine's setting.</summary>
        public bool? ParallelRoot;
        /// <summary>Root filter: only moves of the chip on (FromX, FromY) are searched (all its clones and jumps). Set both or neither.</summary>
        public int? FromX;
        public int? FromY;
        /// <summary>Suppresses the engine's per-depth information log lines (errors are always logged). The AILogCoordinator is never written by options searches.</summary>
        public bool SuppressLogging = true;
        /// <summary>Cooperative cancellation. A cancelled search returns <see cref="SearchResult.Cancelled"/> and never falls back to a shallow move.</summary>
        public CancellationToken Cancellation;
    }

    /// <summary>Outcome of <see cref="AtaxxAIEngine.Search"/>.</summary>
    public sealed class SearchResult {
        public Move Best;
        /// <summary>false when there is no legal move (for the filter) or the search was cancelled or failed.</summary>
        public bool HasMove;
        public bool Cancelled;
        /// <summary>Deepest fully completed iteration (0 if none).</summary>
        public int CompletedDepth;
        /// <summary>Nodes (main + quiescence) spent by this search.</summary>
        public long Nodes;
        /// <summary>Root moves of the last completed iteration, sorted by Final descending (ties by move coordinates). Never null.</summary>
        public IReadOnlyList<RootScore> Ranked = Array.Empty<RootScore>();
    }

    public partial class AtaxxAIEngine {
        /// <summary>
        /// All legal moves of the chip of <paramref name="player"/> on (<paramref name="x"/>, <paramref name="y"/>): every clone and every jump,
        /// WITHOUT the clone-destination canonicalisation GetAllValidMoves applies. Empty if the player has no chip there.
        /// </summary>
        public List<Move> GetValidMovesFrom(BitboardState board, PlayerColor player, int x, int y) {
            var moves = new List<Move>();
            int size = AttaxConstants.BaseConst.BoardSize;
            if (x < 0 || y < 0 || x >= size || y >= size) return moves;
            int fromIndex = GetBitIndex(x, y);
            ulong playerPieces = (player == PlayerColor.Red) ? board.RedPieces : board.BluePieces;
            if ((playerPieces & (1UL << fromIndex)) == 0) return moves;
            ulong empty = board.EmptySquares();

            ulong clones = BoardLookup.SingleStepMoves[fromIndex] & empty;
            while (clones > 0) {
                int toIndex = TrailingZeroCount(clones);
                moves.Add(new Move(x, y, toIndex % size, toIndex / size));
                clones &= clones - 1;
            }
            ulong jumps = BoardLookup.TwoStepMoves[fromIndex] & empty;
            while (jumps > 0) {
                int toIndex = TrailingZeroCount(jumps);
                moves.Add(new Move(x, y, toIndex % size, toIndex / size));
                jumps &= jumps - 1;
            }
            return moves;
        }

        /// <summary>
        /// Client-side "super jump" ability: every empty cell at Chebyshev distance >= 3 from the chip of <paramref name="player"/> on
        /// (<paramref name="x"/>, <paramref name="y"/>) (no upper limit). Disjoint from <see cref="GetValidMovesFrom"/> (distance 1 and 2).
        /// Empty if the player has no chip there. A super jump behaves like a jump (the source is vacated, adjacent enemies are flipped),
        /// so <see cref="MakeMove"/>, <see cref="MakeMoveFast"/> and <see cref="UnmakeMove"/> apply it unchanged.
        /// Super jumps are NOT part of GetAllValidMoves, FillMoves, the AI search, <see cref="IsGameOver"/> or the training logs:
        /// a side without a standard move ends the game even if it could still super jump.
        /// </summary>
        public List<Move> GetSuperJumpMovesFrom(BitboardState board, PlayerColor player, int x, int y) {
            var moves = new List<Move>();
            int size = AttaxConstants.BaseConst.BoardSize;
            if (x < 0 || y < 0 || x >= size || y >= size) return moves;
            int fromIndex = GetBitIndex(x, y);
            ulong playerPieces = (player == PlayerColor.Red) ? board.RedPieces : board.BluePieces;
            if ((playerPieces & (1UL << fromIndex)) == 0) return moves;

            ulong far = board.EmptySquares()
                & ~(BoardLookup.SingleStepMoves[fromIndex] | BoardLookup.TwoStepMoves[fromIndex])
                & ~(1UL << fromIndex);
            while (far > 0) {
                int toIndex = TrailingZeroCount(far);
                moves.Add(new Move(x, y, toIndex % size, toIndex / size));
                far &= far - 1;
            }
            return moves;
        }

        /// <summary>true when <paramref name="m"/> is a super jump (Chebyshev distance >= 3), e.g. to pick an animation or highlight colour.</summary>
        public static bool IsSuperJump(Move m) => Math.Max(Math.Abs(m.ToX - m.FromX), Math.Abs(m.ToY - m.FromY)) >= 3;

        /// <summary>
        /// Full legality check for a client move. Callers (UI/controller) MUST call this before <see cref="MakeMove"/>, because MakeMove
        /// only rejects blocked destinations: it does not check that the source holds the mover's chip or that the destination is empty,
        /// so an unchecked move can put both colours on one cell. Legal: in bounds, the source holds a chip of <paramref name="player"/>,
        /// the destination is empty (not blocked, not occupied), and the Chebyshev distance is 1 or 2, or >= 3 when
        /// <paramref name="allowSuperJump"/> is true.
        /// </summary>
        public bool IsLegalMove(BitboardState board, PlayerColor player, Move move, bool allowSuperJump) {
            int size = AttaxConstants.BaseConst.BoardSize;
            if (move.FromX < 0 || move.FromY < 0 || move.FromX >= size || move.FromY >= size) return false;
            if (move.ToX < 0 || move.ToY < 0 || move.ToX >= size || move.ToY >= size) return false;
            int fromIndex = GetBitIndex(move.FromX, move.FromY);
            int toIndex = GetBitIndex(move.ToX, move.ToY);
            if (fromIndex == toIndex) return false;
            ulong playerPieces = (player == PlayerColor.Red) ? board.RedPieces : board.BluePieces;
            if ((playerPieces & (1UL << fromIndex)) == 0) return false;
            if ((board.EmptySquares() & (1UL << toIndex)) == 0) return false;
            int d = Math.Max(Math.Abs(move.ToX - move.FromX), Math.Abs(move.ToY - move.FromY));
            return d <= 2 || allowSuperJump;
        }

        /// <summary>
        /// Client-side "convert" cheat: the cells of every enemy chip of <paramref name="player"/>, i.e. every cell
        /// <see cref="TryConvertEnemyChip"/> would accept. Intended for the UI highlight.
        /// </summary>
        public List<(int X, int Y)> GetConvertibleChips(BitboardState board, PlayerColor player) {
            var cells = new List<(int X, int Y)>();
            int size = AttaxConstants.BaseConst.BoardSize;
            ulong enemy = (player == PlayerColor.Red) ? board.BluePieces : board.RedPieces;
            while (enemy > 0) {
                int index = TrailingZeroCount(enemy);
                cells.Add((index % size, index / size));
                enemy &= enemy - 1;
            }
            return cells;
        }

        /// <summary>
        /// Client-side "convert" cheat: turns the enemy chip on (<paramref name="x"/>, <paramref name="y"/>) into a chip of
        /// <paramref name="player"/>. Only that one cell changes owner: neighbouring enemy chips are NOT captured. Returns false and leaves the
        /// board untouched when the cell is out of bounds or does not hold an enemy chip (empty, blocked or own chip).
        /// The Zobrist hash is updated incrementally; the side-to-move key is not touched (this is an action, not a turn: the client decides
        /// whether it consumes the turn). The caller MUST check <see cref="IsGameOver"/> / <see cref="GetTerminalResult"/> afterwards,
        /// because converting the last enemy chip, or one that was the enemy's only mobile chip, ends the game.
        /// Not part of the AI search, <see cref="ActionCodec"/> or the training logs: exclude these games from training data.
        /// </summary>
        public bool TryConvertEnemyChip(BitboardState board, PlayerColor player, int x, int y) {
            int size = AttaxConstants.BaseConst.BoardSize;
            if (player != PlayerColor.Red && player != PlayerColor.Blue) return false;
            if (x < 0 || y < 0 || x >= size || y >= size) return false;
            ulong mask = 1UL << GetBitIndex(x, y);
            ref ulong playerPieces = ref (player == PlayerColor.Red ? ref board.RedPieces : ref board.BluePieces);
            ref ulong enemyPieces = ref (player == PlayerColor.Red ? ref board.BluePieces : ref board.RedPieces);
            if ((enemyPieces & mask) == 0) return false;

            enemyPieces &= ~mask;
            playerPieces |= mask;
            board.ZobristHash ^= ZobristHasher.GetPieceKey(x, y, GetPieceTypeIndex(SwitchPlayer(player)));
            board.ZobristHash ^= ZobristHasher.GetPieceKey(x, y, GetPieceTypeIndex(player));
            return true;
        }

        /// <summary>
        /// Whether moving the chip on (<paramref name="fromX"/>, <paramref name="fromY"/>) to the destination of <paramref name="recommended"/>
        /// gives the same position as the recommended move: the exact same source for a jump, any chip within one cell for a clone.
        /// (The engine keeps one canonical source per clone destination.)
        /// </summary>
        public static bool IsEquivalentMove(Move recommended, int fromX, int fromY) {
            bool recommendedIsClone = Math.Abs(recommended.ToX - recommended.FromX) <= 1 && Math.Abs(recommended.ToY - recommended.FromY) <= 1;
            if (!recommendedIsClone) return recommended.FromX == fromX && recommended.FromY == fromY;
            return Math.Abs(recommended.ToX - fromX) <= 1 && Math.Abs(recommended.ToY - fromY) <= 1;
        }

        /// <summary>
        /// Opt-in search for hints and analysis. Never touches the AI's persistent state: no log coordinator writes, no random tie draws,
        /// no <c>currentSearchAge</c> change, <c>Last*</c> statistics are restored, and the transposition tables of the helpers it uses are
        /// cleared before and after (so the first AI search afterwards starts with a cold table).
        /// NOT thread-safe: one search at a time per engine instance (this includes <see cref="GetBestMove"/>); the caller serialises.
        /// The caller must not mutate <see cref="SearchOptions.Root"/> or <see cref="AtaxxAIEngine.Board"/> while it runs.
        /// </summary>
        public SearchResult Search(PlayerColor player, SearchOptions options) {
            if (options == null) options = new SearchOptions();
            if (options.FromX.HasValue != options.FromY.HasValue)
                throw new ArgumentException("FromX and FromY must be set together.", nameof(options));
            if (options.Depth.HasValue && options.Depth.Value < 1)
                throw new ArgumentException("Depth must be >= 1.", nameof(options));

            var result = new SearchResult();
            var savedStats = LastSearchStats;
            var savedRoot = LastRootScores;
            int savedDepth = LastCompletedDepth;
            bool savedFallback = LastSearchUsedFallback;
            long savedFallbackNodes = LastFallbackNodes;
            bool savedInterrupted = LastSearchWasInterrupted;
            try {
                SearchCore(player, false, 0f, 1, options, result);
                result.Nodes = LastSearchStats.Nodes + LastSearchStats.QNodes;
            } finally {
                LastSearchStats = savedStats;
                LastRootScores = savedRoot;
                LastCompletedDepth = savedDepth;
                LastSearchUsedFallback = savedFallback;
                LastFallbackNodes = savedFallbackNodes;
                LastSearchWasInterrupted = savedInterrupted;
            }
            return result;
        }
    }
}
