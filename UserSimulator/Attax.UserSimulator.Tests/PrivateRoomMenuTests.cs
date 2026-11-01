using System;
using System.IO;
using Xunit;

namespace Attax.UserSimulator.Tests {
    public class PrivateRoomMenuTests {
        [Fact]
        public void GuestMenu_RefreshesAndSelectsRoom() {
            string roomsDir = Path.Combine(Path.GetTempPath(), "test_rooms_" + Guid.NewGuid().ToString("N"));
            try {
                var file1 = new RoomCodeFile {
                    RoomCode = "ABC123",
                    RoomId = "r1",
                    HostName = "host1",
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10)
                };
                RoomCodeFolder.Write(roomsDir, file1);

                var list = RoomCodeFolder.ListValid(roomsDir, DateTimeOffset.UtcNow);
                Assert.Single(list);
                Assert.Equal("ABC123", list[0].RoomCode);

                RoomCodeFolder.Delete(roomsDir, "ABC123");
                Assert.Empty(RoomCodeFolder.ListValid(roomsDir, DateTimeOffset.UtcNow));
            } finally {
                if (Directory.Exists(roomsDir)) Directory.Delete(roomsDir, true);
            }
        }
    }
}
