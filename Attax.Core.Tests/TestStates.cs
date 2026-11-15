using Attax.Core.Utils;
using Xunit;

namespace Attax.Core.Tests {
    /// <summary>
    /// Engine-test fixtures. States are composed from the public StartPosition and GameState.Restore APIs;
    /// the server owns the production NewGame and integrity helpers.
    /// </summary>
    internal static class TestStates {
        public static GameState NewGame(GameRulesConfig rules, int blockedCells, int seed, PlayerColor firstPlayer = PlayerColor.Red) {
            BitboardState board = StartPosition.Create(blockedCells, seed, firstPlayer);
            return GameState.CreateInitial(board, firstPlayer, rules);
        }

        public static bool IsHashConsistent(GameState state) {
            return state.Board.ZobristHash == ZobristHasher.ComputeHash(state.Board, state.SideToMove);
        }

        /// <summary>
        /// Asserts the state is a valid engine state: the incremental hash matches a recomputation and the state survives
        /// a Restore round trip with the same fingerprint, and economy credits remain within their cap.
        /// </summary>
        public static void AssertInvariants(GameState state) {
            Assert.True(IsHashConsistent(state));
            var rules = state.ExportRules();
            if (rules.economy != null && rules.economy.enabled) {
                Assert.InRange(state.RedCredits, 0, rules.economy.maxCredits);
                Assert.InRange(state.BlueCredits, 0, rules.economy.maxCredits);
            }
            GameState restored = GameState.Restore(
                state.Board.RedPieces, state.Board.BluePieces, state.Board.BlockedSquares,
                state.RedFootprints, state.BlueFootprints, state.SideToMove,
                state.RedCredits, state.BlueCredits, state.TurnNumber,
                state.ActionCount, state.MovesRemainingThisTurn, state.AbilityActionsUsedThisTurn,
                rules);
            Assert.Equal(state.ComputeFingerprint(), restored.ComputeFingerprint());
        }
    }
}
