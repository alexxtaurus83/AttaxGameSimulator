using CommandLine;

namespace Attax.Console {
    [Verb("validate-hash", HelpText = "Validate zobrist hash consistency.")]
    internal sealed class ValidateHashOptions {
        [Option("games", Default = 25, HelpText = "Number of games to simulate (expected > 0).")]
        public int Games { get; set; } = 25;

        [Option("seed", Default = 0, HelpText = "Random seed.")]
        public int Seed { get; set; } = 0;

        [Option("moves", Default = 120, HelpText = "Maximum plies per game (expected > 0).")]
        public int Moves { get; set; } = 120;

        [Option("strict", Default = true, HelpText = "Throw on first mismatch in strict mode (true/false).")]
        public bool Strict { get; set; } = true;
    }
}
