using System;
using System.Collections.Generic;
using System.IO;
using Attax.Server.Contracts.Serialization;
using Newtonsoft.Json;

namespace Attax.UserSimulator {
    public sealed class RoomCodeFile {
        [JsonProperty("roomCode")]
        public string RoomCode { get; set; } = string.Empty;

        [JsonProperty("roomId")]
        public string RoomId { get; set; } = string.Empty;

        [JsonProperty("hostName")]
        public string HostName { get; set; } = string.Empty;

        [JsonProperty("createdAtUtc")]
        public DateTimeOffset CreatedAtUtc { get; set; }

        [JsonProperty("expiresAtUtc")]
        public DateTimeOffset ExpiresAtUtc { get; set; }
    }

    public static class RoomCodeFolder {
        private static readonly JsonSerializerSettings Settings = AttaxJsonSettings.Create();

        public static string Write(string roomsDir, RoomCodeFile file) {
            Directory.CreateDirectory(roomsDir);
            string path = Path.Combine(roomsDir, $"{file.RoomCode}.json");
            string json = JsonConvert.SerializeObject(file, Formatting.Indented, Settings);
            File.WriteAllText(path, json);
            return path;
        }

        public static void Delete(string roomsDir, string roomCode) {
            string path = Path.Combine(roomsDir, $"{roomCode}.json");
            if (File.Exists(path)) {
                try { File.Delete(path); } catch { }
            }
        }

        public static List<RoomCodeFile> ListValid(string roomsDir, DateTimeOffset now) {
            var list = new List<RoomCodeFile>();
            if (!Directory.Exists(roomsDir)) return list;

            foreach (var file in Directory.GetFiles(roomsDir, "*.json")) {
                try {
                    string json = File.ReadAllText(file);
                    var item = JsonConvert.DeserializeObject<RoomCodeFile>(json, Settings);
                    if (item == null) continue;

                    if (item.ExpiresAtUtc <= now) {
                        try { File.Delete(file); } catch { }
                        continue;
                    }

                    list.Add(item);
                } catch {
                    // Ignore corrupted file
                }
            }

            list.Sort((a, b) => b.CreatedAtUtc.CompareTo(a.CreatedAtUtc));
            return list;
        }
    }
}
