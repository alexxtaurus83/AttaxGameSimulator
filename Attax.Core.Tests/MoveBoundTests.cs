using System;
using Attax.Core;
using Xunit;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core.Tests {
    public class MoveBoundTests {
        [Fact]
        public void ProvenBound_IsBelowBuffer() {
            // jumps <= 16 * min(P, E); clone destinations <= E; P + E <= 49.
            int worst = 0;
            for (int p = 0; p <= 49; p++) {
                int e = 49 - p;
                worst = Math.Max(worst, 16 * Math.Min(p, e) + e);
            }
            Assert.Equal(409, worst);
            Assert.True(worst < AtaxxThreadHelper.MaxMovesBuffer);
        }

        [Fact]
        public void HillClimb_NeverExceedsProvenBound() {
            var engine = TestBoards.NewEngine();
            var rng = new Random(11);
            int best = 0;
            for (int restart = 0; restart < 40; restart++) {
                ulong mask = 0;
                for (int i = 0; i < rng.Next(5, 35); i++) mask |= 1UL << rng.Next(49);
                int cur = engine.GetAllValidMoves(new BitboardState { RedPieces = mask }, PlayerColor.Red).Count;
                for (int step = 0; step < 3000; step++) {
                    ulong cand = mask ^ (1UL << rng.Next(49));
                    if (cand == 0) continue;
                    int c = engine.GetAllValidMoves(new BitboardState { RedPieces = cand }, PlayerColor.Red).Count;
                    if (c >= cur) { mask = cand; cur = c; }
                }
                best = Math.Max(best, cur);
            }
            Assert.True(best <= 409, $"found {best} moves, above the proven bound 409");
        }
    }
}
