using System;

namespace Attax.Core.Utils {
    public sealed class GameState {
        public BitboardState Board;
        public ulong RedFootprints;
        public ulong BlueFootprints;
        public int RedCredits;
        public int BlueCredits;
        public PlayerColor SideToMove;
        public int TurnNumber;
        public int ActionCount;
        public int MovesRemainingThisTurn;
        public int AbilityActionsUsedThisTurn;
        public TerminalResult Result;
        public ulong RulesFingerprint { get; }

        private readonly GameRulesConfig _rules;

        internal GameRulesConfig Rules { get { return _rules; } }

        internal GameState(GameRulesConfig rules, ulong rulesFingerprint) {
            _rules = rules;
            RulesFingerprint = rulesFingerprint;
            Board = new BitboardState();
            SideToMove = PlayerColor.Red;
            MovesRemainingThisTurn = 1;
            AbilityActionsUsedThisTurn = 0;
            TurnNumber = 0;
            ActionCount = 0;
            Result = TerminalResult.NotOver;
        }

        public GameRulesConfig ExportRules() {
            return _rules.Clone();
        }

        public GameState Clone() {
            var copy = new GameState(_rules, RulesFingerprint) {
                Board = this.Board.Clone(),
                RedFootprints = this.RedFootprints,
                BlueFootprints = this.BlueFootprints,
                RedCredits = this.RedCredits,
                BlueCredits = this.BlueCredits,
                SideToMove = this.SideToMove,
                TurnNumber = this.TurnNumber,
                ActionCount = this.ActionCount,
                MovesRemainingThisTurn = this.MovesRemainingThisTurn,
                AbilityActionsUsedThisTurn = this.AbilityActionsUsedThisTurn,
                Result = this.Result
            };
            return copy;
        }

        public int GetCredits(PlayerColor color) {
            if (color == PlayerColor.Red) return RedCredits;
            if (color == PlayerColor.Blue) return BlueCredits;
            return 0;
        }

        public ulong ComputeFingerprint() {
            ulong hash = 14695981039346656037UL;
            hash = FnvFeed(hash, Board.RedPieces);
            hash = FnvFeed(hash, Board.BluePieces);
            hash = FnvFeed(hash, Board.BlockedSquares);
            hash = FnvFeed(hash, Board.ZobristHash);
            hash = FnvFeed(hash, (ulong)SideToMove);
            hash = FnvFeed(hash, RedFootprints);
            hash = FnvFeed(hash, BlueFootprints);
            hash = FnvFeed(hash, (ulong)(long)RedCredits);
            hash = FnvFeed(hash, (ulong)(long)BlueCredits);
            hash = FnvFeed(hash, (ulong)(long)TurnNumber);
            hash = FnvFeed(hash, (ulong)(long)ActionCount);
            hash = FnvFeed(hash, (ulong)(long)MovesRemainingThisTurn);
            hash = FnvFeed(hash, (ulong)(long)AbilityActionsUsedThisTurn);
            hash = FnvFeed(hash, RulesFingerprint);
            return hash;
        }

