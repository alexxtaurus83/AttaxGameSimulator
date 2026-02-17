using CommandLine;

namespace Attax.Console {
    [Verb("validate-log", HelpText = "Validate a training log file.")]
    internal sealed class ValidateLogOptions {
        [Option("path", Default = "training_data.bin", HelpText = "Path to training log file.")]
        public string Path { get; set; } = "training_data.bin";

        [Option("strict", Default = true, HelpText = "Enable strict validation mode (true/false).")]
        public bool Strict { get; set; } = true;
    }
}
