using System;
using System.Text.RegularExpressions;
using CommandLine;

namespace Attax.UserSimulator {
    public sealed class BotOptions {
        [Option("server", Default = "http://localhost:5000", HelpText = "Server base URL.")]
        public string Server { get; set; } = "http://localhost:5000";

        [Option("name", Required = true, HelpText = "Bot name, e.g. bot1.")]
        public string Name { get; set; } = "";

        [Option("mode", Default = "lobby", HelpText = "Mode: lobby or private.")]
        public string Mode { get; set; } = "lobby";

        [Option("role", HelpText = "Role for private mode: host or guest.")]
        public string? Role { get; set; }

        [Option("room-code", HelpText = "Specific room code to join.")]
        public string? RoomCode { get; set; }

        [Option("rooms-dir", Default = "./sim-rooms", HelpText = "Directory where host writes room codes.")]
        public string RoomsDir { get; set; } = "./sim-rooms";

        [Option("spec", Default = "classic:depth=3", HelpText = "PlayerSpec string.")]
        public string Spec { get; set; } = "classic:depth=3";

        [Option("abilities", Default = "off", HelpText = "Abilities: off or policy.")]
        public string Abilities { get; set; } = "off";

        [Option("ability-config", Default = "ai-ability-config.json", HelpText = "Path to ai-ability-config.json.")]
        public string AbilityConfig { get; set; } = "ai-ability-config.json";

        [Option("games", Default = 0, HelpText = "Games to play (0 = forever).")]
        public int Games { get; set; } = 0;

        [Option("think-ms", Default = "1000-4000", HelpText = "Think delay range min-max in ms.")]
        public string ThinkMs { get; set; } = "1000-4000";

        [Option("challenge-probability", Default = 0.5, HelpText = "Probability to issue challenge on idle tick.")]
        public double ChallengeProbability { get; set; } = 0.5;

        [Option("lobby-poll-ms", Default = 3000, HelpText = "Lobby polling interval in ms.")]
        public int LobbyPollMs { get; set; } = 3000;

        [Option("lobby-behavior", Default = "wait", HelpText = "Lobby behavior: 'wait' (idle, waits for challenges and auto-accepts) or 'search' (actively polls and challenges free players).")]
        public string LobbyBehavior { get; set; } = "wait";

        [Option("seed", Default = 0, HelpText = "RNG seed (0 = random).")]
        public int Seed { get; set; } = 0;

        [Option("state-dir", Default = "./bot-state", HelpText = "Credentials store directory.")]
        public string StateDir { get; set; } = "./bot-state";

        [Option("new-identity", Default = false, HelpText = "Ignore stored credentials and register fresh session.")]
        public bool NewIdentity { get; set; } = false;

        [Option("client-version", Default = "1.0.0", HelpText = "Client version sent in headers.")]
        public string ClientVersion { get; set; } = "1.0.0";

        [Option("log-dir", Default = "./logs/bots", HelpText = "Directory for bot logs.")]
        public string LogDir { get; set; } = "./logs/bots";

        [Option("console-level", Default = "Information", HelpText = "Console log level.")]
        public string ConsoleLevel { get; set; } = "Information";

        [Option("file-level", Default = "Debug", HelpText = "File log level.")]
        public string FileLevel { get; set; } = "Debug";

        [Option("cores", HelpText = "Number of CPU cores/threads for AI search (1..64). Overrides or sets cores in --spec.")]
        public int? Cores { get; set; }

        [Option("affinity", HelpText = "Processor affinity mask in hex (optional OS-level restriction, e.g. 0x0F).")]
        public string? Affinity { get; set; }

        public (int Min, int Max) ParsedThinkMs { get; private set; }

        public void Validate() {
            if (string.IsNullOrWhiteSpace(Name) || !Regex.IsMatch(Name, "^[A-Za-z0-9_-]{1,32}$")) {
                throw new ArgumentException($"--name must match ^[A-Za-z0-9_-]{{1,32}}$, got '{Name}'.");
            }

            Mode = Mode.ToLowerInvariant();
            if (Mode != "lobby" && Mode != "private") {
                throw new ArgumentException($"--mode must be 'lobby' or 'private', got '{Mode}'.");
            }

            if (Mode == "private") {
                if (string.IsNullOrWhiteSpace(Role)) {
                    throw new ArgumentException("--role (host or guest) is required when --mode is 'private'.");
                }
                Role = Role.ToLowerInvariant();
                if (Role != "host" && Role != "guest") {
                    throw new ArgumentException($"--role must be 'host' or 'guest', got '{Role}'.");
                }
            }

            if (Cores.HasValue && (Cores.Value < 1 || Cores.Value > 64)) {
                throw new ArgumentException($"--cores must be between 1 and 64, got '{Cores.Value}'.");
            }

            LobbyBehavior = LobbyBehavior.ToLowerInvariant();
            if (LobbyBehavior is not ("wait" or "idle" or "passive" or "search" or "active" or "challenge")) {
                throw new ArgumentException($"--lobby-behavior must be 'wait' or 'search', got '{LobbyBehavior}'.");
            }
            if (LobbyBehavior is "idle" or "passive") LobbyBehavior = "wait";
            if (LobbyBehavior is "active" or "challenge") LobbyBehavior = "search";

            var match = Regex.Match(ThinkMs, @"^(\d+)-(\d+)$");
            if (!match.Success) {
                throw new ArgumentException($"--think-ms must be in min-max format, e.g. 1000-4000, got '{ThinkMs}'.");
            }
            int min = int.Parse(match.Groups[1].Value);
            int max = int.Parse(match.Groups[2].Value);
            if (min < 0 || max < min) {
                throw new ArgumentException($"--think-ms min must be >= 0 and max >= min, got '{ThinkMs}'.");
            }
            ParsedThinkMs = (min, max);

            Abilities = Abilities.ToLowerInvariant();
            if (Abilities != "off" && Abilities != "policy") {
                throw new ArgumentException($"--abilities must be 'off' or 'policy', got '{Abilities}'.");
            }
        }
    }
}
