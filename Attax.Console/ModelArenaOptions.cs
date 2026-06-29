using CommandLine;

namespace Attax.Console {
    [Verb("modelarena", HelpText = "Run model-vs-model arena games.")]
    internal sealed class ModelArenaOptions {
        [Option("model1", Default = "heuristic", HelpText = "Model 1 path or 'heuristic'.")]
        public string Model1 { get; set; } = "heuristic";

        [Option("model2", Default = "heuristic", HelpText = "Model 2 path or 'heuristic'.")]
        public string Model2 { get; set; } = "heuristic";

        [Option("games", Default = 100, HelpText = "Number of arena games (expected > 0).")]
        public int Games { get; set; } = 100;

        [Option("ort", Default = OrtOption.Cpu, HelpText = "ONNX Runtime provider: Cpu or Cuda.")]
        public OrtOption Ort { get; set; } = OrtOption.Cpu;

        [Option("aiDepthM1", Default = 1, HelpText = "Search depth for Model 1 (commonly 1..12).")]
        public int AiDepthM1 { get; set; } = 1;

        [Option("aiDepthM2", Default = 1, HelpText = "Search depth for Model 2 (commonly 1..12).")]
        public int AiDepthM2 { get; set; } = 1;

        [Option("maxNodesM1", Default = 1000, HelpText = "Node budget per move for Model 1")]
        public int MaxNodesM1 { get; set; } = 1000;

        [Option("maxNodesM2", Default = 1000, HelpText = "Node budget per move for Model 2")]
        public int MaxNodesM2 { get; set; } = 1000;

        [Option("disableQuiescence", Default = "true", HelpText = "Disable quiescence search for heuristic players (true/false). Always disabled for model players regardless of this flag. Default true keeps arena fast; set false to let heuristic search tactical sequences deeper.")]
        public string DisableQuiescence { get; set; } = "true";

        [Option("useOrthogonalOnlyCapture", Default = false, HelpText = "Use orthogonal-only capture rule (true/false).")]
        public bool UseOrthogonalOnlyCapture { get; set; } = false;

        [Option("useMLRootOnly", Default = false, HelpText = "Use ML evaluator on root only (true/false).")]
        public bool UseMLRootOnly { get; set; } = false;

        [Option("arenaTemp", Default = 0.0, HelpText = "Softmax temperature for move sampling when both sides are models (0 = greedy/deterministic). Has no effect when either side is heuristic.")]
        public double ArenaTemp { get; set; } = 0.0;

        [Option("arenaTopK", Default = 3, HelpText = "Top-K candidates considered for temperature sampling (only active when arenaTemp > 0 and both sides are models). Must be >= 1.")]
        public int ArenaTopK { get; set; } = 3;

        [Option("arenaOpeningPlies", Default = 0, HelpText = "Random opening plies played before models take over. Guarantees game diversity independent of score scale. Even values recommended (e.g. 4 or 6) so both sides play the same number of random moves.")]
        public int ArenaOpeningPlies { get; set; } = 0;

        [Option("runGamesInParallel", Default = "auto", HelpText = "Override game-level parallelism: auto (default strategy), true (force parallel for throughput), false (force sequential for realistic per-move timing).")]
        public string RunGamesInParallel { get; set; } = "auto";
    }
}
