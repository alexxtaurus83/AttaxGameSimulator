using System.Security.Cryptography;
using System.Text;
using Attax.Core.Utils;
using Attax.Play;
using Newtonsoft.Json;

namespace Attax.Console.Economy {
    /// <summary>Simulation inputs and fingerprints written to the JSONL log.</summary>
    internal sealed class EconomyContext {
        public GameRulesConfig Rules { get; }
        public AiAbilityConfig Ai { get; }
        public string RulesHash => Rules.Fingerprint().ToString("X16");
        public string AiHash { get; }
        public string EngineParamsHash { get; }
        public string BuildId { get; } = Sha(string.Join("|", new[] {
            typeof(EconomyContext), typeof(Attax.Agents.RulesGameRunner), typeof(Attax.Core.AtaxxAIEngine),
            typeof(GameState), typeof(PlayerFactory), typeof(Attax.Data.LogV3Reader)
        }.Select(type => type.Module.ModuleVersionId.ToString("N"))));

        private EconomyContext(GameRulesConfig rules, AiAbilityConfig ai, string aiHash, string engineHash) {
            Rules = rules; Ai = ai; AiHash = aiHash; EngineParamsHash = engineHash;
        }

        public static EconomyContext Load(string? rulesPath, string? aiPath) {
            string rp = string.IsNullOrWhiteSpace(rulesPath) ? Path.Combine(AppContext.BaseDirectory, "game-rules.json") : rulesPath;
            string ap = string.IsNullOrWhiteSpace(aiPath) ? Path.Combine(AppContext.BaseDirectory, "ai-ability-config.json") : aiPath;
            if (!File.Exists(rp)) throw new FileNotFoundException($"Rules file not found: {rp}");
            if (!File.Exists(ap)) throw new FileNotFoundException($"AI ability config not found: {ap}");

            var rules = JsonConvert.DeserializeObject<GameRulesConfig>(File.ReadAllText(rp)) ?? throw new InvalidDataException($"{rp} is empty.");
            var rulesErrors = rules.Validate();
            if (rulesErrors.Count > 0) throw new InvalidDataException($"Invalid rules {rp}: {string.Join("; ", rulesErrors)}");
            var aiText = File.ReadAllText(ap);
            var ai = JsonConvert.DeserializeObject<AiAbilityConfig>(aiText) ?? throw new InvalidDataException($"{ap} is empty.");
            var aiErrors = ai.Validate();
            if (aiErrors.Count > 0) throw new InvalidDataException($"Invalid AI config {ap}: {string.Join("; ", aiErrors)}");

            string engineHash = EngineParamsFile.Fingerprint(EngineParamsFile.LoadBase());
            return new EconomyContext(rules, ai, Sha(aiText), engineHash);
        }

        internal static string Sha(string text) {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")))).Substring(0, 16);
        }
    }
}
