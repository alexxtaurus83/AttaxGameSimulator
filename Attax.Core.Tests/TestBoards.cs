using System;
using Attax.Core;

namespace Attax.Core.Tests {
    internal static class TestBoards {
        // Rows are top to bottom (y = 0..6), columns left to right (x = 0..6).
        // 'R' red, 'B' blue, 'X' blocked, '.' empty.
        public static BitboardState Parse(params string[] rows) {
            if (rows.Length != 7) throw new ArgumentException("need 7 rows");
            var b = new BitboardState();
            for (int y = 0; y < 7; y++) {
                if (rows[y].Length != 7) throw new ArgumentException($"row {y} needs 7 chars");
                for (int x = 0; x < 7; x++) {
                    ulong bit = 1UL << (y * 7 + x);
                    switch (rows[y][x]) {
                        case 'R': b.RedPieces |= bit; break;
                        case 'B': b.BluePieces |= bit; break;
                        case 'X': b.BlockedSquares |= bit; break;
                        case '.': break;
                        default: throw new ArgumentException("bad char " + rows[y][x]);
                    }
                }
            }
            return b;
        }

        public static AtaxxAIEngine NewEngine(int depth = 3, long? maxNodes = null, bool training = true, bool parallel = false) {
            var cfg = new AtaxxAIEngine.AIEngineConfig { DisableRandomRootTies = true,
                AiDepth = depth,
                TrainingMode = training,
                DisableParallelRootSearch = !parallel,
                DisableQuiescenceSearch = true,
                Seed = 1
            };
            return new AtaxxAIEngine(Prm.Base(), cfg) { MaxNodes = maxNodes };
        }

        public static AtaxxAIEngine NewEngineWith(EngineParams p, int depth = 3, bool training = true) {
            var cfg = new AtaxxAIEngine.AIEngineConfig { DisableRandomRootTies = true,
                AiDepth = depth, TrainingMode = training, DisableParallelRootSearch = true, DisableQuiescenceSearch = true, Seed = 1
            };
            return new AtaxxAIEngine(p, cfg);
        }

        public static void SetBoard(AtaxxAIEngine e, BitboardState b, PlayerColor side) {
            e.Board = b;
            e.Board.ZobristHash = e.ComputeZobristHash(b, side);
        }
    }
}
