using System;
using Attax.Core;
using Attax.Core.Utils;

namespace Attax.Play {
    /// <summary>
    /// Thin simulator facade over <see cref="StartPosition"/>.
    /// </summary>
    public static class StartBoard {
        public const int MaxSupportedBlocked = StartPosition.MaxSupportedBlocked;

        public static (int min, int max) ParseRange(string text, string optionName = "--blocked")
            => StartPosition.ParseBlockedRange(text, optionName);

        public static BitboardState Standard() => StartPosition.Standard();

        public static BitboardState Create(AtaxxAIEngine referee, int minBlocked, int maxBlocked, Random rng)
            => StartPosition.Create(minBlocked, maxBlocked, rng);

        public static BitboardState CreateFromShape(string boardSpec, int seed, PlayerColor firstPlayer = PlayerColor.Red)
            => StartPosition.CreateFromShapeSpec(boardSpec, seed, firstPlayer);

        public static BitboardState CreateRandom(int holes, int blocked, int seed, PlayerColor firstPlayer = PlayerColor.Red)
            => RandomShapeGenerator.Generate(holes, blocked, seed, firstPlayer);
    }
}
