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

        [Option("aiDepth", Default = 3, HelpText = "Search depth (commonly 1..12).")]
        public int AiDepth { get; set; } = 3;

        [Option("useOrthogonalOnlyCapture", Default = false, HelpText = "Use orthogonal-only capture rule (true/false).")]
        public bool UseOrthogonalOnlyCapture { get; set; } = false;

        [Option("useMLRootOnly", Default = false, HelpText = "Use ML evaluator on root only (true/false).")]
        public bool UseMLRootOnly { get; set; } = false;

        [Option("disableQuiescence", HelpText = "Disable quiescence search (true/false). If omitted, auto-default is used.")]
        public bool? DisableQuiescence { get; set; }
    }
}
