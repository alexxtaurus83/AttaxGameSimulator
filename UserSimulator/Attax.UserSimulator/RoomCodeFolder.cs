using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Attax.UserSimulator {
    public sealed class RoomCodeFile {
        public string RoomCode { get; set; } = string.Empty;
        public string RoomId { get; set; } = string.Empty;
        public string HostName { get; set; } = string.Empty;
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
    }

    public static class RoomCodeFolder {
        public static string Write(string roomsDir, RoomCodeFile file) {
            Directory.CreateDirectory(roomsDir);
            string path = Path.Combine(roomsDir, $"{file.RoomCode}.json");
            string json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
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
                    var item = JsonSerializer.Deserialize<RoomCodeFile>(json);
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
