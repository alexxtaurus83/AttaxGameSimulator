using System;
using Attax.Client.Protocol;
using Attax.Core.Utils;
using Attax.Server.Contracts.Gameplay;

namespace Attax.UserSimulator {
    /// <summary>Bot-side snapshot restore: throws on snapshot errors or fingerprint mismatch (the bot has no recovery path).</summary>
    internal static class BotSnapshot {
        public static GameState ToState(GameSnapshotDto snapshot) {
            var result = SnapshotMapper.ToGameState(snapshot);
            if (!result.IsValid) {
                throw new InvalidOperationException("Failed to restore GameState from snapshot: " + result.Error);
            }
            return result.State;
        }
    }
}
