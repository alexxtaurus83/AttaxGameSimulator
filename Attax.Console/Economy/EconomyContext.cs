using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Attax.Core.Utils;
using Attax.Play;
using Newtonsoft.Json;

namespace Attax.Console.Economy {
    /// <summary>Loaded rules and AI config plus where they came from, their hashes and the source revision (stamped into every log record).</summary>
    internal sealed class EconomyContext {
        public GameRulesConfig Rules { get; }
        public AiAbilityConfig Ai { get; }
        public string RulesPath { get; }
        public string AiPath { get; }
        /// <summary>True when each file was named explicitly or is the AttaxShared source file (never a resolved bin/publish copy); required by --apply.</summary>
        public bool FromSource { get; }
        public string RulesHash => Rules.Fingerprint().ToString("X16");
        public string AiHash { get; }
        public string EngineParamsHash { get; }
        public string Revision { get; }
        public string BuildId { get; } = Sha(string.Join("|", new[] {
            typeof(EconomyContext), typeof(Attax.Agents.RulesGameRunner), typeof(Attax.Core.AtaxxAIEngine),
            typeof(GameState), typeof(PlayerFactory), typeof(Attax.Data.LogV3Reader)
        }.Select(type => type.Module.ModuleVersionId.ToString("N"))));

        private EconomyContext(GameRulesConfig rules, AiAbilityConfig ai, string rulesPath, string aiPath, bool fromSource, string aiHash, string engineHash, string revision) {
            Rules = rules; Ai = ai; RulesPath = rulesPath; AiPath = aiPath; FromSource = fromSource; AiHash = aiHash; EngineParamsHash = engineHash; Revision = revision;
        }

        public const string RulesFile = "game-rules.json";
        public const string AiFile = "ai-ability-config.json";

        public static EconomyContext Load(string? rulesPath, string? aiPath) {
            string? sourceDir = FindSharedRulesDirectory();
            bool rulesExplicit = !string.IsNullOrWhiteSpace(rulesPath), aiExplicit = !string.IsNullOrWhiteSpace(aiPath);
            string rp = rulesExplicit ? rulesPath! : Resolve(sourceDir, RulesFile);
            string ap = aiExplicit ? aiPath! : Resolve(sourceDir, AiFile);
            if (!File.Exists(rp)) throw new FileNotFoundException($"Rules file not found: {rp}");
            if (!File.Exists(ap)) throw new FileNotFoundException($"AI ability config not found: {ap}");
            rp = Path.GetFullPath(rp); ap = Path.GetFullPath(ap);

            var rules = JsonConvert.DeserializeObject<GameRulesConfig>(File.ReadAllText(rp)) ?? throw new InvalidDataException($"{rp} is empty.");
            var rulesErrors = rules.Validate();
            if (rulesErrors.Count > 0) throw new InvalidDataException($"Invalid rules {rp}: {string.Join("; ", rulesErrors)}");
            var aiText = File.ReadAllText(ap);
            var ai = JsonConvert.DeserializeObject<AiAbilityConfig>(aiText) ?? throw new InvalidDataException($"{ap} is empty.");
            var aiErrors = ai.Validate();
            if (aiErrors.Count > 0) throw new InvalidDataException($"Invalid AI config {ap}: {string.Join("; ", aiErrors)}");

            // --apply may write a file that was named explicitly, or the AttaxShared source file; never a bin/publish copy.
            bool fromSource = (rulesExplicit || (sourceDir != null && IsUnder(rp, sourceDir))) && (aiExplicit || (sourceDir != null && IsUnder(ap, sourceDir)));
            string engineHash = EngineParamsFile.Fingerprint(EngineParamsFile.LoadBase());
            return new EconomyContext(rules, ai, rp, ap, fromSource, Sha(aiText), engineHash, ResolveRevision());
        }

        /// <summary>The same loaded files with other rules/AI values (calibration candidates); hashes follow the new values.</summary>
        public EconomyContext With(GameRulesConfig rules, AiAbilityConfig ai) =>
            new EconomyContext(rules, ai, RulesPath, AiPath, FromSource, Sha(JsonConvert.SerializeObject(ai)), EngineParamsHash, Revision);

        private static string Resolve(string? sourceDir, string name) =>
            sourceDir != null ? Path.Combine(sourceDir, name) : Path.Combine(AppContext.BaseDirectory, name);

        private static bool IsUnder(string path, string dir) =>
            Path.GetFullPath(path).StartsWith(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        internal static string Sha(string text) {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")))).Substring(0, 16);
        }

        /// <summary>The AttaxShared <c>src/Attax.Core.Utils/Rules</c> directory (env ATTAX_SHARED_REPO, or a sibling found above the exe/cwd), or null.</summary>
        internal static string? FindSharedRulesDirectory() {
            string? env = Environment.GetEnvironmentVariable("ATTAX_SHARED_REPO");
            if (!string.IsNullOrWhiteSpace(env)) {
                string d = Path.Combine(env, "src", "Attax.Core.Utils", "Rules");
                if (File.Exists(Path.Combine(d, RulesFile))) return Path.GetFullPath(d);
            }
            foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() }) {
                var dir = new DirectoryInfo(start);
                while (dir != null) {
                    string d = Path.Combine(dir.FullName, "AttaxShared", "src", "Attax.Core.Utils", "Rules");
                    if (File.Exists(Path.Combine(d, RulesFile))) return d;
                    d = Path.Combine(dir.FullName, "..", "AttaxShared", "src", "Attax.Core.Utils", "Rules");
                    if (File.Exists(Path.Combine(d, RulesFile))) return Path.GetFullPath(d);
                    dir = dir.Parent;
                }
            }
            return null;
        }

        /// <summary>The AttaxUnity economy document (env ATTAX_UNITY_REPO, a sibling checkout, or the documented workspace layout), or null.</summary>
        internal static string? FindEconomyDoc() {
            var candidates = new List<string>();
            string? env = Environment.GetEnvironmentVariable("ATTAX_UNITY_REPO");
            if (!string.IsNullOrWhiteSpace(env)) candidates.Add(Path.Combine(env, "docs", "ECONOMY.md"));
            foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() }) {
                var dir = new DirectoryInfo(start);
                while (dir != null) {
                    candidates.Add(Path.Combine(dir.FullName, "AttaxUnity", "docs", "ECONOMY.md"));
                    candidates.Add(Path.Combine(dir.FullName, "Unity_projects", "AttaxUnity", "docs", "ECONOMY.md"));
                    dir = dir.Parent;
                }
            }
            return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        }

        private static string ResolveRevision() {
            var attributes = typeof(EconomyContext).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();
            string Value(string key) => attributes.FirstOrDefault(a => a.Key == key)?.Value ?? "unknown";
            return $"shared={Value("EconomySharedRevision")};simulator={Value("EconomySimulatorRevision")};state={Value("EconomySourceState")}";
        }
    }
}
