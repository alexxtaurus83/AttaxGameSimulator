#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using Attax.Core.Utils;

namespace Attax.Core {
    /// <summary>
    /// Every tunable number of the heuristic engine in one plain, serialisable object.
    ///
    /// THERE ARE NO DEFAULT VALUES IN CODE. The values live in <c>engine-params.json</c> at the root of the Core folder
    /// (the single source of truth); a host deserialises that file (plus any overrides it wants) and passes the object to the engine
    /// constructor, <c>new AtaxxAIEngine(engineParams, config)</c>. The parameters are MANDATORY: there is no engine without them. A freshly
    /// constructed object is all zeros and is rejected by <see cref="Validate"/> on purpose, so a forgotten field can never silently become 0.
    ///
    /// Shape rules (works with Newtonsoft.Json; UnityEngine.JsonUtility is untested): public fields, [Serializable] nested classes,
    /// only int / double / bool / 1-D arrays. Always deserialise the COMPLETE file; a partial file leaves the missing fields at 0
    /// (the console tools do the layering for you: base file, then your file, then command-line overrides).
    /// The engine validates and copies the object at construction, so editing it afterwards changes nothing.
    ///
    /// Units: eval weights are "heuristic points" (leaf evaluator output before EvaluationScale = 10,000); the root group is added
    /// to the search score at the root only.
    /// </summary>
    [Serializable]
    public sealed class EngineParams {
        public const int CurrentSchemaVersion = 1;

        public int schemaVersion;
        public EvalParams eval;
        public RootParams root;
        public SearchParams search;

        /// <summary>Deep copy. Safe to mutate; the original is unaffected.</summary>
        public EngineParams Clone() => new EngineParams {
            schemaVersion = schemaVersion,
            eval = eval?.Clone(),
            root = root?.Clone(),
            search = search?.Clone()
        };

        /// <summary>Every problem found, empty when valid. Never throws.</summary>
        public List<string> Validate() {
            var errors = new List<string>();
            if (schemaVersion != CurrentSchemaVersion)
                errors.Add($"schemaVersion is {schemaVersion}; this engine understands {CurrentSchemaVersion}.");
            if (eval == null) errors.Add("eval group is missing."); else eval.Validate(errors);
            if (root == null) errors.Add("root group is missing."); else root.Validate(errors);
            if (search == null) errors.Add("search group is missing."); else search.Validate(errors);
            return errors;
        }

        internal CompiledEngineParams Compile() {
            var errors = Validate();
            if (errors.Count > 0)
                throw new ArgumentException("Invalid EngineParams (is this the complete engine-params.json, not a partial file?):" + Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", errors), nameof(EngineParams));
            return new CompiledEngineParams(Clone());
        }

        internal static void Check(List<string> errors, string name, double value, double min, double max, string why = null) {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < min || value > max)
                errors.Add($"{name} = {value.ToString(CultureInfo.InvariantCulture)} is outside [{min.ToString(CultureInfo.InvariantCulture)}, {max.ToString(CultureInfo.InvariantCulture)}]" + (why == null ? "." : ": " + why));
        }
    }

    /// <summary>Leaf evaluator (HeuristicEvaluator). Changes here alter every node of the search.</summary>
    [Serializable]
    public sealed class EvalParams {
        // Material: weight per piece of difference, interpolated by total pieces between scaleStartPieces and scaleEndPieces.
        public int materialEarly;
        public int materialLate;
        // Mobility (reachable destination squares, mine minus theirs), interpolated the same way. Midgame only (see endgameEmptyThreshold).
        public int mobilityEarly;
        public int mobilityLate;
        public int scaleStartPieces;
        public int scaleEndPieces;
        // Squares adjacent to my reachable squares (potential mobility) and weighted centre control. Midgame only.
        public int potentialMobilityWeight;
        public int centerControlWeight;
        // Corner and edge (non-corner) pieces, every phase.
        public int cornerWeight;
        public int edgeWeight;
        // The evaluator is in "midgame" mode while MORE than this many squares are empty, else in "endgame" mode (stability term).
        public int endgameEmptyThreshold;
        // Endgame stability term: stable-piece difference times stabilityWeight when totalPieces >= stabilityFullMinPieces,
        // otherwise times stabilityLowMult. (The thresholds only line up when no squares are blocked: the endgame starts at 14 empty
        // squares = 35 pieces on an open board, but with b blocked cells it starts at 35 - b pieces.)
        public int stabilityWeight;
        public int stabilityFullMinPieces;
        public int stabilityLowMult;
        // 49 weights, index = y * 7 + x, multiplied by (my pieces there - their pieces there).
        public int[] centerControlTable;

