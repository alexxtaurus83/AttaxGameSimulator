using System;
using System.IO;
using Attax.Server.Contracts.Serialization;
using Newtonsoft.Json;

namespace Attax.UserSimulator {
    public sealed class BotCredentials {
        [JsonProperty("playerId")]
        public string PlayerId { get; set; } = string.Empty;

        [JsonProperty("recoverySecret")]
        public string RecoverySecret { get; set; } = string.Empty;
    }

    public sealed class CredentialStore {
        private readonly string _stateDir;
        private static readonly JsonSerializerSettings Settings = AttaxJsonSettings.Create();

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
                return JsonConvert.DeserializeObject<BotCredentials>(json, Settings);
            } catch {
                return null;
            }
        }

        public void Save(string botName, string playerId, string recoverySecret) {
            string path = GetPath(botName);
            var creds = new BotCredentials { PlayerId = playerId, RecoverySecret = recoverySecret };
            string json = JsonConvert.SerializeObject(creds, Formatting.Indented, Settings);
            File.WriteAllText(path, json);
        }
    }
}
