using System;
using System.Globalization;
using Attax.Core;
using Attax.Core.Utils;

namespace Attax.Play {
    /// <summary>
    /// Opening boards for self-play and the arena: the standard four-piece start, optionally with random blocked cells exactly as the
    /// game places them (<see cref="AtaxxAIEngine.GenerateRandomBlockedCellPositions"/>: spread over board zones, only on empty squares).
    /// The game lets the player choose 0 to 6 blocked cells; a range such as "0,6" draws the count uniformly per game/pair.
    /// </summary>
    public static class StartBoard {
        public const int MaxSupportedBlocked = 12;

        /// <summary>Parses "N" or "min,max" (inclusive). Throws FormatException with a precise message.</summary>
        public static (int min, int max) ParseRange(string text, string optionName = "--blocked") {
            if (string.IsNullOrWhiteSpace(text)) return (0, 0);
            var parts = text.Split(',');
            int a, b;
            bool ok = parts.Length == 1
                ? int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out a) && (b = a) == a
                : parts.Length == 2 && int.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out a) && int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out b);
            if (!ok) throw new FormatException($"{optionName} expects 'N' or 'min,max' (whole numbers), got '{text}'.");
            a = int.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
            b = parts.Length == 1 ? a : int.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
            if (a > b) throw new FormatException($"{optionName}: min ({a}) is greater than max ({b}).");
            if (b > MaxSupportedBlocked) throw new FormatException($"{optionName}: at most {MaxSupportedBlocked} blocked cells are supported (the game uses 0 to 6), got {b}.");
            return (a, b);
        }

        public static BitboardState Standard() {
            var b = new BitboardState();
            b.RedPieces = (1UL << 0) | (1UL << 48);
            b.BluePieces = (1UL << 6) | (1UL << 42);
            return b;
        }

        /// <summary>
        /// Standard start plus random blocks. <paramref name="rng"/> is only consulted when max &gt; 0, so a 0,0 range leaves every
        /// existing random stream untouched. The Zobrist hash is not set; callers compute it for the side to move.
        /// </summary>
        public static BitboardState Create(AtaxxAIEngine referee, int minBlocked, int maxBlocked, Random rng) {
            var board = Standard();
            if (maxBlocked <= 0) return board;
            int count = minBlocked == maxBlocked ? minBlocked : rng.Next(minBlocked, maxBlocked + 1);
            if (count <= 0) return board;
            foreach (var (x, y) in referee.GenerateRandomBlockedCellPositions(count, board, rng.Next()))
                board.BlockedSquares |= 1UL << AtaxxAIEngine.GetBitIndex(x, y);
            return board;
        }

        /// <summary>
        /// Builds an opening board from a board shape ID, "all" (random catalog shape), or "random:holes=H,blocked=B".
        /// </summary>
        public static BitboardState CreateFromShape(string boardSpec, int seed, PlayerColor firstPlayer = PlayerColor.Red) {
            if (string.IsNullOrWhiteSpace(boardSpec)) {
                return RandomShapeGenerator.Generate(BoardShapeCatalog.Default, seed, firstPlayer);
            }
            string spec = boardSpec.Trim();
            if (string.Equals(spec, "all", StringComparison.OrdinalIgnoreCase)) {
                var shape = BoardShapeCatalog.PickRandom(new Random(seed)); // NOSONAR
                return RandomShapeGenerator.Generate(shape, seed, firstPlayer);
            }
            if (spec.StartsWith("random:", StringComparison.OrdinalIgnoreCase)) {
                int holes = 0;
                int blocked = 0;
                string rest = spec.Substring("random:".Length);
                foreach (var part in rest.Split(',', StringSplitOptions.RemoveEmptyEntries)) {
                    var kv = part.Split('=', 2);
                    if (kv.Length != 2) throw new FormatException($"Invalid --board random spec '{boardSpec}'. Expected random:holes=H,blocked=B.");
                    string k = kv[0].Trim().ToLowerInvariant();
                    if (!int.TryParse(kv[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < 0)
                        throw new FormatException($"Invalid integer '{kv[1]}' in --board '{boardSpec}'.");
                    if (k == "holes") holes = v;
                    else if (k == "blocked") blocked = v;
                    else throw new FormatException($"Unknown key '{k}' in --board '{boardSpec}'.");
                }
                return CreateRandom(holes, blocked, seed, firstPlayer);
            }
            if (!BoardShapeCatalog.TryGetById(spec, out var explicitShape)) {
                throw new ArgumentException($"Unknown board shape '{boardSpec}'.", nameof(boardSpec));
            }
            return RandomShapeGenerator.Generate(explicitShape, seed, firstPlayer);
        }

        public static BitboardState CreateRandom(int holes, int blocked, int seed, PlayerColor firstPlayer = PlayerColor.Red) {
            return RandomShapeGenerator.Generate(holes, blocked, seed, firstPlayer);
        }
    }
}