        internal EvalParams Clone() {
            var c = (EvalParams)MemberwiseClone();
            c.centerControlTable = centerControlTable == null ? null : (int[])centerControlTable.Clone();
            return c;
        }

        internal void Validate(List<string> e) {
            // Bound: 49 pieces * 100 * about six terms stays far below the terminal score (1e9 / 1e4 = 1e5 points).
            const string why = "weights are limited to +-100 so a position score can never reach the terminal-win score";
            EngineParams.Check(e, "eval.materialEarly", materialEarly, 1, 100, "must be positive; " + why);
            EngineParams.Check(e, "eval.materialLate", materialLate, 1, 100, "must be positive; " + why);
            EngineParams.Check(e, "eval.mobilityEarly", mobilityEarly, -100, 100, why);
            EngineParams.Check(e, "eval.mobilityLate", mobilityLate, -100, 100, why);
            EngineParams.Check(e, "eval.potentialMobilityWeight", potentialMobilityWeight, -100, 100, why);
            EngineParams.Check(e, "eval.centerControlWeight", centerControlWeight, -100, 100, why);
            EngineParams.Check(e, "eval.cornerWeight", cornerWeight, -100, 100, why);
            EngineParams.Check(e, "eval.edgeWeight", edgeWeight, -100, 100, why);
            EngineParams.Check(e, "eval.stabilityWeight", stabilityWeight, -100, 100, why);
            EngineParams.Check(e, "eval.stabilityLowMult", stabilityLowMult, -100, 100, why);
            EngineParams.Check(e, "eval.scaleStartPieces", scaleStartPieces, 0, 48);
            EngineParams.Check(e, "eval.scaleEndPieces", scaleEndPieces, 1, 49);
            if (scaleStartPieces >= scaleEndPieces) e.Add("eval.scaleStartPieces must be smaller than eval.scaleEndPieces.");
            EngineParams.Check(e, "eval.endgameEmptyThreshold", endgameEmptyThreshold, 0, 49);
            EngineParams.Check(e, "eval.stabilityFullMinPieces", stabilityFullMinPieces, 0, 49);
            if (centerControlTable == null) e.Add("eval.centerControlTable is missing.");
            else if (centerControlTable.Length != 49) e.Add($"eval.centerControlTable must have 49 entries (7x7), has {centerControlTable.Length}.");
            else for (int i = 0; i < 49; i++) EngineParams.Check(e, $"eval.centerControlTable[{i}]", centerControlTable[i], -100, 100, why);
        }
    }

    /// <summary>
    /// Root-only move preference, added to the search score of each root move (never inside the tree, so it cannot break
    /// negamax antisymmetry). Units are heuristic points, multiplied by EvaluationScale (10,000) inside the engine.
    /// </summary>
    [Serializable]
    public sealed class RootParams {
        // Extra preference for a clone (growth) by game phase, in total pieces: <= openingMax, <= midMax, then late.
        public double cloneOpening;
        public double cloneMid;
        public double cloneLate;
        public int openingMax;
        public int midMax;
        // Per flipped enemy piece (times the comeback factor). The search already sees flips one ply down, so this is usually 0.
        public double flipPoints;
        // Penalty per enemy piece the opponent can flip back on its best reply (divided by the comeback factor).
        public double riskPoints;
        // Per unit of positionalTable at the destination square, only while totalPieces <= positionalMaxPieces.
        public double positionalPoints;
        public int positionalMaxPieces;
        // A jump that neither captures nor grows is penalised on the clone axis.
        public double nonCapturingJumpPenalty;
        // Comeback aggression from my piece lead (mine - theirs): the first i with lead >= aggressionMinLead[i] uses
        // aggressionFactor[i]; below every threshold uses the last factor. The factor multiplies flip points and divides
        // clone and risk points. thresholds must be strictly descending; factors has one more entry than thresholds
        // (an empty threshold list with one factor is a constant factor).
        public int[] aggressionMinLead;
        public double[] aggressionFactor;
        // 49 positional values, index = y * 7 + x.
        public int[] positionalTable;

        internal RootParams Clone() {
            var c = (RootParams)MemberwiseClone();
            c.aggressionMinLead = aggressionMinLead == null ? null : (int[])aggressionMinLead.Clone();
            c.aggressionFactor = aggressionFactor == null ? null : (double[])aggressionFactor.Clone();
            c.positionalTable = positionalTable == null ? null : (int[])positionalTable.Clone();
            return c;
        }

