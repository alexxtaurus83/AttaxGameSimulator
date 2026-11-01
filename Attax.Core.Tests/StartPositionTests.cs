using System;
using System.Collections.Generic;
using Attax.Core.Utils;
using Xunit;

namespace Attax.Core.Tests {
    public class StartPositionTests {
        [Fact]
        public void Standard_SetsExpectedPieces_AndRedHash() {
            var standard = StartPosition.Standard(PlayerColor.Red);
            Assert.Equal((1UL << 0) | (1UL << 48), standard.RedPieces);
            Assert.Equal((1UL << 6) | (1UL << 42), standard.BluePieces);
            Assert.Equal(0UL, standard.BlockedSquares);
            Assert.Equal(ZobristHasher.ComputeHash(standard, PlayerColor.Red), standard.ZobristHash);
        }

        [Fact]
        public void Standard_BlueFirst_HasDifferentHash() {
            var redStart = StartPosition.Standard(PlayerColor.Red);
            var blueStart = StartPosition.Standard(PlayerColor.Blue);
            Assert.Equal(redStart.RedPieces, blueStart.RedPieces);
            Assert.Equal(redStart.BluePieces, blueStart.BluePieces);
            Assert.NotEqual(redStart.ZobristHash, blueStart.ZobristHash);
            Assert.Equal(redStart.ZobristHash ^ ZobristHasher.GetSideToMoveKey(), blueStart.ZobristHash);
        }

        [Fact]
        public void Create_ZeroBlocked_ReturnsStandard() {
            var b = StartPosition.Create(0, 42, PlayerColor.Red);
            Assert.Equal(0UL, b.BlockedSquares);
            Assert.Equal(ZobristHasher.ComputeHash(b, PlayerColor.Red), b.ZobristHash);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(6)]
        [InlineData(12)]
        public void Create_BlocksOnlyEmptySquares_AndCountMatches(int count) {
            for (int seed = 0; seed < 10; seed++) {
                var b = StartPosition.Create(count, seed, PlayerColor.Red);
                ulong occupied = b.RedPieces | b.BluePieces;
                Assert.Equal(0UL, b.BlockedSquares & occupied);
                Assert.Equal(count, BitboardOps.PopCount(b.BlockedSquares));
                Assert.Equal(ZobristHasher.ComputeHash(b, PlayerColor.Red), b.ZobristHash);
            }
        }

        [Fact]
        public void VerifyOpeningCompatibility_Succeeds() {
            bool ok = EngineInfo.VerifyOpeningCompatibility(out string error);
            Assert.True(ok, error);
            Assert.Empty(error);
        }
    }
}
