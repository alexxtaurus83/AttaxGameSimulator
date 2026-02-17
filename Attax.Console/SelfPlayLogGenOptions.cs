using CommandLine;

namespace Attax.Console {
    [Verb("selfplayloggen", HelpText = "Generate self-play training logs.")]
    internal sealed class SelfPlayLogGenOptions {
        [Option("games", Default = 100, HelpText = "Number of games to generate (expected > 0).")]
        public int Games { get; set; } = 100;

        [Option("seed", Default = 0, HelpText = "Random seed.")]
        public int Seed { get; set; } = 0;

        [Option("out", Default = "training_data.bin", HelpText = "Output log file path.")]
        public string Out { get; set; } = "training_data.bin";

        [Option("nodeBudget", Default = 1000, HelpText = "Search node budget per move (expected > 0).")]
        public int NodeBudget { get; set; } = 1000;

        [Option("topK", Default = 1, HelpText = "Top-K sampling value (commonly 1..64).")]
        public int TopK { get; set; } = 1;

        [Option("temp", Default = 1.0, HelpText = "Sampling temperature (commonly 0..100).")]
        public double Temp { get; set; } = 1.0;

        [Option("samples", Default = 20, HelpText = "Target samples per game (expected >= 0).")]
        public int Samples { get; set; } = 20;

        [Option("samplesPerGame", Hidden = true, HelpText = "Legacy alias for samples.")]
        public int? SamplesPerGame { get; set; }

        [Option("aiDepth", Default = 3, HelpText = "Search depth (commonly 1..12).")]
        public int AiDepth { get; set; } = 3;

        [Option("useOrthogonalOnlyCapture", Default = false, HelpText = "Use orthogonal-only capture rule (true/false).")]
        public bool UseOrthogonalOnlyCapture { get; set; } = false;

        [Option("debugInit", Hidden = true, Default = false, HelpText = "Enable self-play initialization debug output (true/false).")]
        public bool DebugInit { get; set; } = false;

        [Option("useMLRootOnly", Default = false, HelpText = "Use ML evaluator on root only (true/false).")]
        public bool UseMLRootOnly { get; set; } = false;

        [Option("disableQuiescence", Default = true, HelpText = "Disable quiescence search (true/false).")]
        public bool DisableQuiescence { get; set; } = true;

        [Option("logGenMode", Default = true, HelpText = "Enable log-generation mode (true/false).")]
        public bool LogGenMode { get; set; } = true;

        [Option("epsilonStart", Default = 0.25, HelpText = "Opening epsilon value (expected 0..1).")]
        public double EpsilonStart { get; set; } = 0.25;

        [Option("epsilonMid", Default = 0.10, HelpText = "Midgame epsilon value (expected 0..1).")]
        public double EpsilonMid { get; set; } = 0.10;

        [Option("epsilonLate", Default = 0.02, HelpText = "Endgame epsilon value (expected 0..1).")]
        public double EpsilonLate { get; set; } = 0.02;

        [Option("epsilonPly1", Default = 10, HelpText = "First epsilon schedule pivot ply (expected >= 0).")]
        public int EpsilonPly1 { get; set; } = 10;

        [Option("epsilonPly2", Default = 25, HelpText = "Second epsilon schedule pivot ply (expected >= 0 and >= epsilonPly1).")]
        public int EpsilonPly2 { get; set; } = 25;

        [Option("nodesMin", HelpText = "Minimum randomized node budget (expected > 0).")]
        public int? NodesMin { get; set; }

        [Option("nodesMax", HelpText = "Maximum randomized node budget (expected > 0 and >= nodesMin).")]
        public int? NodesMax { get; set; }

        [Option("topKSet", HelpText = "CSV of top-K values (e.g., 1,2,4).")]
        public string? TopKSet { get; set; }

        [Option("tempSet", HelpText = "CSV of temperature values (e.g., 0.8,1.0,1.2).")]
        public string? TempSet { get; set; }

        [Option("profileMode", Default = ProfileModeOption.Fixed, HelpText = "Profile mode: Fixed or Random.")]
        public ProfileModeOption ProfileMode { get; set; } = ProfileModeOption.Fixed;

        [Option("weakSideChance", Default = 0.30, HelpText = "Chance to assign a weak side (expected 0..1).")]
        public double WeakSideChance { get; set; } = 0.30;

        [Option("weakSideNodesScale", Default = 0.40, HelpText = "Node scaling for weak side (commonly 0.01..10).")]
        public double WeakSideNodesScale { get; set; } = 0.40;

        [Option("symmetryMode", Default = SymmetryModeOption.None, HelpText = "Symmetry logging mode: None, Random, or All.")]
        public SymmetryModeOption SymmetryMode { get; set; } = SymmetryModeOption.None;
    }
}