        internal void Validate(List<string> e) {
            EngineParams.Check(e, "root.cloneOpening", cloneOpening, -1000, 1000);
            EngineParams.Check(e, "root.cloneMid", cloneMid, -1000, 1000);
            EngineParams.Check(e, "root.cloneLate", cloneLate, -1000, 1000);
            EngineParams.Check(e, "root.openingMax", openingMax, 0, 49);
            EngineParams.Check(e, "root.midMax", midMax, 0, 49);
            if (openingMax > midMax) e.Add("root.openingMax must not exceed root.midMax.");
            EngineParams.Check(e, "root.flipPoints", flipPoints, -1000, 1000);
            EngineParams.Check(e, "root.riskPoints", riskPoints, -1000, 1000);
            EngineParams.Check(e, "root.positionalPoints", positionalPoints, -1000, 1000);
            EngineParams.Check(e, "root.positionalMaxPieces", positionalMaxPieces, 0, 49);
            EngineParams.Check(e, "root.nonCapturingJumpPenalty", nonCapturingJumpPenalty, -1000, 1000);
            if (aggressionMinLead == null) e.Add("root.aggressionMinLead is missing.");
            if (aggressionFactor == null) e.Add("root.aggressionFactor is missing.");
            if (aggressionMinLead != null && aggressionFactor != null) {
                if (aggressionFactor.Length != aggressionMinLead.Length + 1)
                    e.Add($"root.aggressionFactor needs exactly one more entry ({aggressionMinLead.Length + 1}) than root.aggressionMinLead ({aggressionMinLead.Length}); it has {aggressionFactor.Length}.");
                for (int i = 0; i < aggressionMinLead.Length; i++) {
                    EngineParams.Check(e, $"root.aggressionMinLead[{i}]", aggressionMinLead[i], -49, 49);
                    if (i > 0 && aggressionMinLead[i] >= aggressionMinLead[i - 1]) e.Add("root.aggressionMinLead must be strictly descending.");
                }
                for (int i = 0; i < aggressionFactor.Length; i++)
                    EngineParams.Check(e, $"root.aggressionFactor[{i}]", aggressionFactor[i], 0.1, 100, "it divides clone and risk points, so it must be positive");
            }
            if (positionalTable == null) e.Add("root.positionalTable is missing.");
            else if (positionalTable.Length != 49) e.Add($"root.positionalTable must have 49 entries (7x7), has {positionalTable.Length}.");
            else for (int i = 0; i < 49; i++) EngineParams.Check(e, $"root.positionalTable[{i}]", positionalTable[i], -100, 100);
        }
    }

    /// <summary>
    /// Search gates that were hard-coded in AlphaBeta. These change how much of the tree is searched (nodes, time) as well as
    /// what it concludes, so compare them at equal TIME, not only at equal depth.
    /// </summary>
    [Serializable]
    public sealed class SearchParams {
        // Null-move pruning (normal mode only, and not when DisableNullMovePruning is set): tried only while MORE than
        // nullMoveMinEmpty squares are empty, the mover has at least nullMoveMinPieces pieces and depth >= nullMoveMinDepth;
        // the null search is nullMoveReduction plies shallower.
        public int nullMoveMinEmpty;
        public int nullMoveMinPieces;
        public int nullMoveMinDepth;
        public int nullMoveReduction;
        // Capture-only quiescence at the leaves (normal mode, unless DisableQuiescenceSearch): used when the root iteration
        // depth is at least quiescenceMinRootDepth, extending captures quiescenceDepth plies.
        public int quiescenceMinRootDepth;
        public int quiescenceDepth;

        internal SearchParams Clone() => (SearchParams)MemberwiseClone();

        internal void Validate(List<string> e) {
            EngineParams.Check(e, "search.nullMoveMinEmpty", nullMoveMinEmpty, 0, 49);
            EngineParams.Check(e, "search.nullMoveMinPieces", nullMoveMinPieces, 0, 49);
            EngineParams.Check(e, "search.nullMoveMinDepth", nullMoveMinDepth, 1, 12);
            EngineParams.Check(e, "search.nullMoveReduction", nullMoveReduction, 1, 6);
            EngineParams.Check(e, "search.quiescenceMinRootDepth", quiescenceMinRootDepth, 1, 12);
            EngineParams.Check(e, "search.quiescenceDepth", quiescenceDepth, 0, 6);
        }
    }

    /// <summary>
    /// Validated, immutable snapshot used on the hot path: the interpolated weights are precomputed per total piece count.
    /// The interpolation expressions are copies of the legacy formulas, kept operation for operation so results are bit-identical;
    /// RefactorGoldenTests and EngineParamsTests pin that.
    /// </summary>
    internal sealed class CompiledEngineParams {
        public const int Slots = AttaxConstants.BaseConst.BoardSize * AttaxConstants.BaseConst.BoardSize + 1;

