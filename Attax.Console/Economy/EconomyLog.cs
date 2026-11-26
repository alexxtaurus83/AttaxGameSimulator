using System.Text.Json;
using System.Text.Json.Serialization;

namespace Attax.Console.Economy {
    /// <summary>
    /// Economy simulation log: one JSON object per line (JSONL), <see cref="CurrentSchemaVersion"/> stamped into every record.
    /// It is a separate format from the v4 (ATLG) training log (<c>LogV3</c>), which this tool never touches.
    /// </summary>
    internal static class EconomyLog {
        public const int CurrentSchemaVersion = 1;

        internal static readonly JsonSerializerOptions Json = new JsonSerializerOptions {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        /// <summary>Writes all records to <c>path.tmp</c> and renames it, so a reader never sees a half-written log.</summary>
        public static void Write(string path, IEnumerable<EconomyGameRecord> records) {
            string full = Path.GetFullPath(path);
            string? dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = full + ".tmp";
            using (var w = new StreamWriter(tmp, false, new System.Text.UTF8Encoding(false))) {
                foreach (var r in records) {
                    r.SchemaVersion = CurrentSchemaVersion;
                    w.Write(JsonSerializer.Serialize(r, Json));
                    w.Write('\n');
                }
            }
            if (File.Exists(full)) File.Delete(full);
            File.Move(tmp, full);
        }

        public static List<EconomyGameRecord> Read(string path) {
            var list = new List<EconomyGameRecord>();
            int line = 0;
            foreach (string text in File.ReadLines(path)) {
                line++;
                if (string.IsNullOrWhiteSpace(text)) continue;
                EconomyGameRecord? rec;
                try { rec = JsonSerializer.Deserialize<EconomyGameRecord>(text, Json); }
                catch (JsonException ex) { throw new InvalidDataException($"{path}:{line}: invalid economy log record ({ex.Message})"); }
                if (rec == null) throw new InvalidDataException($"{path}:{line}: empty record.");
                if (rec.SchemaVersion != CurrentSchemaVersion)
                    throw new InvalidDataException($"{path}:{line}: unsupported economy log schemaVersion {rec.SchemaVersion} (expected {CurrentSchemaVersion}).");
                list.Add(rec);
            }
            return list;
        }

        /// <summary>Resolves --in: files as given, directories contribute their top-level *.jsonl only (no .tmp, no subdirectories).</summary>
        public static List<string> ResolveInputs(IEnumerable<string> inputs) {
            var files = new List<string>();
            foreach (string input in inputs) {
                if (Directory.Exists(input)) {
                    var found = Directory.GetFiles(input, "*.jsonl", SearchOption.TopDirectoryOnly);
                    Array.Sort(found, StringComparer.OrdinalIgnoreCase);
                    files.AddRange(found);
                } else if (File.Exists(input) && Path.GetExtension(input).Equals(".jsonl", StringComparison.OrdinalIgnoreCase)) {
                    files.Add(input);
                } else {
                    throw new FileNotFoundException($"Economy log input not found: {input}");
                }
            }
            if (files.Count == 0) throw new InvalidOperationException("No .jsonl economy logs found in --in.");
            return files;
        }
    }

    internal sealed class EconomySideIncome {
        public int OwnTurns { get; set; }
        public int CaptureCredits { get; set; }
        public int FootprintCredits { get; set; }
        public long? CreditsLostToCap { get; set; }
        public List<int>? TurnStartCredits { get; set; }
        public int Flips { get; set; }
        public int CreditsSpent { get; set; }
        public int Casts { get; set; }
        public double SearchMs { get; set; }
        public double AbilityDecideMs { get; set; }
        public int Income => CaptureCredits + FootprintCredits;
    }

    internal sealed class EconomyCastRecord {
        public int Ply { get; set; }
        public string Side { get; set; } = "";
        public string Kind { get; set; } = "";
        public int Level { get; set; }
        public int Cost { get; set; }
        public string Effect { get; set; } = "";
        public string Reason { get; set; } = "";
        public float EstimatedGainChips { get; set; }
        public float NetGainChips { get; set; }
        public int CandidatesVerified { get; set; }
        public bool AutoPassed { get; set; }
    }

    internal sealed class EconomyValueSample {
        public int Ply { get; set; }
        public int OwnTurn { get; set; }
        public string Side { get; set; } = "";
        public int Depth { get; set; }
        public string Kind { get; set; } = "";
        public int Level { get; set; }
        public bool HasCandidate { get; set; }
        public bool HasVerification { get; set; }
        public bool Terminal { get; set; }
        public bool ExceedsCreditCap { get; set; }
        /// <summary>Best positive finite gain in chips (no cost term), absent when there is none.</summary>
        public float? GainChips { get; set; }
    }

    internal sealed class EconomyGameRecord {
        public int SchemaVersion { get; set; }
        public string Tag { get; set; } = "";
        public int Game { get; set; }
        public int SessionGameCount { get; set; }
        public string BoardId { get; set; } = "";
        public int Playable { get; set; }
        public int Seed { get; set; }
        public string Red { get; set; } = "";
        public string Blue { get; set; } = "";
        public bool Swapped { get; set; }
        public bool Abilities { get; set; }
        public string RulesHash { get; set; } = "";
        public string AiConfigHash { get; set; } = "";
        public string EngineParamsHash { get; set; } = "";
        /// <summary>Source provenance embedded at build time, not the current checkout HEAD.</summary>
        public string Revision { get; set; } = "";
        public string BuildId { get; set; } = "";
        public string SamplingMethod { get; set; } = "";
        public string? TelemetryMethod { get; set; }
        public int? OpeningPlies { get; set; }
        public int? OpeningPliesPlayed { get; set; }
        public string? OpeningPosition { get; set; }
        public int RedTurnStarts { get; set; }
        public int BlueTurnStarts { get; set; }
        public int Plies { get; set; }
        public bool Terminal { get; set; }
        public int Winner { get; set; }
        public int RedChips { get; set; }
        public int BlueChips { get; set; }
        public double WallMs { get; set; }
        public double SearchMsRed { get; set; }
        public double SearchMsBlue { get; set; }
        public double AbilityDecideMsRed { get; set; }
        public double AbilityDecideMsBlue { get; set; }
        public double ValueSampleMs { get; set; }
        public EconomySideIncome RedIncome { get; set; } = new EconomySideIncome();
        public EconomySideIncome BlueIncome { get; set; } = new EconomySideIncome();
        public List<EconomyCastRecord> Casts { get; set; } = new List<EconomyCastRecord>();
        public List<EconomyValueSample> Values { get; set; } = new List<EconomyValueSample>();
    }
}
