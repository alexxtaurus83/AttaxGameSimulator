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
            var cfg = new AIEngineConfig { AiDepth = 2, TrainingMode = true, DisableParallelRootSearch = !parallelRoot, DisableQuiescenceSearch = true, Seed = 1 };
            var e = new AtaxxAIEngine(new HeuristicEvaluator(), null, coordinator, cfg);
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
    }
}
