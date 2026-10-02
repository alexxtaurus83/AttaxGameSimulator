using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Attax.Play {
    public enum PlayerKind { Classic, Value, Policy, Random }

    /// <summary>
    /// One player configuration, written as  kind[:key=value,key=value].
    ///
    ///   classic:depth=3,nodes=0          heuristic negamax (no model file needed)
    ///   value:model=m.onnx,depth=2       negamax on the model's VALUE head
    ///   policy:model=m.onnx,temp=0       direct move from the model's POLICY head, no search
    ///   random                           uniform random legal move
    ///
    /// Keys: model, depth (1..12), nodes (0 = unlimited), time (ms, 0 = off; iterative deepening up to depth+4),
    ///       bonus (root-bonus scale, >= 0), temp, topk, train (true/false), id (true/false, iterative deepening in train mode),
    ///       quiescence (true/false: capture-only leaf extension; default true in the arena, false in self-play),
    ///       ties (true/false: pick at random among equally scored root moves; default true = the engine default, false = always the first),
    ///       params (path of an engine-parameter JSON file) and p.&lt;group&gt;.&lt;name&gt; (single parameter overrides applied on top of
    ///       the file, e.g. p.root.riskPoints=1.2 or p.eval.centerControlTable.24=4; names are checked when the spec is parsed).
    /// Keys that do not apply to a kind are rejected instead of being ignored, so a typo or a misplaced
    /// option can never silently change what is being measured.
    /// Temperature units: classic = legacy engine units (effectively greedy), value = value units (1.0 = a full
    /// win/loss swing), policy = logit units.
    /// </summary>
    public sealed class PlayerSpec : IEquatable<PlayerSpec> {
        public PlayerKind Kind { get; private set; }
        public string ModelPath { get; private set; }
        public int Depth { get; private set; } = 3;
        public int Nodes { get; private set; }
        public int TimeMs { get; private set; }
        public float? Bonus { get; private set; }
        public float Temp { get; private set; }
        public int TopK { get; private set; } = 1;
        public bool? Train { get; private set; }
        public bool IterativeDeepening { get; private set; }
        public bool? Quiescence { get; private set; }
        /// <summary>null = the engine default (random root ties on); false = deterministic (the first of the tied moves); true = explicitly on.</summary>
        public bool? RandomTies { get; private set; }
        public string ParamsPath { get; private set; }
        /// <summary>Canonical (path, value) overrides in the order given.</summary>
        public List<KeyValuePair<string, string>> Overrides { get; private set; } = new List<KeyValuePair<string, string>>();

        public bool IsSearch => Kind == PlayerKind.Classic || Kind == PlayerKind.Value;
        public bool UsesModel => Kind == PlayerKind.Value || Kind == PlayerKind.Policy;

        /// <summary>Root-bonus scale actually applied: classic keeps the heuristic bonuses (1), value drops them (0).</summary>
        public float EffectiveBonus => Bonus ?? (Kind == PlayerKind.Value ? 0f : 1f);

        private static readonly Dictionary<PlayerKind, string[]> Allowed = new Dictionary<PlayerKind, string[]> {
            { PlayerKind.Classic, new[] { "depth", "nodes", "time", "bonus", "temp", "topk", "train", "id", "quiescence", "ties", "params", "p.<group>.<name>" } },
            { PlayerKind.Value,   new[] { "model", "depth", "nodes", "time", "bonus", "temp", "topk", "train", "id", "quiescence", "ties", "params", "p.<group>.<name>" } },
            { PlayerKind.Policy,  new[] { "model", "temp", "topk" } },
            { PlayerKind.Random,  new string[0] },
        };

        /// <summary>
        /// The engine parameters this player uses: engine-params.json (the base file), then the params= file (which lists only what it
        /// changes), then the p.* overrides, in that order. Throws with a precise message when a file is missing/invalid or an override
        /// leaves the allowed range. Call it once up front so bad input fails before any game. Always returns a private copy.
        /// </summary>
        public Attax.Core.EngineParams ResolveParams() {
            if (!IsSearch) return null;    // policy and random players do not search, so they have no engine parameters
            var p = ParamsPath != null ? EngineParamsFile.Load(ParamsPath) : EngineParamsFile.LoadBase();
            foreach (var o in Overrides) EngineParamsFile.Apply(p, o.Key, o.Value);
            var errors = p.Validate();
            if (errors.Count > 0)
                throw new FormatException($"Player '{this}': invalid engine parameters:{Environment.NewLine}  - {string.Join(Environment.NewLine + "  - ", errors)}");
            return p;
        }

        public static PlayerSpec Parse(string text) {
            if (string.IsNullOrWhiteSpace(text)) throw new FormatException("Player spec is empty.");
            int colon = text.IndexOf(':');
            string kindText = (colon < 0 ? text : text.Substring(0, colon)).Trim().ToLowerInvariant();
            var spec = new PlayerSpec();
            switch (kindText) {
                case "classic": spec.Kind = PlayerKind.Classic; break;
                case "value": spec.Kind = PlayerKind.Value; break;
                case "policy": spec.Kind = PlayerKind.Policy; break;
                case "random": spec.Kind = PlayerKind.Random; break;
                default: throw new FormatException($"Unknown player kind '{kindText}' in '{text}'. Expected classic, value, policy or random.");
            }

            var allowed = Allowed[spec.Kind];
            var seen = new HashSet<string>();
            if (colon >= 0 && colon < text.Length - 1) {
                foreach (var pair in text.Substring(colon + 1).Split(',')) {
                    if (string.IsNullOrWhiteSpace(pair)) continue;
                    int eq = pair.IndexOf('=');
                    if (eq <= 0 || eq == pair.Length - 1) throw new FormatException($"Bad option '{pair}' in '{text}'. Use key=value.");
                    string key = pair.Substring(0, eq).Trim().ToLowerInvariant();
                    string value = pair.Substring(eq + 1).Trim();
                    if (key.StartsWith("p.")) {
                        if (!allowed.Contains("p.<group>.<name>"))
                            throw new FormatException($"Parameter overrides (p.*) are not valid for '{kindText}' players in '{text}'.");
                        // Resolve now (canonical spelling, type, range of the syntax); full validation happens in ResolveParams.
                        var canon = EngineParamsFile.Canonicalize(key.Substring(2), value, text);
                        if (spec.Overrides.Any(o => o.Key == canon.Key)) throw new FormatException($"Parameter '{canon.Key}' given twice in '{text}'.");
                        spec.Overrides.Add(canon);
                        continue;
                    }
                    if (!allowed.Contains(key))
                        throw new FormatException($"Option '{key}' is not valid for '{kindText}' players in '{text}'. Valid options: {(allowed.Length == 0 ? "(none)" : string.Join(", ", allowed))}.");
                    if (!seen.Add(key)) throw new FormatException($"Option '{key}' given twice in '{text}'.");
                    spec.Set(key, value, text);
                }
            }

            if (spec.UsesModel && string.IsNullOrWhiteSpace(spec.ModelPath))
                throw new FormatException($"'{kindText}' players need model=<path> in '{text}'.");
            return spec;
        }

        private void Set(string key, string value, string whole) {
            var inv = CultureInfo.InvariantCulture;
            switch (key) {
                case "model": ModelPath = value; break;
                case "depth": Depth = ParseInt(key, value, 1, 12, whole); break;
                case "nodes": Nodes = ParseInt(key, value, 0, int.MaxValue, whole); break;
                case "time": TimeMs = ParseInt(key, value, 0, int.MaxValue, whole); break;
                case "topk": TopK = ParseInt(key, value, 1, 100000, whole); break;
                case "bonus": Bonus = ParseFloat(key, value, 0f, whole); break;
                case "temp": Temp = ParseFloat(key, value, 0f, whole); break;
                case "train": Train = ParseBool(key, value, whole); break;
                case "id": IterativeDeepening = ParseBool(key, value, whole); break;
                case "quiescence": Quiescence = ParseBool(key, value, whole); break;
                case "ties": RandomTies = ParseBool(key, value, whole); break;
                case "params": ParamsPath = value; break;
            }
        }

        private static int ParseInt(string key, string v, int min, int max, string whole) {
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) || r < min || r > max)
                throw new FormatException($"Option '{key}' must be an integer in [{min}, {max}], got '{v}' in '{whole}'.");
            return r;
        }

        private static float ParseFloat(string key, string v, float min, string whole) {
            if (!float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float r) || float.IsNaN(r) || float.IsInfinity(r) || r < min)
                throw new FormatException($"Option '{key}' must be a number >= {min}, got '{v}' in '{whole}'.");
            return r;
        }

        private static bool ParseBool(string key, string v, string whole) {
            switch (v.ToLowerInvariant()) {
                case "true": case "1": case "yes": return true;
                case "false": case "0": case "no": return false;
                default: throw new FormatException($"Option '{key}' must be true or false, got '{v}' in '{whole}'.");
            }
        }

        /// <summary>Canonical text form; Parse(ToString()) reproduces an equal spec.</summary>
        public override string ToString() => Canonical(true);

        /// <summary>Everything that decides which move a SEARCH picks, excluding how the move is sampled (temp/topk).</summary>
        public string SearchKey() => Canonical(false);

        private string Canonical(bool includeSampling) {
            var inv = CultureInfo.InvariantCulture;
            var parts = new List<string>();
            if (UsesModel) parts.Add("model=" + ModelPath);
            if (IsSearch) {
                parts.Add("depth=" + Depth.ToString(inv));
                if (Nodes > 0) parts.Add("nodes=" + Nodes.ToString(inv));
                if (TimeMs > 0) parts.Add("time=" + TimeMs.ToString(inv));
                if (Bonus.HasValue) parts.Add("bonus=" + Bonus.Value.ToString("R", inv));
                if (Train.HasValue) parts.Add("train=" + (Train.Value ? "true" : "false"));
                if (IterativeDeepening) parts.Add("id=true");
                if (Quiescence.HasValue) parts.Add("quiescence=" + (Quiescence.Value ? "true" : "false"));
                if (RandomTies == false) parts.Add("ties=false");   // true is the engine default, so "ties=true" and no key are the same spec
                if (ParamsPath != null) parts.Add("params=" + ParamsPath);
                foreach (var o in Overrides) parts.Add("p." + o.Key + "=" + o.Value);
            }
            if (includeSampling && (IsSearch || Kind == PlayerKind.Policy)) {
                if (Temp > 0) parts.Add("temp=" + Temp.ToString("R", inv));
                if (TopK > 1) parts.Add("topk=" + TopK.ToString(inv));
            }
            string name = Kind.ToString().ToLowerInvariant();
            return parts.Count == 0 ? name : name + ":" + string.Join(",", parts);
        }

        public bool Equals(PlayerSpec other) => other != null && ToString() == other.ToString();
        public override bool Equals(object obj) => Equals(obj as PlayerSpec);
        public override int GetHashCode() => ToString().GetHashCode();

        /// <summary>A copy with the root tie rule set explicitly (see <see cref="RandomTies"/>). Part of <see cref="SearchKey"/>.</summary>
        public PlayerSpec WithRandomTies(bool randomTies) {
            var c = (PlayerSpec)MemberwiseClone();
            c.Overrides = new List<KeyValuePair<string, string>>(Overrides);
            c.RandomTies = randomTies;
            return c;
        }

        /// <summary>A copy that always plays greedily (used for teachers: a label must be the argmax, never a sample).</summary>
        public PlayerSpec Greedy() {
            var c = (PlayerSpec)MemberwiseClone();
            c.Temp = 0f;
            c.TopK = 1;
            return c;
        }
    }
}
