using CommandLine;

namespace Attax.Console {
    [Verb("mirror-test", HelpText = "Run deterministic mirror sanity test.")]
    internal sealed class MirrorTestOptions {
        [Option("model", Default = "heuristic", HelpText = "Model path or 'heuristic'.")]
        public string Model { get; set; } = "heuristic";

        [Option("ort", Default = OrtOption.Cpu, HelpText = "ONNX Runtime provider: Cpu or Cuda.")]
        public OrtOption Ort { get; set; } = OrtOption.Cpu;

        [Option("side", Default = MirrorSideOption.Red, HelpText = "Side to evaluate: Red or Blue.")]
        public MirrorSideOption Side { get; set; } = MirrorSideOption.Red;
    }
}
