using System;
using Xunit;
using Attax.Core;

namespace Attax.Core.Tests {
    public class PolicyLogitSelectorTests {
        [Fact]
        public void SelectGreedy_NoLegalMoves_ReturnsHasMoveFalse() {
            var b = new BitboardState(); // completely empty, no pieces to move
            var logits = new float[ActionCodec.ActionCount];
            var choice = PolicyLogitSelector.SelectGreedy(logits, b, PlayerColor.Red);
            Assert.False(choice.HasMove);
        }

        [Fact]
        public void SelectGreedy_ReturnsHighestLegalLogit() {
            var b = new BitboardState();
            b.RedPieces |= 1UL; // red piece at 0
            var logits = new float[ActionCodec.ActionCount];
            Array.Fill(logits, -100f);
            
            // set up two legal moves
            int a1 = ActionCodec.Encode(0, 1);
            int a2 = ActionCodec.Encode(0, 7);
            
            logits[a1] = 10f;
            logits[a2] = 20f; // best legal
            
            // highest illegal logit
            logits[ActionCodec.Encode(2, 3)] = 100f; // not legal
            
            var choice = PolicyLogitSelector.SelectGreedy(logits, b, PlayerColor.Red);
            Assert.True(choice.HasMove);
            Assert.Equal(a2, choice.Action);
            Assert.Equal(20f, logits[choice.Action]);
        }

        [Fact]
        public void SelectGreedy_ThrowsOnNaN() {
            var b = new BitboardState();
            b.RedPieces |= 1UL;
            var logits = new float[ActionCodec.ActionCount];
            logits[0] = float.NaN;
            Assert.Throws<InvalidOperationException>(() => PolicyLogitSelector.SelectGreedy(logits, b, PlayerColor.Red));
        }
    }
}