        public readonly int[] MaterialWeight = new int[Slots];
        public readonly int[] MobilityWeight = new int[Slots];
        public readonly int[] StabilityMult = new int[Slots];
        public readonly double[] CloneBonusPoints = new double[Slots];

        public readonly int PotentialMobilityWeight, CenterControlWeight, CornerWeight, EdgeWeight, EndgameEmptyThreshold;
        public readonly int[] CenterControlTable;

        public readonly double FlipPoints, RiskPoints, PositionalPoints, NonCapturingJumpPenalty;
        public readonly int PositionalMaxPieces;
        public readonly int[] PositionalTable;
        private readonly int[] aggressionMinLead;
        private readonly double[] aggressionFactor;

        public readonly int NullMoveMinEmpty, NullMoveMinPieces, NullMoveMinDepth, NullMoveReduction, QuiescenceMinRootDepth, QuiescenceDepth;

        public CompiledEngineParams(EngineParams p) {
            var ev = p.eval; var r = p.root; var s = p.search;
            for (int t = 0; t < Slots; t++) {
                MaterialWeight[t] = Material(ev, t);
                MobilityWeight[t] = Mobility(ev, t);
                StabilityMult[t] = t >= ev.stabilityFullMinPieces ? ev.stabilityWeight : ev.stabilityLowMult;
                CloneBonusPoints[t] = t <= r.openingMax ? r.cloneOpening : t <= r.midMax ? r.cloneMid : r.cloneLate;
            }
            PotentialMobilityWeight = ev.potentialMobilityWeight; CenterControlWeight = ev.centerControlWeight;
            CornerWeight = ev.cornerWeight; EdgeWeight = ev.edgeWeight; EndgameEmptyThreshold = ev.endgameEmptyThreshold;
            CenterControlTable = (int[])ev.centerControlTable.Clone();

            FlipPoints = r.flipPoints; RiskPoints = r.riskPoints; PositionalPoints = r.positionalPoints;
            NonCapturingJumpPenalty = r.nonCapturingJumpPenalty; PositionalMaxPieces = r.positionalMaxPieces;
            PositionalTable = (int[])r.positionalTable.Clone();
            aggressionMinLead = (int[])r.aggressionMinLead.Clone();
            aggressionFactor = (double[])r.aggressionFactor.Clone();

            NullMoveMinEmpty = s.nullMoveMinEmpty; NullMoveMinPieces = s.nullMoveMinPieces; NullMoveMinDepth = s.nullMoveMinDepth;
            NullMoveReduction = s.nullMoveReduction; QuiescenceMinRootDepth = s.quiescenceMinRootDepth; QuiescenceDepth = s.quiescenceDepth;
        }

        // Interpolated material weight (the formula of the original engine, verbatim apart from the parameter source).
        private static int Material(EvalParams ev, int totalPieces) {
            if (totalPieces <= ev.scaleStartPieces) return ev.materialEarly;
            if (totalPieces >= ev.scaleEndPieces) return ev.materialLate;
            float progress = (float)(totalPieces - ev.scaleStartPieces) / (ev.scaleEndPieces - ev.scaleStartPieces);
            return (int)(ev.materialEarly + (ev.materialLate - ev.materialEarly) * progress);
        }

        // Interpolated mobility weight (the formula of the original engine, verbatim; note the subtraction form).
        private static int Mobility(EvalParams ev, int totalPieces) {
            if (totalPieces <= ev.scaleStartPieces) return ev.mobilityEarly;
            if (totalPieces >= ev.scaleEndPieces) return ev.mobilityLate;
            float progress = (float)(totalPieces - ev.scaleStartPieces) / (ev.scaleEndPieces - ev.scaleStartPieces);
            return (int)(ev.mobilityEarly - (ev.mobilityEarly - ev.mobilityLate) * progress);
        }

        /// <summary>Comeback factor from my piece lead (the original three-step function, generalised to a table).</summary>
        public double Aggression(int lead) {
            for (int i = 0; i < aggressionMinLead.Length; i++)
                if (lead >= aggressionMinLead[i]) return aggressionFactor[i];
            return aggressionFactor[aggressionFactor.Length - 1];
        }

        public double CloneBonus(int totalPieces) => CloneBonusPoints[totalPieces < 0 ? 0 : totalPieces >= Slots ? Slots - 1 : totalPieces];

        public int PositionalValue(int x, int y) {
            const int n = AttaxConstants.BaseConst.BoardSize;
            return (x >= 0 && x < n && y >= 0 && y < n) ? PositionalTable[y * n + x] : 0;
        }
    }
}
