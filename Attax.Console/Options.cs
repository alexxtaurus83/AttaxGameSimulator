using CommandLine;

namespace Attax.Console {
    internal enum SymmetryModeOption { None, Random, All }

    [Verb("selfplay", HelpText = "Generate v3 self-play logs with any mix of classic / value / policy / random players.")]
    internal sealed class SelfPlayOptions {
        [Option("games", Default = 100, HelpText = "Number of games.")]
        public int Games { get; set; } = 100;

        [Option("seed", Default = 0, HelpText = "Base seed. Game identity is derived from (generation, seed, index).")]
        public int Seed { get; set; } = 0;

        [Option("generation", Default = 0, HelpText = "Model generation number stamped into the log (part of the game identity).")]
        public int Generation { get; set; } = 0;

        [Option("out", Required = true, HelpText = "Output .bin path (v3).")]
        public string Out { get; set; } = "";

        [Option("red", Default = "classic:depth=3", HelpText = "Red player spec, e.g. classic:depth=3 | value:model=m.onnx,depth=2 | policy:model=m.onnx,temp=1 | random. Note: nodes=N below the cost of the target depth makes most moves fall back to depth 1 unless id=true.")]
        public string Red { get; set; } = "classic:depth=3";

        [Option("blue", HelpText = "Blue player spec (default: same as --red).")]
        public string? Blue { get; set; }

        [Option("samples", Default = 20, HelpText = "Positions kept per game; 0 keeps every position.")]
        public int Samples { get; set; } = 20;

        [Option("epsilonStart", Default = 0.25, HelpText = "Probability of a uniformly random move until epsilonPly1.")]
        public double EpsilonStart { get; set; } = 0.25;
        [Option("epsilonMid", Default = 0.10, HelpText = "Random move probability until epsilonPly2.")]
        public double EpsilonMid { get; set; } = 0.10;
        [Option("epsilonLate", Default = 0.02, HelpText = "Random move probability afterwards.")]
        public double EpsilonLate { get; set; } = 0.02;
        [Option("epsilonPly1", Default = 10, HelpText = "First epsilon pivot ply.")]
        public int EpsilonPly1 { get; set; } = 10;
        [Option("epsilonPly2", Default = 25, HelpText = "Second epsilon pivot ply.")]
        public int EpsilonPly2 { get; set; } = 25;

        [Option("teacher", HelpText = "Optional teacher spec (classic or value) used to label a fraction of kept positions inline. Labels are greedy. Free when the mover IS that teacher.")]
        public string? Teacher { get; set; }

        [Option("teacherRandomTies", Default = false, HelpText = "Let the TEACHER break ties between equally scored root moves at random, as the engine does by default for the game. Default false: the teacher takes the first of the tied moves (spec key ties=false), a consistent rule for the model to learn that a re-search reproduces. The movers keep the engine default (random), which diversifies the games.")]
        public bool TeacherRandomTies { get; set; } = false;

        [Option("teacherFraction", Default = 1.0, HelpText = "Share of kept positions sent to the teacher (0..1).")]
        public double TeacherFraction { get; set; } = 1.0;

        [Option("symmetry", Default = SymmetryModeOption.None, HelpText = "None, Random (one random symmetry per sample) or All (8 copies). Copies share the game identity.")]
        public SymmetryModeOption Symmetry { get; set; } = SymmetryModeOption.None;

        [Option("blocked", Default = "0,0", HelpText = "Random blocked cells per game, N or min,max (inclusive). The game uses 0..6, so 0,6 matches it; 0,0 (default) = none. Placement is the game's own. Blocked squares are stored in the log.")]
        public string Blocked { get; set; } = "0,0";

        [Option("maxPlies", Default = 400, HelpText = "Ply cap. A capped game is recorded as PlyCap and its outcome is not a real result.")]
        public int MaxPlies { get; set; } = 400;

        [Option("parallel", Default = 0, HelpText = "Games in parallel (0 = processor count).")]
        public int Parallel { get; set; } = 0;
    }

    [Verb("relabel", HelpText = "Add teacher labels to an existing v3 log (offline teacher review).")]
    internal sealed class RelabelOptions {
        [Option("in", Required = true, HelpText = "Input v3 log.")]
        public string In { get; set; } = "";
        [Option("out", Required = true, HelpText = "Output v3 log (new file).")]
        public string Out { get; set; } = "";
        [Option("teacher", Required = true, HelpText = "Teacher spec (classic or value).")]
        public string Teacher { get; set; } = "";
        [Option("teacherRandomTies", Default = false, HelpText = "Let the teacher break ties between equally scored root moves at random (default false: the first tied move, consistent labels).")]
        public bool TeacherRandomTies { get; set; } = false;
        [Option("fraction", Default = 1.0, HelpText = "Share of unlabeled positions to review (0..1), chosen at random.")]
        public double Fraction { get; set; } = 1.0;
        [Option("seed", Default = 0, HelpText = "Seed for choosing positions to review.")]
        public int Seed { get; set; } = 0;
        [Option("parallel", Default = 0, HelpText = "Workers (0 = processor count).")]
        public int Parallel { get; set; } = 0;
    }

