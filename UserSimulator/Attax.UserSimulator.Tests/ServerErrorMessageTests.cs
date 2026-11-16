using Xunit;

namespace Attax.UserSimulator.Tests {
    /// <summary>The bot reports server errors with the shared player-readable text (Attax.Client.Protocol.ClientErrorTexts).</summary>
    public sealed class ServerErrorMessageTests {
        [Fact]
        public void UserMessage_KnownCode_UsesTheSharedText() {
            var ex = new ServerErrorException("ROOM_NOT_FOUND", "raw server message");
            Assert.Equal("Room not found. Check the code and try again.", ex.UserMessage);
            Assert.Equal("Server error [ROOM_NOT_FOUND]: raw server message", ex.Message);
            Assert.Equal("ROOM_NOT_FOUND", ex.Code);
        }

        [Fact]
        public void UserMessage_UnknownCode_FallsBackToTheServerMessage() {
            var ex = new ServerErrorException("SOMETHING_NEW", "raw server message");
            Assert.Equal("raw server message", ex.UserMessage);
        }

        [Fact]
        public void TurnNotReady_UsesSharedText_AndIsStaleState() {
            var ex = new ServerErrorException(Attax.Server.Contracts.Errors.ErrorCodes.Gameplay.TURN_NOT_READY, "raw");
            Assert.Equal("Turn is still preparing. Syncing...", ex.UserMessage);
            Assert.True(Attax.Client.Protocol.ErrorCodeClassifier.IsStaleState(ex.Code));
        }
    }
}
