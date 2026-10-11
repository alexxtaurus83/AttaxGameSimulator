using System;

namespace Attax.Core.Utils {
    public static class EngineInfo {
        public const int RulesRevision = 1;
        public const string OpeningAlgorithmId = "zoned-system-random-v1";

        private static readonly (int seed, int count, ulong expectedMask)[] GoldenVectors = new (int, int, ulong)[] {
            (0, 3, 0x0000000080000220UL),
            (42, 6, 0x000000A020290000UL),
            (123, 1, 0x0000020000000000UL),
            (999, 12, 0x0000000000C3C70EUL)
        };

        public static bool VerifyOpeningCompatibility(out string error) {
            var standard = StartPosition.Standard();
            foreach (var (seed, count, expectedMask) in GoldenVectors) {
                var blocks = StartPosition.GenerateBlockedCellPositions(count, standard, seed);
                ulong actualMask = 0UL;
                foreach (var (x, y) in blocks) {
                    actualMask |= 1UL << BoardLookup.GetBitIndex(x, y);
                }

                if (actualMask != expectedMask) {
                    error = $"Opening vector mismatch on seed {seed}, count {count}: expected 0x{expectedMask:X16}, got 0x{actualMask:X16}.";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }
    }
}
