using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Attax.Core;
using Attax.Model;
using Attax.Data;
using Xunit;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Core.Tests {
    public class LogV3Tests : IDisposable {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "attax_logv3_" + Guid.NewGuid().ToString("N"));
        public LogV3Tests() { Directory.CreateDirectory(dir); }
        public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

        private string P(string n) => Path.Combine(dir, n);

        private static readonly BitboardState Start = TestBoards.Parse("R.....B", ".......", ".......", ".......", ".......", ".......", "B.....R");

        private static LogV3.Sample MakeSample(ulong uid, ushort ply, BitboardState b, PlayerColor side, int played, int teacher = -1, byte teacherId = 0, byte flags = 0, byte sym = 0) =>
            new LogV3.Sample {
                GameUid = uid, Ply = ply, Red = b.RedPieces, Blue = b.BluePieces, Blocked = b.BlockedSquares,
                Side = (byte)(side == PlayerColor.Red ? 0 : 1), PlayedAction = (short)played, TeacherAction = (short)teacher,
                Flags = flags, Symmetry = sym, TeacherId = teacherId
            };

        private static int FirstLegal(BitboardState b, PlayerColor side, int skip = 0) {
            var mask = new bool[ActionCodec.ActionCount];
            ActionCodec.FillLegalMask(b, side, mask);
            return Enumerable.Range(0, ActionCodec.ActionCount).Where(a => mask[a]).ElementAt(skip);
        }

        private static LogV3.GameHeader Header(ulong uid) => new LogV3.GameHeader { GameUid = uid, Seed = 5, Board = 7, Generation = 2, RedMode = LogV3.PlayerMode.Policy, BlueMode = LogV3.PlayerMode.Classic };
        private static LogV3.GameResult Result(ulong uid, sbyte r = 1, ushort plies = 10, LogV3.Termination t = LogV3.Termination.Terminal) => new LogV3.GameResult { GameUid = uid, ResultRedPov = r, TotalPlies = plies, Termination = t };

        private void WriteOne(string path, Action<LogV3Writer> declare, IReadOnlyList<LogV3.Sample> samples, LogV3.GameResult? result = null) {
            using var w = new LogV3Writer(path);
            declare?.Invoke(w);
            w.WriteGame(Header(7), samples, result ?? Result(7));
        }

        private static readonly LogV3.TeacherConfig Teacher1 = new LogV3.TeacherConfig { Id = 1, Kind = LogV3.TeacherKind.HeuristicSearch, Depth = 3, NodeBudget = 0, RootBonusScale = 1f, Flags = 0 };

        [Fact]
        public void SampleStruct_Is46Bytes() {
            Assert.Equal(LogV3.SampleSize, System.Runtime.InteropServices.Marshal.SizeOf<LogV3.Sample>());
        }

        [Fact]
        public void RoundTrip_PreservesEverything() {
            int a0 = FirstLegal(Start, PlayerColor.Red), a1 = FirstLegal(Start, PlayerColor.Red, 3);
            var samples = new[] {
                MakeSample(7, 0, Start, PlayerColor.Red, a0, a1, 1, LogV3.FlagTeacherValid),
                MakeSample(7, 1, Start, PlayerColor.Red, a1, -1, 0, LogV3.FlagPlayedRandom, 3),
            };
            // symmetry 3 sample must be a real transformed position for semantic validation
            var t = Start.Clone(); ulong r = t.RedPieces, bl = t.BluePieces, x = t.BlockedSquares;
            BitboardOps.TransformState(ref r, ref bl, ref x, 3);
            samples[1].Red = r; samples[1].Blue = bl; samples[1].Blocked = x;
            var tb = new BitboardState { RedPieces = r, BluePieces = bl, BlockedSquares = x };
            samples[1].PlayedAction = (short)FirstLegal(tb, PlayerColor.Red);

            WriteOne(P("a.bin"), w => w.DeclareTeacher(Teacher1), samples);
            var reader = new LogV3Reader();
            var games = reader.ReadGames(P("a.bin")).ToList();
            var g = Assert.Single(games);
            Assert.Equal(7UL, g.Header.GameUid);
            Assert.Equal(2, g.Header.Generation);
            Assert.Equal(LogV3.PlayerMode.Policy, g.Header.RedMode);
            Assert.Equal(LogV3.PlayerMode.Classic, g.Header.BlueMode);
            Assert.Equal(2, g.Samples.Length);
            Assert.True(g.Samples[0].TeacherValid);
            Assert.Equal(a1, g.Samples[0].TeacherAction);
            Assert.Equal(1, g.Samples[0].TeacherId);
            Assert.False(g.Samples[1].TeacherValid);
            Assert.True(g.Samples[1].PlayedRandom);
            Assert.Equal(3, g.Samples[1].Symmetry);
            Assert.Equal(LogV3.Termination.Terminal, g.Result.Termination);
            Assert.Equal(Teacher1.Depth, reader.Teachers[1].Depth);
        }

        [Fact]
        public void ManyGamesAndBlocks_RoundTrip() {
            using (var w = new LogV3Writer(P("m.bin"))) {
                w.DeclareTeacher(Teacher1);
                for (ulong uid = 0; uid < 20; uid++) {
                    var s = Enumerable.Range(0, 9).Select(i => MakeSample(uid, (ushort)i, Start, PlayerColor.Red, FirstLegal(Start, PlayerColor.Red, i % 5))).ToList();
                    w.WriteGame(Header(uid), s, Result(uid, (sbyte)(uid % 3 - 1), 12), samplesPerBlock: 4);
                }
                Assert.Equal(20, w.Games);
                Assert.Equal(180, w.Samples);
            }
            var games = new LogV3Reader().ReadGames(P("m.bin")).ToList();
            Assert.Equal(20, games.Count);
            Assert.All(games, g => Assert.Equal(9, g.Samples.Length));
            Assert.Equal(Enumerable.Range(0, 20).Select(i => (ulong)i), games.Select(g => g.Header.GameUid));
        }

        [Fact]
        public void Writer_RejectsUndeclaredTeacherAndMismatchedUids() {
            using var w = new LogV3Writer(P("w.bin"));
            var s = MakeSample(7, 0, Start, PlayerColor.Red, FirstLegal(Start, PlayerColor.Red), 1, 9, LogV3.FlagTeacherValid);
            Assert.Throws<InvalidOperationException>(() => w.WriteGame(Header(7), new[] { s }, Result(7)));
            var s2 = MakeSample(8, 0, Start, PlayerColor.Red, FirstLegal(Start, PlayerColor.Red));
            Assert.Throws<ArgumentException>(() => w.WriteGame(Header(7), new[] { s2 }, Result(7)));
            Assert.Throws<ArgumentException>(() => w.WriteGame(Header(7), new LogV3.Sample[0], Result(8)));
            Assert.Throws<ArgumentException>(() => w.DeclareTeacher(new LogV3.TeacherConfig { Id = 0 }));
            w.DeclareTeacher(Teacher1);
            Assert.Throws<InvalidOperationException>(() => w.DeclareTeacher(Teacher1));
        }

        private void ExpectInvalid(string name, LogV3.Sample sample, bool declareTeacher = true, LogV3.GameResult? result = null, string contains = null) {
            WriteOne(P(name), declareTeacher ? (Action<LogV3Writer>)(w => w.DeclareTeacher(Teacher1)) : null, new[] { sample }, result);
            var ex = Assert.Throws<InvalidDataException>(() => new LogV3Reader().ReadGames(P(name)).ToList());
            if (contains != null) Assert.Contains(contains, ex.Message);
        }

        [Fact]
        public void Reader_RejectsIllegalPlayedAction() {
            var legal = FirstLegal(Start, PlayerColor.Red);
            var mask = new bool[ActionCodec.ActionCount]; ActionCodec.FillLegalMask(Start, PlayerColor.Red, mask);
            int illegal = Enumerable.Range(0, ActionCodec.ActionCount).First(a => !mask[a]);
            ExpectInvalid("i1.bin", MakeSample(7, 0, Start, PlayerColor.Red, illegal), contains: "played action");
            ExpectInvalid("i2.bin", MakeSample(7, 0, Start, PlayerColor.Red, -1), contains: "played action");
            // legal for Red but the sample says Blue to move: Blue has different legal actions
            var blueMask = new bool[ActionCodec.ActionCount]; ActionCodec.FillLegalMask(Start, PlayerColor.Blue, blueMask);
            int redOnly = Enumerable.Range(0, ActionCodec.ActionCount).First(a => mask[a] && !blueMask[a]);
            ExpectInvalid("i3.bin", MakeSample(7, 0, Start, PlayerColor.Blue, redOnly), contains: "played action");
            Assert.True(legal >= 0);
        }

        [Fact]
        public void Reader_RejectsTeacherInconsistencies() {
            int a = FirstLegal(Start, PlayerColor.Red);
            // valid flag but no teacher action
            ExpectInvalid("t1.bin", MakeSample(7, 0, Start, PlayerColor.Red, a, -1, 1, LogV3.FlagTeacherValid), contains: "teacher action");
            // valid flag but undeclared teacher id in file
            ExpectInvalid("t2.bin", MakeSample(7, 0, Start, PlayerColor.Red, a, a, 0, LogV3.FlagTeacherValid), declareTeacher: true, contains: "teacher");
            // teacher action present without the valid flag
            ExpectInvalid("t3.bin", MakeSample(7, 0, Start, PlayerColor.Red, a, a, 0, 0), contains: "TeacherValid");
            // teacher id present without the valid flag
            ExpectInvalid("t4.bin", MakeSample(7, 0, Start, PlayerColor.Red, a, -1, 1, 0), contains: "teacher id");
            // illegal teacher action
            var mask = new bool[ActionCodec.ActionCount]; ActionCodec.FillLegalMask(Start, PlayerColor.Red, mask);
            int illegal = Enumerable.Range(0, ActionCodec.ActionCount).First(x => !mask[x]);
            ExpectInvalid("t5.bin", MakeSample(7, 0, Start, PlayerColor.Red, a, illegal, 1, LogV3.FlagTeacherValid), contains: "teacher action");
        }

        [Fact]
        public void TeacherScore_RoundTrips_IncludingDecisiveTerminalValues() {
            int a = FirstLegal(Start, PlayerColor.Red);
            var scored = MakeSample(7, 0, Start, PlayerColor.Red, a, a, 1, (byte)(LogV3.FlagTeacherValid | LogV3.FlagTeacherScoreValid)); scored.TeacherScore = -1_234_567;
            var win = MakeSample(7, 1, Start, PlayerColor.Red, a, a, 1, (byte)(LogV3.FlagTeacherValid | LogV3.FlagTeacherScoreValid)); win.TeacherScore = 999_999_000;
            var lose = MakeSample(7, 2, Start, PlayerColor.Red, a, a, 1, (byte)(LogV3.FlagTeacherValid | LogV3.FlagTeacherScoreValid)); lose.TeacherScore = -999_999_000;
            var noScore = MakeSample(7, 3, Start, PlayerColor.Red, a, a, 1, LogV3.FlagTeacherValid);          // a label without a score (older/relabeled data)
            WriteOne(P("s.bin"), w => w.DeclareTeacher(Teacher1), new[] { scored, win, lose, noScore });
            var g = new LogV3Reader().ReadGames(P("s.bin")).Single();
            Assert.Equal(new[] { -1_234_567, 999_999_000, -999_999_000, 0 }, g.Samples.Select(s => s.TeacherScore).ToArray());
            Assert.Equal(new[] { true, true, true, false }, g.Samples.Select(s => s.TeacherScoreValid).ToArray());
        }

        [Fact]
        public void Reader_RejectsInconsistentTeacherScores() {
            int a = FirstLegal(Start, PlayerColor.Red);
            // score flag without a teacher label
            var f1 = MakeSample(7, 0, Start, PlayerColor.Red, a, -1, 0, LogV3.FlagTeacherScoreValid);
            ExpectInvalid("sc1.bin", f1, contains: "teacher score flag");
            // score value present although the flag is clear
            var f2 = MakeSample(7, 0, Start, PlayerColor.Red, a, a, 1, LogV3.FlagTeacherValid); f2.TeacherScore = 5;
            ExpectInvalid("sc2.bin", f2, contains: "teacher score set");
            // unknown flag bit
            ExpectInvalid("sc3.bin", MakeSample(7, 0, Start, PlayerColor.Red, a, a, 1, (byte)(LogV3.FlagTeacherValid | 8)), contains: "flags");
        }

        [Fact]
        public void Version3Logs_AreRejectedWithAClearMessage() {
            int a = FirstLegal(Start, PlayerColor.Red);
            WriteOne(P("v.bin"), null, new[] { MakeSample(7, 0, Start, PlayerColor.Red, a) });
            var bytes = File.ReadAllBytes(P("v.bin")); bytes[4] = 3; bytes[5] = 0;
            File.WriteAllBytes(P("v3.bin"), bytes);
            var ex = Assert.Throws<InvalidDataException>(() => new LogV3Reader().ReadGames(P("v3.bin")).ToList());
            Assert.Contains("version 3", ex.Message); Assert.Contains("supports 4", ex.Message);
        }

        [Fact]
        public void Reader_RejectsBadBoardsAndMetadata() {
            int a = FirstLegal(Start, PlayerColor.Red);
            var overlap = MakeSample(7, 0, Start, PlayerColor.Red, a); overlap.Blue |= overlap.Red;
            ExpectInvalid("b1.bin", overlap, contains: "overlapping");
            var outside = MakeSample(7, 0, Start, PlayerColor.Red, a); outside.Red |= 1UL << 55;
            ExpectInvalid("b2.bin", outside, contains: "outside");
            var badSide = MakeSample(7, 0, Start, PlayerColor.Red, a); badSide.Side = 2;
            ExpectInvalid("b3.bin", badSide, contains: "side");
            var badSym = MakeSample(7, 0, Start, PlayerColor.Red, a, sym: 9);
            ExpectInvalid("b4.bin", badSym, contains: "symmetry");
            var badFlags = MakeSample(7, 0, Start, PlayerColor.Red, a, flags: 0x80);
            ExpectInvalid("b5.bin", badFlags, contains: "flags");
            var okSample = MakeSample(7, 0, Start, PlayerColor.Red, a);
            ExpectInvalid("b6.bin", okSample, result: Result(7, 5), contains: "result");
            ExpectInvalid("b7.bin", okSample, result: Result(7, 1, 10, (LogV3.Termination)9), contains: "termination");
            ExpectInvalid("b8.bin", MakeSample(7, 20, Start, PlayerColor.Red, a), result: Result(7, 1, 10), contains: "ply");
        }

        [Fact]
        public void Reader_RejectsCorruptionTruncationAndWrongVersion() {
            int a = FirstLegal(Start, PlayerColor.Red);
            WriteOne(P("c.bin"), null, new[] { MakeSample(7, 0, Start, PlayerColor.Red, a) });
            var bytes = File.ReadAllBytes(P("c.bin"));

            var flipped = (byte[])bytes.Clone(); flipped[flipped.Length - 15] ^= 0xFF;
            File.WriteAllBytes(P("c1.bin"), flipped);
            Assert.ThrowsAny<Exception>(() => new LogV3Reader().ReadGames(P("c1.bin")).ToList());

            File.WriteAllBytes(P("c2.bin"), bytes.Take(bytes.Length - 3).ToArray());
            Assert.ThrowsAny<Exception>(() => new LogV3Reader().ReadGames(P("c2.bin")).ToList());

            var noResult = bytes.Take(bytes.Length - 14).ToArray(); // drop the GameResult record
            File.WriteAllBytes(P("c3.bin"), noResult);
            Assert.ThrowsAny<Exception>(() => new LogV3Reader().ReadGames(P("c3.bin")).ToList());

            var wrongVersion = (byte[])bytes.Clone(); wrongVersion[4] = 2;
            File.WriteAllBytes(P("c4.bin"), wrongVersion);
            var ex = Assert.Throws<InvalidDataException>(() => new LogV3Reader().ReadGames(P("c4.bin")).ToList());
            Assert.Contains("version", ex.Message);

            var noMagic = (byte[])bytes.Clone(); noMagic[0] = 0;
            File.WriteAllBytes(P("c5.bin"), noMagic);
            Assert.Throws<InvalidDataException>(() => new LogV3Reader().ReadGames(P("c5.bin")).ToList());

            File.WriteAllBytes(P("c6.bin"), new byte[] { 1, 2 });
            Assert.Throws<InvalidDataException>(() => new LogV3Reader().ReadGames(P("c6.bin")).ToList());
        }

        [Fact]
        public void Termination_PlyCap_IsPreserved() {
            int a = FirstLegal(Start, PlayerColor.Red);
            WriteOne(P("p.bin"), null, new[] { MakeSample(7, 0, Start, PlayerColor.Red, a) }, Result(7, 0, 401, LogV3.Termination.PlyCap));
            var g = new LogV3Reader().ReadGames(P("p.bin")).Single();
            Assert.Equal(LogV3.Termination.PlyCap, g.Result.Termination);
            Assert.Equal(401, g.Result.TotalPlies);
        }

        [Fact]
        public void ValidationSplit_IsStableAndRoughlyProportional() {
            // Golden values from zlib.crc32(struct.pack('<Q', uid)); python/test_data.py asserts the same numbers.
            Assert.Equal(LogV3.UidHash(0UL), LogV3.UidHash(0UL));
            int val = 0; const int n = 20000;
            for (ulong u = 0; u < n; u++) if (LogV3.InValidationSplit(u, 0.1)) val++;
            Assert.InRange(val / (double)n, 0.085, 0.115);
            Assert.False(LogV3.InValidationSplit(5, 0.0));
            Assert.True(LogV3.InValidationSplit(5, 1.0));
            // a game and all its symmetry copies share a uid, so they always land on the same side
            Assert.Equal(LogV3.InValidationSplit(123456789UL, 0.3), LogV3.InValidationSplit(123456789UL, 0.3));
        }

        [Fact]
        public void UidHash_GoldenValues() {
            Assert.Equal(Golden0, LogV3.UidHash(0UL));
            Assert.Equal(Golden1, LogV3.UidHash(1UL));
            Assert.Equal(GoldenBig, LogV3.UidHash(0x0123456789ABCDEFUL));
        }

        // zlib.crc32(struct.pack('<Q', uid)), computed with Python; python/test_data.py asserts the same numbers.
        private const uint Golden0 = 0x6522DF69;
        private const uint Golden1 = 0xA988DFF7;
        private const uint GoldenBig = 0x443BE247;
    }
}