        public static GameState Restore(ulong red, ulong blue, ulong blocked, ulong redFootprints, ulong blueFootprints,
                                        PlayerColor sideToMove, int redCredits, int blueCredits,
                                        int turnNumber, int actionCount, int movesRemainingThisTurn,
                                        int abilityActionsUsedThisTurn, GameRulesConfig rules) {
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            var errors = rules.Validate();
            if (errors.Count > 0) throw new ArgumentException("Invalid rules config: " + string.Join("; ", errors));

            if (((red | blue | blocked | redFootprints | blueFootprints) & ~BitboardState.BoardMask) != 0)
                throw new ArgumentException("Masks contain bits outside 7x7 board.");

            if ((red & blue) != 0 || (red & blocked) != 0 || (blue & blocked) != 0)
                throw new ArgumentException("Board piece masks overlap.");

            ulong occupied = red | blue | blocked;
            if (((redFootprints | blueFootprints) & occupied) != 0)
                throw new ArgumentException("Footprints cannot be on occupied or blocked cells.");

            if ((redFootprints & blueFootprints) != 0)
                throw new ArgumentException("Red and Blue footprints overlap.");

            if (sideToMove != PlayerColor.Red && sideToMove != PlayerColor.Blue)
                throw new ArgumentException("Invalid side to move: " + sideToMove);

            if (turnNumber < 0 || actionCount < 0)
                throw new ArgumentException("Counters cannot be negative.");

            if (turnNumber > actionCount)
                throw new ArgumentException("TurnNumber cannot exceed ActionCount.");

            if (abilityActionsUsedThisTurn < 0 || abilityActionsUsedThisTurn > 1)
                throw new ArgumentException("AbilityActionsUsedThisTurn must be 0 or 1.");

            if (movesRemainingThisTurn < 1 || movesRemainingThisTurn > 2)
                throw new ArgumentException("MovesRemainingThisTurn must be 1 or 2.");

            if (movesRemainingThisTurn == 2 && abilityActionsUsedThisTurn != 1)
                throw new ArgumentException("MovesRemainingThisTurn = 2 requires abilityActionsUsedThisTurn = 1.");

            if (actionCount == 0 && (turnNumber != 0 || abilityActionsUsedThisTurn != 0 || movesRemainingThisTurn != 1))
                throw new ArgumentException("Initial state (actionCount=0) requires turnNumber=0, abilityActions=0, movesRemaining=1.");

            if (rules.economy == null || !rules.economy.enabled) {
                if (redCredits != 0 || blueCredits != 0 || redFootprints != 0 || blueFootprints != 0)
                    throw new ArgumentException("Disabled economy requires zero credits and footprints.");
                if (abilityActionsUsedThisTurn != 0 || movesRemainingThisTurn != 1)
                    throw new ArgumentException("Disabled economy requires zero ability uses and 1 move remaining.");
            } else {
                if (redCredits < 0 || redCredits > rules.economy.maxCredits || blueCredits < 0 || blueCredits > rules.economy.maxCredits)
                    throw new ArgumentException("Credits must be in range [0, maxCredits].");
            }

            var ownedRules = rules.Clone();
            ulong fp = ownedRules.Fingerprint();
            var state = new GameState(ownedRules, fp) {
                Board = new BitboardState {
                    RedPieces = red,
                    BluePieces = blue,
                    BlockedSquares = blocked
                },
                RedFootprints = redFootprints,
                BlueFootprints = blueFootprints,
                RedCredits = redCredits,
                BlueCredits = blueCredits,
                SideToMove = sideToMove,
                TurnNumber = turnNumber,
                ActionCount = actionCount,
                MovesRemainingThisTurn = movesRemainingThisTurn,
                AbilityActionsUsedThisTurn = abilityActionsUsedThisTurn
            };

            state.Board.ZobristHash = ZobristHasher.ComputeHash(state.Board, sideToMove);
            state.Result = AttaxRules.GetTerminalResult(state.Board);

            return state;
        }

        public static GameState CreateInitial(BitboardState board, PlayerColor firstPlayer, GameRulesConfig rules) {
            if (board == null) throw new ArgumentNullException(nameof(board));
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            int startingCredits = rules.economy != null && rules.economy.enabled ? rules.economy.startingCredits : 0;
            return Restore(
                board.RedPieces,
                board.BluePieces,
                board.BlockedSquares,
                0UL,
                0UL,
                firstPlayer,
                startingCredits,
                startingCredits,
                0,
                0,
                1,
                0,
                rules);
        }

        private static ulong FnvFeed(ulong hash, ulong val) {
            unchecked {
                for (int i = 0; i < 8; i++) {
                    byte b = (byte)((val >> (i * 8)) & 0xFF);
                    hash ^= b;
                    hash *= 1099511628211UL;
                }
            }
            return hash;
        }
    }
}
