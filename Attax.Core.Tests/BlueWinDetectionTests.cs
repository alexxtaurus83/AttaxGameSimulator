using System.Linq;
using Attax.Core;
using Xunit;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core.Tests {
    public class BlueWinDetectionTests {
        private const int ImmediateWinScore = int.MaxValue - 1000; // mirrors the private engine constant

        [Theory]
        [InlineData(PlayerColor.Red, false)]
        [InlineData(PlayerColor.Blue, false)]
        [InlineData(PlayerColor.Red, true)]
        [InlineData(PlayerColor.Blue, true)]
        public void EliminatingMove_GetsImmediateWinScore_ForBothColors(PlayerColor mover, bool parallelRoot) {
            char me = mover == PlayerColor.Red ? 'R' : 'B';
            char op = mover == PlayerColor.Red ? 'B' : 'R';
            var b = TestBoards.Parse(
                "" + op + "..." + me + me + me,
                "..." + me + me + me + me,
                ".......", ".......", ".......", ".......", ".......");

            var coordinator = new AILogCoordinator();
            var cfg = new AIEngineConfig { DisableRandomRootTies = true, AiDepth = 2, TrainingMode = true, DisableParallelRootSearch = !parallelRoot, DisableQuiescenceSearch = true, Seed = 1 };
            var e = new AtaxxAIEngine(Prm.Base(), cfg, null, null, coordinator);
            TestBoards.SetBoard(e, b, mover);
            e.AIPlayerColor = mover;

            var move = e.GetBestMove(mover);
            var after = b.Clone();
            e.MakeMove(after, move, mover);
            Assert.Equal(0, AtaxxAIEngine.PopCount(mover == PlayerColor.Red ? after.BluePieces : after.RedPieces));

            var turn = coordinator.GlobalLog[0];
            var details = (turn.opponentMove ?? turn.playerMove).aIMoveDetails;
            var chosen = details.aiMoveCandidates.Values.First(c => c.move.Equals(move));
            Assert.Equal(ImmediateWinScore, chosen.finalScore);
        }

        [Fact]
        public void MoveBlockingOpponent_AheadScoresImmediateWinScore_BehindDoesNot() {
            // Case 1: Red is ahead (Red 2 chips, Blue 1 chip).
            // Blue has 1 piece at (0,0). All reachables blocked except (2,2).
            // Red at (2,3) and (6,6). Red clones to (2,2): Blue is trapped, Red has 3 chips vs Blue 1 chip.
            var bAhead = TestBoards.Parse(
                "BXX....",
                "XXX....",
                "XX.....",
                "..R....",
                ".......",
                ".......",
                "......R");

            var coordinatorAhead = new AILogCoordinator();
            var cfgAhead = new AIEngineConfig { DisableRandomRootTies = true, AiDepth = 1, TrainingMode = true, DisableParallelRootSearch = true, DisableQuiescenceSearch = true, Seed = 1 };
            var eAhead = new AtaxxAIEngine(Prm.Base(), cfgAhead, null, null, coordinatorAhead);
            TestBoards.SetBoard(eAhead, bAhead, PlayerColor.Red);
            eAhead.AIPlayerColor = PlayerColor.Red;

            var moveAhead = new Move(2, 3, 2, 2);
            eAhead.GetBestMove(PlayerColor.Red);

            var turnAhead = coordinatorAhead.GlobalLog[0];
            var detailsAhead = (turnAhead.opponentMove ?? turnAhead.playerMove).aIMoveDetails;
            var candidateAhead = detailsAhead.aiMoveCandidates.Values.First(c => c.move.Equals(moveAhead));
            Assert.Equal(ImmediateWinScore, candidateAhead.finalScore);

            // Case 2: Red is behind (Red 1 chip at (2,3), Blue 3 chips: (0,0) trapped except (2,2), plus (5,6) and (6,6) trapped).
            // Red clones to (2,2): Blue is trapped, but Blue has 3 chips vs Red 2 chips. Red loses -> not ImmediateWinScore.
            var bBehind = TestBoards.Parse(
                "BXX....",
                "XXX....",
                "XX.....",
                "..R....",
                "....XXX",
                "....XXB",
                "....XBB");

            var coordinatorBehind = new AILogCoordinator();
            var cfgBehind = new AIEngineConfig { DisableRandomRootTies = true, AiDepth = 1, TrainingMode = true, DisableParallelRootSearch = true, DisableQuiescenceSearch = true, Seed = 1 };
            var eBehind = new AtaxxAIEngine(Prm.Base(), cfgBehind, null, null, coordinatorBehind);
            TestBoards.SetBoard(eBehind, bBehind, PlayerColor.Red);
            eBehind.AIPlayerColor = PlayerColor.Red;

            var moveBehind = new Move(2, 3, 2, 2);
            eBehind.GetBestMove(PlayerColor.Red);

            var turnBehind = coordinatorBehind.GlobalLog[0];
            var detailsBehind = (turnBehind.opponentMove ?? turnBehind.playerMove).aIMoveDetails;
            var candidateBehind = detailsBehind.aiMoveCandidates.Values.First(c => c.move.Equals(moveBehind));
            Assert.NotEqual(ImmediateWinScore, candidateBehind.finalScore);
        }
    }
}
