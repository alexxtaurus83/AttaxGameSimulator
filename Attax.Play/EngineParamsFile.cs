using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Attax.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Attax.Play {
    /// <summary>
    /// File and command-line handling for <see cref="EngineParams"/>. This is deliberately NOT in Attax.Core: the game
    /// (Unity) deserialises its own file and hands the object to the engine; the console tools use this class.
    ///
    /// There are no defaults in code. The values live in <c>engine-params.json</c> (the "base file", at the root of the Core folder and
    /// copied next to the console exe). Every layer starts from it: base file, then an optional params=file.json that lists only the
    /// fields it changes, then p.* overrides. Loading is strict: an unknown property, a wrong type or a value that fails validation is
    /// an error that names the file, the property and the line, so a typo can never silently leave a parameter unchanged.
    /// </summary>
    public static class EngineParamsFile {
        private static readonly object cacheLock = new object();
        private static readonly Dictionary<string, (long ticks, long length, long baseStamp, EngineParams value)> cache =
            new Dictionary<string, (long, long, long, EngineParams)>(StringComparer.OrdinalIgnoreCase);

        public const string BaseFileName = "engine-params.json";
        public const string BaseFileEnvVar = "ATTAX_ENGINE_PARAMS";

        private static JsonSerializerSettings ReadSettings => new JsonSerializerSettings {
            MissingMemberHandling = MissingMemberHandling.Error,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            NullValueHandling = NullValueHandling.Ignore,
        };

        /// <summary>Where the base file is looked for: the ATTAX_ENGINE_PARAMS environment variable, else next to the program.</summary>
        public static string BasePath() {
            string env = Environment.GetEnvironmentVariable(BaseFileEnvVar);
            if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
            return Path.Combine(AppContext.BaseDirectory, BaseFileName);
        }

        private static JObject ReadJson(string json, string source) {
            try {
                var token = JToken.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error, CommentHandling = CommentHandling.Ignore });
                if (!(token is JObject o)) throw new InvalidDataException($"{source}: the top level must be a JSON object.");
                return o;
            } catch (JsonException ex) {
                throw new InvalidDataException($"{source}: {ex.Message}", ex);
            }
        }

        private static EngineParams ToParams(JObject obj, string source) {
            EngineParams p;
            try {
                p = obj.ToObject<EngineParams>(JsonSerializer.Create(ReadSettings));
            } catch (JsonException ex) {
                throw new InvalidDataException($"{source}: {ex.Message}", ex);
            }
            if (p == null) throw new InvalidDataException($"{source}: the file is empty.");
            var errors = p.Validate();
            if (errors.Count > 0)
                throw new InvalidDataException($"{source}: invalid engine parameters:{Environment.NewLine}  - {string.Join(Environment.NewLine + "  - ", errors)}");
            return p;
        }

        /// <summary>
        /// Parse and validate JSON text on top of <paramref name="baseParams"/>: only the properties present in the text change, every
        /// other value comes from the base. A table (array) present in the text replaces the whole table. <paramref name="source"/> is
        /// only used in error messages.
        /// </summary>
        public static EngineParams Parse(string json, EngineParams baseParams, string source = "<json>") {
            if (baseParams == null) throw new ArgumentNullException(nameof(baseParams));
            var layer = ReadJson(json, source);
            var merged = JObject.FromObject(baseParams.Clone());
            merged.Merge(layer, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace, MergeNullValueHandling = MergeNullValueHandling.Ignore });
            return ToParams(merged, source);
        }

        /// <summary>Parse a COMPLETE file (every property present), the way the game loads engine-params.json. Missing properties are an error.</summary>
        public static EngineParams ParseComplete(string json, string source = "<json>") {
            var obj = ReadJson(json, source);
            // Compare against the shape of the class so a partial file cannot pass as complete.
            var shape = JObject.FromObject(new EngineParams { eval = new EvalParams { centerControlTable = new int[0] }, root = new RootParams { aggressionMinLead = new int[0], aggressionFactor = new double[0], positionalTable = new int[0] }, search = new SearchParams() });
            var missing = new List<string>();
            CollectMissing(shape, obj, "", missing);
            if (missing.Count > 0) throw new InvalidDataException($"{source}: not a complete parameter file; missing: {string.Join(", ", missing)}.");
            return ToParams(obj, source);
        }

        private static void CollectMissing(JObject shape, JObject actual, string prefix, List<string> missing) {
            foreach (var prop in shape.Properties()) {
                var a = actual[prop.Name];
                if (a == null) { missing.Add(prefix + prop.Name); continue; }
                if (prop.Value is JObject so && a is JObject ao) CollectMissing(so, ao, prefix + prop.Name + ".", missing);
            }
        }

        private static (long ticks, long length, EngineParams value)? baseCache;

        /// <summary>
        /// The base parameters from engine-params.json (see <see cref="BasePath"/>). Complete and validated, cached by size and timestamp.
        /// The returned object is a private copy.
        /// </summary>
        public static EngineParams LoadBase() {
            string path = BasePath();
            if (!File.Exists(path))
                throw new FileNotFoundException($"Base engine parameters not found: {path}. They are copied next to the console exe from Attax.Core/{BaseFileName}; set {BaseFileEnvVar} to use another file.", path);
            var info = new FileInfo(path);
            lock (cacheLock) {
                if (baseCache.HasValue && baseCache.Value.ticks == info.LastWriteTimeUtc.Ticks && baseCache.Value.length == info.Length) return baseCache.Value.value.Clone();
            }
            var parsed = ParseComplete(File.ReadAllText(path), path);
            lock (cacheLock) baseCache = (info.LastWriteTimeUtc.Ticks, info.Length, parsed);
            return parsed.Clone();
        }

        /// <summary>
        /// Loads a layer file on top of the base file (strict, validated): the file lists only what it changes. Parsed results are cached
        /// by path, size and timestamp (and invalidated when the base file changes); the returned object is a private copy.
        /// </summary>
        public static EngineParams Load(string path) {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Parameter file path is empty.", nameof(path));
            string full = Path.GetFullPath(path);
            if (!File.Exists(full)) throw new FileNotFoundException($"Engine parameter file not found: {path}", full);
            var info = new FileInfo(full);
            var baseInfo = new FileInfo(BasePath());
            long baseStamp = baseInfo.Exists ? baseInfo.LastWriteTimeUtc.Ticks ^ baseInfo.Length : 0;
            lock (cacheLock) {
                if (cache.TryGetValue(full, out var hit) && hit.ticks == info.LastWriteTimeUtc.Ticks && hit.length == info.Length && hit.baseStamp == baseStamp)
                    return hit.value.Clone();
            }
            var parsed = Parse(File.ReadAllText(full), LoadBase(), path);
            lock (cacheLock) cache[full] = (info.LastWriteTimeUtc.Ticks, info.Length, baseStamp, parsed);
            return parsed.Clone();
        }

        /// <summary>JSON with every value written out (tables on one line each), the format of engine-params.json.</summary>
        public static string ToJson(EngineParams p) {
            string json = JsonConvert.SerializeObject(p.Clone(), Formatting.Indented);
            return Regex.Replace(json, @"\[\s*([-0-9.eE+,\s]*?)\s*\]", m => "[" + Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim() + "]");
        }

        public static void Save(EngineParams p, string path) {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, ToJson(p) + Environment.NewLine, new UTF8Encoding(false));
        }

        /// <summary>Short stable id of the effective values.</summary>
        public static string Fingerprint(EngineParams p) {
            if (p == null) throw new ArgumentNullException(nameof(p));
            using (var sha = SHA256.Create()) {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(p.Clone(), Formatting.None)));
                return BitConverter.ToString(hash, 0, 5).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>First 8 bytes of the same hash as <see cref="Fingerprint"/>. Stored in log teacher records.</summary>
        public static ulong FingerprintUInt64(EngineParams p) {
            if (p == null) throw new ArgumentNullException(nameof(p));
            using (var sha = SHA256.Create()) {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(p.Clone(), Formatting.None)));
                return BitConverter.ToUInt64(hash, 0);
            }
        }

        // ---- reflection helpers: one place knows how a dotted path maps to a field --------------------------------------

        private static bool IsLeaf(Type t) => t == typeof(int) || t == typeof(double) || t == typeof(bool) || t == typeof(int[]) || t == typeof(double[]);

        /// <summary>Every leaf value as ("root.riskPoints", "0.8") / ("eval.centerControlTable[24]", "3") in declaration order.</summary>
        public static List<KeyValuePair<string, string>> Flatten(EngineParams p) {
            var list = new List<KeyValuePair<string, string>>();
            FlattenInto(p.Clone(), "", list);
            return list;
        }

        private static void FlattenInto(object o, string prefix, List<KeyValuePair<string, string>> list) {
            foreach (var f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)) {
                object v = f.GetValue(o);
                string name = prefix + f.Name;
                if (f.FieldType == typeof(int[])) { var a = (int[])v; for (int i = 0; i < a.Length; i++) list.Add(new KeyValuePair<string, string>($"{name}[{i}]", a[i].ToString(CultureInfo.InvariantCulture))); }
                else if (f.FieldType == typeof(double[])) { var a = (double[])v; for (int i = 0; i < a.Length; i++) list.Add(new KeyValuePair<string, string>($"{name}[{i}]", a[i].ToString("R", CultureInfo.InvariantCulture))); }
                else if (IsLeaf(f.FieldType)) list.Add(new KeyValuePair<string, string>(name, Convert.ToString(v, CultureInfo.InvariantCulture)));
                else if (v != null) FlattenInto(v, name + ".", list);
            }
        }

        /// <summary>Human-readable differences, one "path: old -> new" line per changed value.</summary>
        public static List<string> Diff(EngineParams before, EngineParams after) {
            var a = Flatten(before ?? LoadBase());
            var b = Flatten(after ?? LoadBase());
            var bd = b.ToDictionary(kv => kv.Key, kv => kv.Value);
            var lines = new List<string>();
            foreach (var kv in a)
                if (bd.TryGetValue(kv.Key, out var nv) && nv != kv.Value) lines.Add($"{kv.Key}: {kv.Value} -> {nv}");
            return lines;
        }

        /// <summary>
        /// Resolves a user path ("root.riskpoints", "eval.centerControlTable.24", any case) to its canonical spelling and a
        /// canonical value string, or throws FormatException with the list of valid choices. Pure: touches no file.
        /// </summary>
        public static KeyValuePair<string, string> Canonicalize(string path, string value, string whole) {
            // Use the SHAPE of the class (never the base file) so a spec can be parsed without any file on disk.
            var probe = new EngineParams {
                eval = new EvalParams { centerControlTable = new int[49] },
                root = new RootParams { aggressionMinLead = new int[3], aggressionFactor = new double[4], positionalTable = new int[49] },
                search = new SearchParams()
            };
            return SetOrProbe(probe, path, value, whole);
        }

        public static void Apply(EngineParams p, string canonicalPath, string value) {
            SetOrProbe(p, canonicalPath, value, canonicalPath + "=" + value);
        }

        private static KeyValuePair<string, string> SetOrProbe(EngineParams root, string path, string value, string whole) {
            var parts = path.Split('.');
            // Allow both "table.24" and "table[24]".
            var norm = new List<string>();
            foreach (var part in parts) {
                var m = Regex.Match(part, @"^(\w+)\[(\d+)\]$");
                if (m.Success) { norm.Add(m.Groups[1].Value); norm.Add(m.Groups[2].Value); } else norm.Add(part);
            }
            object cur = root;
            var canon = new List<string>();
            int i = 0;
            for (; i < norm.Count; i++) {
                var f = cur.GetType().GetField(norm[i], BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (f == null || f.Name == nameof(EngineParams.schemaVersion))
                    throw new FormatException($"'{path}' in '{whole}' is not an engine parameter. Valid names under '{(canon.Count == 0 ? "" : string.Join(".", canon) + ".")}': {string.Join(", ", cur.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).Where(x => x.Name != nameof(EngineParams.schemaVersion)).Select(x => x.Name))}.");
                canon.Add(f.Name);
                var next = f.GetValue(cur);
                var type = f.FieldType;
                if (!IsLeaf(type)) { cur = next; continue; }
                if (type == typeof(int[]) || type == typeof(double[])) {
                    if (i + 1 >= norm.Count || !int.TryParse(norm[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int idx))
                        throw new FormatException($"'{string.Join(".", canon)}' in '{whole}' is a table; address one cell as '{string.Join(".", canon)}.<index>'.");
                    if (i + 2 != norm.Count) throw new FormatException($"'{path}' in '{whole}' has extra segments after the table index.");
                    var arr = (Array)next;
                    if (arr == null || arr.Length == 0) { arr = type == typeof(int[]) ? (Array)new int[0] : new double[0]; }
                    if (idx >= arr.Length) throw new FormatException($"Index {idx} in '{whole}' is outside '{string.Join(".", canon)}' (0..{arr.Length - 1}).");
                    object parsed = type == typeof(int[]) ? (object)ParseInt(value, whole) : ParseDouble(value, whole);
                    arr.SetValue(parsed, idx);
                    canon.Add(idx.ToString(CultureInfo.InvariantCulture));
                    return new KeyValuePair<string, string>(string.Join(".", canon.Take(canon.Count - 1)) + "." + canon.Last(), Convert.ToString(parsed, CultureInfo.InvariantCulture) is string s0 && parsed is double d0 ? d0.ToString("R", CultureInfo.InvariantCulture) : Convert.ToString(parsed, CultureInfo.InvariantCulture));
                }
                if (i + 1 != norm.Count) throw new FormatException($"'{path}' in '{whole}' has extra segments after '{string.Join(".", canon)}'.");
                object val;
                if (type == typeof(int)) val = ParseInt(value, whole);
                else if (type == typeof(double)) val = ParseDouble(value, whole);
                else val = ParseBool(value, whole);
                f.SetValue(cur, val);
                return new KeyValuePair<string, string>(string.Join(".", canon), val is double d ? d.ToString("R", CultureInfo.InvariantCulture) : val is bool b ? (b ? "true" : "false") : Convert.ToString(val, CultureInfo.InvariantCulture));
            }
            throw new FormatException($"'{path}' in '{whole}' names a group, not a value. Pick one of: {string.Join(", ", cur.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).Select(x => x.Name))}.");
        }

        private static int ParseInt(string v, string whole) {
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r)) throw new FormatException($"'{v}' in '{whole}' is not an integer.");
            return r;
        }
        private static double ParseDouble(string v, string whole) {
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) || double.IsNaN(r) || double.IsInfinity(r)) throw new FormatException($"'{v}' in '{whole}' is not a finite number.");
            return r;
        }
        private static bool ParseBool(string v, string whole) {
            switch (v.ToLowerInvariant()) { case "true": case "1": return true; case "false": case "0": return false; }
            throw new FormatException($"'{v}' in '{whole}' is not true/false.");
        }
    }
}
