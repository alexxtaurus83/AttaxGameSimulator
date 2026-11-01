using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Attax.Core;
using Attax.Core.Utils;
using Attax.Play;
using Xunit;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core.Tests {
    public class CompatibilityTests {
        private static string GetFixturePath(string filename) {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string inBin = Path.Combine(baseDir, "Fixtures", filename);
            if (File.Exists(inBin)) return inBin;
            string inProj = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Fixtures", filename));
            if (File.Exists(inProj)) return inProj;
            throw new FileNotFoundException($"Fixture file {filename} not found in {inBin} or {inProj}");
        }

        [Fact]
        public void EngineMoveGolden_MatchesFixtures() {
            string path = GetFixturePath("engine_moves_golden.txt");
            var lines = File.ReadAllLines(path);

            var engine = new AtaxxAIEngine(Prm.Base(), new AIEngineConfig {
                DisableRandomRootTies = true,
                AiDepth = 2,
                TrainingMode = true,
                DisableParallelRootSearch = true,
                DisableQuiescenceSearch = true,
                Seed = 42
            });

            foreach (var line in lines) {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                var parts = line.Split('|');
                int idx = int.Parse(parts[0]);
                PlayerColor side = (PlayerColor)Enum.Parse(typeof(PlayerColor), parts[1]);
                ulong red = ulong.Parse(parts[2].Substring(2), NumberStyles.HexNumber);
                ulong blue = ulong.Parse(parts[3].Substring(2), NumberStyles.HexNumber);
                ulong blocked = ulong.Parse(parts[4].Substring(2), NumberStyles.HexNumber);
                ulong initialHash = ulong.Parse(parts[5].Substring(2), NumberStyles.HexNumber);
                var moveParts = parts[6].Split(',');
                Move expBest = new Move(int.Parse(moveParts[0]), int.Parse(moveParts[1]), int.Parse(moveParts[2]), int.Parse(moveParts[3]));
                int expMovesCount = int.Parse(parts[7]);
                ulong expAfterRed = ulong.Parse(parts[8].Substring(2), NumberStyles.HexNumber);
                ulong expAfterBlue = ulong.Parse(parts[9].Substring(2), NumberStyles.HexNumber);
                ulong expAfterBlocked = ulong.Parse(parts[10].Substring(2), NumberStyles.HexNumber);
                ulong expAfterHash = ulong.Parse(parts[11].Substring(2), NumberStyles.HexNumber);

                var b = new BitboardState { RedPieces = red, BluePieces = blue, BlockedSquares = blocked, ZobristHash = initialHash };
                Assert.Equal(initialHash, ZobristHasher.ComputeHash(b, side));
                TestBoards.SetBoard(engine, b.Clone(), side);
                engine.AIPlayerColor = side;

                var legal = engine.GetAllValidMoves(b, side);
                Assert.Equal(expMovesCount, legal.Count);

                Move actualBest = engine.GetBestMove(side);
                Assert.Equal(expBest, actualBest);

                var after = b.Clone();
                if (!actualBest.Equals(default(Move))) {
                    engine.MakeMove(after, actualBest, side);
                }
                Assert.Equal(expAfterRed, after.RedPieces);
                Assert.Equal(expAfterBlue, after.BluePieces);
                Assert.Equal(expAfterBlocked, after.BlockedSquares);
                Assert.Equal(expAfterHash, after.ZobristHash);
            }
        }

        [Fact]
        public void PlayoutGolden_MatchesFixtures() {
            string path = GetFixturePath("playouts_golden.txt");
            var lines = File.ReadAllLines(path);

            foreach (var line in lines) {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                var parts = line.Split('|');
                int seed = int.Parse(parts[0]);
                int expPlies = int.Parse(parts[1]);
                string movesStr = parts[2];
                ulong expFinalRed = ulong.Parse(parts[3].Substring(2), NumberStyles.HexNumber);
                ulong expFinalBlue = ulong.Parse(parts[4].Substring(2), NumberStyles.HexNumber);
                ulong expFinalBlocked = ulong.Parse(parts[5].Substring(2), NumberStyles.HexNumber);
                ulong expFinalHash = ulong.Parse(parts[6].Substring(2), NumberStyles.HexNumber);
                TerminalResult expTerm = (TerminalResult)Enum.Parse(typeof(TerminalResult), parts[7]);

                var red = new AtaxxAIEngine(Prm.Base(), new AIEngineConfig {
                    DisableRandomRootTies = true, AiDepth = 2, TrainingMode = true,
                    DisableParallelRootSearch = true, DisableQuiescenceSearch = true, Seed = seed
                });
                var blue = new AtaxxAIEngine(Prm.Base(), new AIEngineConfig {
                    DisableRandomRootTies = true, AiDepth = 2, TrainingMode = true,
                    DisableParallelRootSearch = true, DisableQuiescenceSearch = true, Seed = seed + 500
                });

                var board = StartBoard.Standard();
                int blockCount = (seed - 1000) % 4;
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

                Assert.Equal(expPlies, ply);
                Assert.Equal(movesStr, string.Join(";", moveList));
                Assert.Equal(expFinalRed, board.RedPieces);
                Assert.Equal(expFinalBlue, board.BluePieces);
                Assert.Equal(expFinalBlocked, board.BlockedSquares);
                Assert.Equal(expFinalHash, board.ZobristHash);
                Assert.Equal(expTerm, AttaxRules.GetTerminalResult(board));
            }
        }

        [Fact]
        public void LowLevelGolden_MatchesFixtures() {
            string path = GetFixturePath("low_level_golden.txt");
            var lines = File.ReadAllLines(path);

            var positions = RefactorGoldenTests.RandomPositions(500, 7777);
            int lineIdx = 0;

            for (int i = 0; i < positions.Count; i++) {
                var (b, side) = positions[i];
                while (lineIdx < lines.Length && (string.IsNullOrWhiteSpace(lines[lineIdx]) || lines[lineIdx].StartsWith("#"))) {
                    lineIdx++;
                }
                if (lineIdx >= lines.Length) break;
                var parts = lines[lineIdx++].Split('|');

                var expTerm = (TerminalResult)Enum.Parse(typeof(TerminalResult), parts[5]);
                bool expHasRed = bool.Parse(parts[6]);
                bool expHasBlue = bool.Parse(parts[7]);
                ulong expActionFnv = ulong.Parse(parts[8].Substring(2), NumberStyles.HexNumber);
                ulong expMovesFromFnv = ulong.Parse(parts[9].Substring(2), NumberStyles.HexNumber);
                ulong expMoveFnv = ulong.Parse(parts[10].Substring(2), NumberStyles.HexNumber);

                Assert.Equal(expTerm, AttaxRules.GetTerminalResult(b));
                Assert.Equal(expHasRed, AttaxRules.HasAnyLegalMove(b, PlayerColor.Red));
                Assert.Equal(expHasBlue, AttaxRules.HasAnyLegalMove(b, PlayerColor.Blue));

                var legalActions = new List<int>();
                ActionCodec.GetLegalActions(b, side, legalActions);
                ulong actionFnv = 14695981039346656037UL;
                foreach (int act in legalActions) {
                    actionFnv = RefactorGoldenTests.Fnv(actionFnv, act);
                }
                Assert.Equal(expActionFnv, actionFnv);

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
                Assert.Equal(expMovesFromFnv, movesFromFnv);

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
                Assert.Equal(expMoveFnv, moveFnv);
            }
        }

        [Fact]
        public void OpeningsGolden_MatchesFixtures() {
            string path = GetFixturePath("openings_golden.txt");
            var lines = File.ReadAllLines(path);

            var referee = TestBoards.NewEngine();
            int lineIdx = 0;

            for (int seed = 0; seed < 1000; seed++) {
                var start = StartBoard.Standard();
                for (int count = 1; count <= 12; count++) {
                    while (lineIdx < lines.Length && (string.IsNullOrWhiteSpace(lines[lineIdx]) || lines[lineIdx].StartsWith("#"))) {
                        lineIdx++;
                    }
                    var parts = lines[lineIdx++].Split('|');
                    int expSeed = int.Parse(parts[0]);
                    int expCount = int.Parse(parts[1]);
                    ulong expMask = ulong.Parse(parts[2].Substring(2), NumberStyles.HexNumber);

                    var blocks = referee.GenerateRandomBlockedCellPositions(count, start, seed);
                    ulong mask = 0;
                    foreach (var (x, y) in blocks) {
                        mask |= 1UL << BoardLookup.GetBitIndex(x, y);
                    }
                    Assert.Equal(expSeed, seed);
                    Assert.Equal(expCount, count);
                    Assert.Equal(expMask, mask);
                }
            }

            for (int seed = 0; seed < 50; seed++) {
                while (lineIdx < lines.Length && (string.IsNullOrWhiteSpace(lines[lineIdx]) || lines[lineIdx].StartsWith("#"))) {
                    lineIdx++;
                }
                var parts = lines[lineIdx++].Split('|');
                int expSeed = int.Parse(parts[1]);
                ulong expMask = ulong.Parse(parts[2].Substring(2), NumberStyles.HexNumber);

                var rng = new Random(seed);
                var b = StartBoard.Create(referee, 1, 6, rng);
                Assert.Equal(expSeed, seed);
                Assert.Equal(expMask, b.BlockedSquares);
            }
        }
    }
}
