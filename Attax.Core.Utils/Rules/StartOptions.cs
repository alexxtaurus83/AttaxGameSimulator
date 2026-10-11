using System;

namespace Attax.Core.Utils {
    public sealed class StartOptions {
        public int BlockedCells;
        public int Seed;
        public PlayerColor FirstPlayer = PlayerColor.Red;

        public StartOptions() { }

        public StartOptions(int blockedCells, int seed, PlayerColor firstPlayer = PlayerColor.Red) {
            BlockedCells = blockedCells;
            Seed = seed;
            FirstPlayer = firstPlayer;
        }
    }
}
