using System;
using System.IO;
using System.Text.Json;

namespace Attax.UserSimulator {
    public sealed class BotCredentials {
        public string PlayerId { get; set; } = string.Empty;
        public string RecoverySecret { get; set; } = string.Empty;
    }

    public sealed class CredentialStore {
        private readonly string _stateDir;

        public CredentialStore(string stateDir) {
            _stateDir = stateDir;
            Directory.CreateDirectory(_stateDir);
        }

        private string GetPath(string botName) => Path.Combine(_stateDir, $"{botName}.json");

        public BotCredentials? Load(string botName) {
            string path = GetPath(botName);
            if (!File.Exists(path)) return null;
            try {
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<BotCredentials>(json);
            } catch {
                return null;
            }
        }

        public void Save(string botName, string playerId, string recoverySecret) {
            string path = GetPath(botName);
            var creds = new BotCredentials { PlayerId = playerId, RecoverySecret = recoverySecret };
            string json = JsonSerializer.Serialize(creds, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
    }
}