    [Verb("arena", HelpText = "Paired-opening match between two player specs, swapping colours.")]
    internal sealed class ArenaOptions {
        [Option("p1", Required = true, HelpText = "Player 1 spec.")]
        public string P1 { get; set; } = "";
        [Option("p2", Required = true, HelpText = "Player 2 spec.")]
        public string P2 { get; set; } = "";
        [Option("pairs", Default = 50, HelpText = "Opening pairs. Each opening is played twice with colours swapped, so games = 2 * pairs.")]
        public int Pairs { get; set; } = 50;
        [Option("blocked", Default = "0,0", HelpText = "Random blocked cells per pair, N or min,max (inclusive). The game uses 0..6, so 0,6 matches it; 0,0 (default) = none. Both games of a pair get the same blocks.")]
        public string Blocked { get; set; } = "0,0";
        [Option("openingPlies", Default = 4, HelpText = "Random opening plies shared by both games of a pair (even recommended).")]
        public int OpeningPlies { get; set; } = 4;
        [Option("seed", Default = 0, HelpText = "Seed for openings and sampling.")]
        public int Seed { get; set; } = 0;
        [Option("maxPlies", Default = 400, HelpText = "Ply cap; capped games are decided by piece count and reported separately.")]
        public int MaxPlies { get; set; } = 400;
        [Option("parallel", Default = 0, HelpText = "Games in parallel (0 = processor count; use 1 for clean per-move timing).")]
        public int Parallel { get; set; } = 0;
        [Option("sprt", HelpText = "Stop early with a sequential test, e.g. 0,5 = decide between no-better (Elo 0) and +5 Elo better. Checked after every batch of pairs; --pairs is the maximum.")]
        public string? Sprt { get; set; }
        [Option("sprtAlpha", Default = 0.05, HelpText = "SPRT false-accept risk (declaring P1 better when it is not).")]
        public double SprtAlpha { get; set; } = 0.05;
        [Option("sprtBeta", Default = 0.05, HelpText = "SPRT false-reject risk (missing a real improvement of elo1).")]
        public double SprtBeta { get; set; } = 0.05;
        [Option("minPairs", Default = 40, HelpText = "Never stop before this many pairs (the SPRT needs a usable variance estimate).")]
        public int MinPairs { get; set; } = 40;
        [Option("pairsCsv", HelpText = "Write one line per finished pair (opening index, both game results, plies, nodes) to this CSV.")]
        public string? PairsCsv { get; set; }
    }

    [Verb("compare-moves", HelpText = "Run two player specs on the same logged positions and count how often they choose different moves (no games). Use it to find parameters that matter, and to prove a change is inert.")]
    internal sealed class CompareMovesOptions {
        [Option("playerA", Required = true, HelpText = "Player A spec (search player: classic or value).")]
        public string A { get; set; } = "";
        [Option("playerB", Required = true, HelpText = "Player B spec.")]
        public string B { get; set; } = "";
        [Option("positions", Required = true, HelpText = "A v3 log to take positions from (any log; labels are ignored).")]
        public string Positions { get; set; } = "";
        [Option("count", Default = 2000, HelpText = "Positions to compare (random sample, positions with at least 2 legal moves).")]
        public int Count { get; set; } = 2000;
        [Option("seed", Default = 1, HelpText = "Seed for the position sample.")]
        public int Seed { get; set; } = 1;
        [Option("parallel", Default = 0, HelpText = "Workers (0 = processor count).")]
        public int Parallel { get; set; } = 0;
        [Option("show", Default = 0, HelpText = "Print this many example positions where the moves differ.")]
        public int Show { get; set; } = 0;
    }

    [Verb("params-default", HelpText = "Write a complete copy of the base engine-params.json (every value, every table) to a file to edit.")]
    internal sealed class ParamsDefaultOptions {
        [Option("out", Required = true, HelpText = "Output JSON path.")]
        public string Out { get; set; } = "";
        [Option("force", Default = false, HelpText = "Overwrite an existing file.")]
        public bool Force { get; set; } = false;
    }

    [Verb("params-check", HelpText = "Validate an engine-parameter JSON file on top of the base engine-params.json (strict: unknown names, wrong types and out-of-range values are errors) and list what it changes.")]
    internal sealed class ParamsCheckOptions {
        [Option("file", Required = true, HelpText = "Parameter JSON file.")]
        public string File { get; set; } = "";
    }

    [Verb("validate-log", HelpText = "Validate a v3 training log.")]
    internal sealed class ValidateLogOptions {
        [Option("path", Required = true, HelpText = "Path to the log.")]
        public string Path { get; set; } = "";
    }

    [Verb("verify-model", HelpText = "Load an ONNX model, validate the contract and measure inference latency.")]
    internal sealed class VerifyModelOptions {
        [Option("model", Required = true, HelpText = "ONNX model path.")]
        public string Model { get; set; } = "";
        [Option("threads", Default = 1, HelpText = "Intra-op threads (0 = runtime default).")]
        public int Threads { get; set; } = 1;
    }

    [Verb("contract-fixtures", HelpText = "Write JSON fixtures (positions, planes, legal actions, symmetry maps, optional model outputs) for the Python parity test.")]
    internal sealed class FixturesOptions {
        [Option("out", Required = true, HelpText = "Output JSON path.")]
        public string Out { get; set; } = "";
        [Option("count", Default = 40, HelpText = "Number of positions.")]
        public int Count { get; set; } = 40;
        [Option("seed", Default = 1, HelpText = "Seed.")]
        public int Seed { get; set; } = 1;
        [Option("model", HelpText = "Optional ONNX model: also record its outputs so Python can compare numerically.")]
        public string? Model { get; set; }
    }
}
