using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Attax.Core;
using Attax.Core.Utils;
using Attax.Play;
using Xunit;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core.Tests {
    public class BaselineFixtureGenerator {
        private static string GetFixturesDir() {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string projDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Fixtures"));
            if (Directory.Exists(projDir)) return projDir;
            string candidate = Path.Combine(baseDir, "Fixtures");
            Directory.CreateDirectory(candidate);
            return candidate;
        }

        // Utility method to generate fixtures on baseline. Not run during normal testing.
        public static void GenerateAllFixtures() {
            string dir = GetFixturesDir();
            GenerateEngineMoves(Path.Combine(dir, "engine_moves_golden.txt"));
            GeneratePlayouts(Path.Combine(dir, "playouts_golden.txt"));
            GenerateLowLevel(Path.Combine(dir, "low_level_golden.txt"));
            GenerateOpenings(Path.Combine(dir, "openings_golden.txt"));
        }

        private static void GenerateEngineMoves(string path) {
            var sb = new StringBuilder();
            sb.AppendLine("# Baseline engine moves golden (commit b67ca5c)");
            var positions = new List<(BitboardState b, PlayerColor side)>();

            // 1. Standard start
            positions.Add((StartBoard.Standard(), PlayerColor.Red));

            // 2. Blocked cell openings (seeds 0..49 with counts 1, 3, 6) -> 150 positions
            var referee = TestBoards.NewEngine();
            for (int seed = 0; seed < 50; seed++) {
                foreach (int count in new[] { 1, 3, 6 }) {
                    var b = StartBoard.Standard();
                    var blocks = referee.GenerateRandomBlockedCellPositions(count, b, seed);
                    foreach (var (x, y) in blocks) {
                        b.BlockedSquares |= 1UL << BoardLookup.GetBitIndex(x, y);
                    }
                    positions.Add((b, PlayerColor.Red));
                }
            }

            // 3. Random midgame positions -> 49 positions (total = 200)
            var midgame = RefactorGoldenTests.RandomPositions(49, 9999);
            positions.AddRange(midgame);

            var engine = new AtaxxAIEngine(Prm.Base(), new AIEngineConfig {
                DisableRandomRootTies = true,
                AiDepth = 2,
                TrainingMode = true,
                DisableParallelRootSearch = true,
                DisableQuiescenceSearch = true,
                Seed = 42
            });

            for (int i = 0; i < positions.Count; i++) {
                var (b, side) = positions[i];
                b.ZobristHash = ZobristHasher.ComputeHash(b, side);
                TestBoards.SetBoard(engine, b.Clone(), side);
                engine.AIPlayerColor = side;
                var moves = engine.GetAllValidMoves(b, side);
                Move best = engine.GetBestMove(side);
                var after = b.Clone();
                if (!best.Equals(default(Move))) {
                    engine.MakeMove(after, best, side);
                }
                sb.AppendLine($"{i}|{side}|0x{b.RedPieces:X16}|0x{b.BluePieces:X16}|0x{b.BlockedSquares:X16}|0x{b.ZobristHash:X16}|{best.FromX},{best.FromY},{best.ToX},{best.ToY}|{moves.Count}|0x{after.RedPieces:X16}|0x{after.BluePieces:X16}|0x{after.BlockedSquares:X16}|0x{after.ZobristHash:X16}");
            }

            File.WriteAllText(path, sb.ToString());
        }

        private static void GeneratePlayouts(string path) {
            var sb = new StringBuilder();
            sb.AppendLine("# Baseline playouts golden (50 games, depth 2, safety bound 80 plies)");

            for (int g = 0; g < 50; g++) {
                int seed = 1000 + g;
                var red = new AtaxxAIEngine(Prm.Base(), new AIEngineConfig {
                    DisableRandomRootTies = true, AiDepth = 2, TrainingMode = true,
                    DisableParallelRootSearch = true, DisableQuiescenceSearch = true, Seed = seed
                });
                var blue = new AtaxxAIEngine(Prm.Base(), new AIEngineConfig {
                    DisableRandomRootTies = true, AiDepth = 2, TrainingMode = true,
                    DisableParallelRootSearch = true, DisableQuiescenceSearch = true, Seed = seed + 500
                });

                var board = StartBoard.Standard();
                int blockCount = g % 4; // 0..3 blocks
                if (blockCount > 0) {
                    var blocks = red.GenerateRandomBlockedCellPositions(blockCount, board, seed);
                    foreach (var (x, y) in blocks) {
                        board.BlockedSquares |= 1UL << BoardLookup.GetBitIndex(x, y);
                    }
                }
                var side = PlayerColor.Red;
                var moveList = new List<string>();

                int ply = 0;
                while (ply < 80 && !red.IsGameOver(board)) {
                    var active = side == PlayerColor.Red ? red : blue;
                    TestBoards.SetBoard(active, board.Clone(), side);
                    active.AIPlayerColor = side;
                    var legal = active.GetAllValidMoves(board, side);
                    if (legal.Count == 0) break;
                    Move m = active.GetBestMove(side);
                    if (m.Equals(default(Move))) break;
                    moveList.Add($"{m.FromX},{m.FromY},{m.ToX},{m.ToY}");
                    active.MakeMove(board, m, side);
                    side = SwitchPlayer(side);
                    ply++;
                }

                var terminal = AttaxRules.GetTerminalResult(board);
                sb.AppendLine($"{seed}|{ply}|{string.Join(";", moveList)}|0x{board.RedPieces:X16}|0x{board.BluePieces:X16}|0x{board.BlockedSquares:X16}|0x{board.ZobristHash:X16}|{terminal}");
            }

            File.WriteAllText(path, sb.ToString());
        }

        private static void GenerateLowLevel(string path) {
            var sb = new StringBuilder();
            sb.AppendLine("# Baseline low-level operations golden (500 positions)");

            var positions = RefactorGoldenTests.RandomPositions(500, 7777);
            for (int i = 0; i < positions.Count; i++) {
                var (b, side) = positions[i];
                var term = AttaxRules.GetTerminalResult(b);
                bool hasMoveRed = AttaxRules.HasAnyLegalMove(b, PlayerColor.Red);
                bool hasMoveBlue = AttaxRules.HasAnyLegalMove(b, PlayerColor.Blue);

                var legalActions = new List<int>();
                ActionCodec.GetLegalActions(b, side, legalActions);
                ulong actionFnv = 14695981039346656037UL;
                foreach (int act in legalActions) {
                    actionFnv = RefactorGoldenTests.Fnv(actionFnv, act);
                }

                // Sample moves from GetValidMovesFrom
                var legalMoves = new List<Move>();
                ulong movesFromFnv = 14695981039346656037UL;
                for (int fy = 0; fy < 7; fy++) {
                    for (int fx = 0; fx < 7; fx++) {
                        var movesNormal = AttaxRules.GetValidMovesFrom(b, side, fx, fy, false);
                        var movesSuper = AttaxRules.GetValidMovesFrom(b, side, fx, fy, true);
                        legalMoves.AddRange(movesNormal);
                        movesFromFnv = RefactorGoldenTests.Fnv(movesFromFnv, movesNormal.Count);
                        movesFromFnv = RefactorGoldenTests.Fnv(movesFromFnv, movesSuper.Count);
                    }
                }

                // Digest moves results
                ulong moveFnv = 14695981039346656037UL;
                foreach (var m in legalMoves) {
                    var copy = b.Clone();
                    copy.ZobristHash = ZobristHasher.ComputeHash(copy, side);
                    var res = AttaxRules.MakeMove(copy, m, side);
                    moveFnv = RefactorGoldenTests.Fnv(moveFnv, (long)copy.RedPieces);
                    moveFnv = RefactorGoldenTests.Fnv(moveFnv, (long)copy.BluePieces);
                    moveFnv = RefactorGoldenTests.Fnv(moveFnv, (long)copy.BlockedSquares);
                    moveFnv = RefactorGoldenTests.Fnv(moveFnv, (long)copy.ZobristHash);
                    moveFnv = RefactorGoldenTests.Fnv(moveFnv, res.FlippedPieces.Count);
                    foreach (var (fx, fy) in res.FlippedPieces) {
                        moveFnv = RefactorGoldenTests.Fnv(moveFnv, (fy * 7 + fx));
                    }
                }

                // Convert checks for (3,3)
                var convPlayerBoard = b.Clone();
                convPlayerBoard.ZobristHash = ZobristHasher.ComputeHash(convPlayerBoard, side);
                bool convPlayerOk = AttaxRules.TryConvertEnemyChip(convPlayerBoard, side, 3, 3, ChipConversion.ToPlayer);

                var convBlockedBoard = b.Clone();
                convBlockedBoard.ZobristHash = ZobristHasher.ComputeHash(convBlockedBoard, side);
                bool convBlockedOk = AttaxRules.TryConvertEnemyChip(convBlockedBoard, side, 3, 3, ChipConversion.ToBlocked);

                sb.AppendLine($"{i}|0x{b.RedPieces:X16}|0x{b.BluePieces:X16}|0x{b.BlockedSquares:X16}|{side}|{term}|{hasMoveRed}|{hasMoveBlue}|0x{actionFnv:X16}|0x{movesFromFnv:X16}|0x{moveFnv:X16}|{convPlayerOk}:0x{convPlayerBoard.ZobristHash:X16}|{convBlockedOk}:0x{convBlockedBoard.ZobristHash:X16}");
            }

            File.WriteAllText(path, sb.ToString());
        }

        private static void GenerateOpenings(string path) {
            var sb = new StringBuilder();
            sb.AppendLine("# Baseline openings matrix golden (seeds 0..999, counts 1..12)");

            var referee = TestBoards.NewEngine();
            for (int seed = 0; seed < 1000; seed++) {
                var start = StartBoard.Standard();
                for (int count = 1; count <= 12; count++) {
                    var blocks = referee.GenerateRandomBlockedCellPositions(count, start, seed);
                    ulong mask = 0;
                    foreach (var (x, y) in blocks) {
                        mask |= 1UL << BoardLookup.GetBitIndex(x, y);
                    }
                    sb.AppendLine($"{seed}|{count}|0x{mask:X16}");
                }
            }

            // StartBoard.Create draws
            for (int seed = 0; seed < 50; seed++) {
                var rng = new Random(seed);
                var b = StartBoard.Create(referee, 1, 6, rng);
                sb.AppendLine($"STARTBOARD|{seed}|0x{b.BlockedSquares:X16}");
            }

            File.WriteAllText(path, sb.ToString());
        }
    }
}
