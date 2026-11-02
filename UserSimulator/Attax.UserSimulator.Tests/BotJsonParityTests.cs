using System;
using System.IO;
using Attax.Server.Contracts.Actions;
using Attax.Server.Contracts.Common;
using Attax.Server.Contracts.Gameplay;
using Attax.Server.Contracts.Serialization;
using Newtonsoft.Json;
using Xunit;

namespace Attax.UserSimulator.Tests;

public class BotJsonParityTests
{
    [Fact]
    public void ServerJson_UsesAttaxJsonSettings_AndProducesParityJson()
    {
        var action = new ActionIntentDto
        {
            Kind = ActionKindDto.Convert,
            FromX = 1,
            FromY = 2,
            ToX = 3,
            ToY = 4
        };

        var serverSettings = AttaxJsonSettings.Create();
        var serverJson = JsonConvert.SerializeObject(action, serverSettings);
        var botJson = JsonConvert.SerializeObject(action, ServerJson.Settings);

        Assert.Equal(serverJson, botJson);
    }

    [Fact]
    public void CredentialStore_LoadsOldFormat_Successfully()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bot_legacy_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "legacyBot.json");
            // Old STJ wrote camelCase or PascalCase properties without json attributes
            var legacyJson = """
            {
              "PlayerId": "p_legacy_123",
              "RecoverySecret": "sec_legacy_456"
            }
            """;
            File.WriteAllText(filePath, legacyJson);

            var store = new CredentialStore(tempDir);
            var loaded = store.Load("legacyBot");

            Assert.NotNull(loaded);
            Assert.Equal("p_legacy_123", loaded.PlayerId);
            Assert.Equal("sec_legacy_456", loaded.RecoverySecret);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void RoomCodeFolder_LoadsOldFormat_Successfully()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "room_legacy_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "ABCD.json");
            var now = DateTimeOffset.UtcNow;
            var legacyJson = $$"""
            {
              "RoomCode": "ABCD",
              "RoomId": "rm_123",
              "HostName": "HostBot",
              "CreatedAtUtc": "{{now:O}}",
              "ExpiresAtUtc": "{{now.AddMinutes(5):O}}"
            }
            """;
            File.WriteAllText(filePath, legacyJson);

            var list = RoomCodeFolder.ListValid(tempDir, now);
            Assert.Single(list);
            Assert.Equal("ABCD", list[0].RoomCode);
            Assert.Equal("HostBot", list[0].HostName);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
