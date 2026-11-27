using System.Security.Cryptography;
using System.Text;
using Attax.Client.Protocol;
using Attax.Core.Utils;
using Attax.Play;

namespace Attax.Console.Economy {
    /// <summary>Simulation inputs and fingerprints written to the JSONL log.</summary>
    internal sealed class EconomyContext {
        public GameRulesConfig Rules { get; }
        public AiAbilityConfig Ai { get; }
        public string RulesHash => Rules.Fingerprint().ToString("X16");
        public string AiHash => Ai.Fingerprint().ToString("X16");
        public string EngineParamsHash { get; }
        public string BuildId { get; } = Sha(string.Join("|", new[] {
            typeof(EconomyContext), typeof(Attax.Agents.RulesGameRunner), typeof(Attax.Core.AtaxxAIEngine),
            typeof(GameState), typeof(PlayerFactory), typeof(Attax.Data.LogV3Reader)
        }.Select(type => type.Module.ModuleVersionId.ToString("N"))));

        private EconomyContext(GameRulesConfig rules, AiAbilityConfig ai, string engineHash) {
            Rules = rules; Ai = ai; EngineParamsHash = engineHash;
        }

        public static EconomyContext Load(string? rulesPath, string? aiPath) {
            string rp = string.IsNullOrWhiteSpace(rulesPath) ? Path.Combine(AppContext.BaseDirectory, "game-rules.json") : rulesPath;
            string ap = string.IsNullOrWhiteSpace(aiPath) ? Path.Combine(AppContext.BaseDirectory, "ai-ability-config.json") : aiPath;

            var rules = RulesJsonLoader.LoadGameRules(rp, requireExplicitSchemaVersion: false, forbidAbilityAllowance: false);
            var ai = RulesJsonLoader.LoadAiAbilityConfig(ap);

            string engineHash = EngineParamsFile.Fingerprint(EngineParamsFile.LoadBase());
            return new EconomyContext(rules, ai, engineHash);
        }

        internal static string Sha(string text) {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")))).Substring(0, 16);
        }
    }
}
