using Attax.Core;
using Xunit;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core.Tests {
    public class TerminalRuleTests {
        private readonly AtaxxAIEngine e = TestBoards.NewEngine();

        [Fact]
        public void NormalPosition_IsNotOver() {
            var b = TestBoards.Parse("R.....B", ".......", ".......", ".......", ".......", ".......", "B.....R");
            Assert.Equal(TerminalResult.NotOver, e.GetTerminalResult(b));
        }

        [Fact]
        public void BlueWipedOut_RedWins_AndViceVersa() {
            var b1 = TestBoards.Parse("R......", ".......", ".......", ".......", ".......", ".......", ".......");
            Assert.Equal(TerminalResult.RedWins, e.GetTerminalResult(b1));
            var b2 = TestBoards.Parse("B......", ".......", ".......", ".......", ".......", ".......", ".......");
            Assert.Equal(TerminalResult.BlueWins, e.GetTerminalResult(b2));
        }

        [Fact]
        public void StuckSideLoses_EvenWithMaterialMajority() {
            // Red has 4 pieces locked in the corner by blocks and its own pieces, cannot move.
            // Blue has 1 piece with free moves. Red has the majority but is stuck, so Blue wins.
            var b = TestBoards.Parse(
                "RRXX...",
                "RRXX...",
                "XXXX...",
                "XXXX...",
                ".......",
                ".......",
                "......B");
            // Red squares can reach nothing empty: neighbours within 2 are all R or X.
            Assert.Equal(TerminalResult.BlueWins, e.GetTerminalResult(b));
            Assert.True(e.GetTerminalOutcomeFor(b, PlayerColor.Blue) > 0);
            Assert.True(e.GetTerminalOutcomeFor(b, PlayerColor.Red) < 0);
        }

        [Fact]
        public void StuckBlueWithMajority_RedWins() {
            var b = TestBoards.Parse(
                "BBXX...",
                "BBXX...",
                "XXXX...",
                "XXXX...",
                ".......",
                ".......",
                "......R");
            Assert.Equal(TerminalResult.RedWins, e.GetTerminalResult(b));
        }

        [Fact]
        public void FullBoard_ComparesCounts_EqualIsDraw() {
            // 49 squares cannot split evenly, so block one square to get 24 vs 24.
            var rows = new string[7];
            for (int y = 0; y < 7; y++) rows[y] = new string(y < 3 ? 'R' : 'B', 7);
            rows[3] = "XBBBBBB"; // 21 red (rows 0-2), 6 + 21 = 27 blue
            var more = TestBoards.Parse(rows);
            Assert.Equal(TerminalResult.BlueWins, e.GetTerminalResult(more));

            var even = TestBoards.Parse("RRRRRRR", "RRRRRRR", "RRRRRRR", "RRRRXBB", "BBBBBBB", "BBBBBBB", "BBBBBBB");
            // red 21+4=25? count explicitly below
            int r = AtaxxAIEngine.PopCount(even.RedPieces), bl = AtaxxAIEngine.PopCount(even.BluePieces);
            Assert.Equal(r > bl ? TerminalResult.RedWins : bl > r ? TerminalResult.BlueWins : TerminalResult.Draw, e.GetTerminalResult(even));
        }

        [Fact]
        public void BothStuckWithEmptiesLeft_ComparesCounts() {
            // Red block and Blue block walled off from each other; neither can reach an empty square.
            var b = TestBoards.Parse(
                "RR.....",
                "RR.....",
                "XXX....",
                "XXX....",
                "XXX....",
                "XXX..BB",
                "XXX..BB");
            // Empties exist on the board but are out of reach of both sides (distance > 2 from each block).
            Assert.Equal(TerminalResult.NotOver, e.GetTerminalResult(TestBoards.Parse(
                "R......", ".......", ".......", ".......", ".......", ".......", "......B")));
            var stuckBoth = TestBoards.Parse(
                "RRXXXXX",
                "RRXXXXX",
                "XXXXXXX",
                "XXXXXXX",
                "XXXXXXX",
                "XXXXXBB",
                "XXXXXBB");
            Assert.Equal(TerminalResult.Draw, e.GetTerminalResult(stuckBoth));
        }

        [Theory]
        [InlineData(PlayerColor.Red)]
        [InlineData(PlayerColor.Blue)]
        public void ImmediateElimination_IsFoundForBothColors(PlayerColor mover) {
            // Mover has one piece next to the enemy's lone piece: any capturing clone wipes the enemy out.
            char me = mover == PlayerColor.Red ? 'R' : 'B';
            char opp = mover == PlayerColor.Red ? 'B' : 'R';
            var b = TestBoards.Parse(
                "" + me + opp + ".....", ".......", ".......", ".......", ".......", ".......", ".......");
            var engine = TestBoards.NewEngine(depth: 3);
            TestBoards.SetBoard(engine, b, mover);
            engine.AIPlayerColor = mover;
            var move = engine.GetBestMove(mover);
            var after = b.Clone();
            engine.MakeMove(after, move, mover);
            int oppLeft = AtaxxAIEngine.PopCount(mover == PlayerColor.Red ? after.BluePieces : after.RedPieces);
            Assert.Equal(0, oppLeft);
        }
    }
}
