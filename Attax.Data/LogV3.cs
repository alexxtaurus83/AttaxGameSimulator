using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Attax.Core;
using Attax.Core.Utils;
using Attax.Model;
using K4os.Compression.LZ4;

namespace Attax.Data {
    /// <summary>
    /// Training log v4 ("ATLG" + ushort 4; the class keeps its v3 name). Version 4 added the teacher SCORE to every sample (46 bytes).
    /// One file = header, then records:
    ///   1 GameHeader    [gameUid:8][seed:8][boardSize:1][generation:2][redMode:1][blueMode:1]
    ///   2 SampleBlock   [count:4][uncompressedSize:4][compressedSize:4][crc32:4][LZ4 payload of 46-byte samples]
    ///   3 GameResult    [gameUid:8][resultRedPov:1 sbyte][totalPlies:2][termination:1]
    ///   4 TeacherConfig [id:1][kind:1][depth:1][nodeBudget:4][rootBonusScale:4 float][flags:1][modelHash:8]
    /// A game is always written contiguously (header, sample blocks, result). TeacherConfig records may appear
    /// anywhere between games and must precede the first sample that references them.
    ///
    /// Sample = the position BEFORE the move, the action actually played, and an optional teacher action and score.
    /// The played action is a rollout artefact (possibly random/exploratory). The teacher action is a separate label
    /// and is only trusted when the TeacherValid flag is set. The game outcome belongs to the played trajectory only.
    /// TeacherScore is the teacher search's value of the position for the side to move, in engine units (heuristic points x 10,000),
    /// without the root bonus; decisive terminal results are around +/-1e9. It is only meaningful with FlagTeacherScoreValid (which
    /// requires TeacherValid) and is the same for every symmetry copy of a position.
    /// GameUid is the stable identity across files, generations, relabeled copies and symmetry copies; the train/val
    /// split must be derived from it.
    /// </summary>
    public static class LogV3 {
        public const uint Magic = 0x474C5441; // "ATLG"
        public const ushort Version = 4;
        public const byte BoardSize = 7;
        public const int SampleSize = 46;

        public const byte RecGameHeader = 1, RecSampleBlock = 2, RecGameResult = 3, RecTeacherConfig = 4;

        public const byte FlagTeacherValid = 1;   // TeacherAction is a trusted teacher label
        public const byte FlagPlayedRandom = 2;   // the played move was an exploration (uniform random) move
        public const byte FlagTeacherScoreValid = 4;   // TeacherScore holds the teacher search value (requires FlagTeacherValid)
        public const byte KnownFlags = FlagTeacherValid | FlagPlayedRandom | FlagTeacherScoreValid;
        public const byte TeacherFlagNormalMode = 1, TeacherFlagQuiescence = 2, TeacherFlagRandomTies = 4;

        public const short NoAction = -1;
        public const byte NoTeacher = 0;

        public enum Termination : byte {
            /// <summary>Natural end: GetTerminalResult decided the game. Outcome is reliable.</summary>
            Terminal = 0,
            /// <summary>Ply cap reached. Result is a piece-count fallback and is NOT a true game result.</summary>
            PlyCap = 1,
        }

        public enum PlayerMode : byte { Classic = 0, Value = 1, Policy = 2, Random = 3 }
        public enum TeacherKind : byte { HeuristicSearch = 0, ModelValueSearch = 1 }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct Sample {
            public ulong GameUid;
            public ushort Ply;
            public ulong Red, Blue, Blocked;
            public byte Side;            // 0 = red to move, 1 = blue to move
            public short PlayedAction;   // ActionCodec index, always legal
            public short TeacherAction;  // ActionCodec index, or -1
            public byte Flags;
            public byte Symmetry;        // symmetry already applied to board and both actions (0 = identity)
            public byte TeacherId;       // 0 = none, else TeacherConfig id
            public int TeacherScore;     // engine units, see the class comment; 0 when FlagTeacherScoreValid is clear

            public bool TeacherScoreValid => (Flags & FlagTeacherScoreValid) != 0;
            public bool TeacherValid => (Flags & FlagTeacherValid) != 0;
            public bool PlayedRandom => (Flags & FlagPlayedRandom) != 0;
        }

        public struct GameHeader {
            public ulong GameUid;
            public ulong Seed;
            public byte Board;
            public ushort Generation;
            public PlayerMode RedMode, BlueMode;
        }

        public struct GameResult {
            public ulong GameUid;
            public sbyte ResultRedPov;
            public ushort TotalPlies;
            public Termination Termination;
        }

        public struct TeacherConfig {
            public byte Id;
            public TeacherKind Kind;
            public byte Depth;
            public int NodeBudget;       // 0 = unlimited
            public float RootBonusScale;
            /// <summary>Bit 0 (TeacherFlagNormalMode): the teacher searched in normal mode (null-move pruning on), not training mode.
            /// Bit 1 (TeacherFlagQuiescence): capture-only quiescence was on. Bit 2 (TeacherFlagRandomTies): the teacher broke ties between equally scored
            /// root moves at random (0 = it took the first, so a re-search reproduces the label). 0 = training mode, first-move ties.</summary>
            public byte Flags;
            /// <summary>First 8 bytes (little-endian) of the SHA-256 of the teacher model file, or, for a heuristic teacher, of its
            /// engine-parameter JSON; 0 = heuristic with the built-in default parameters.</summary>
            public ulong ModelHash;
        }

        public sealed class Game {
            public GameHeader Header;
            public Sample[] Samples;
            public GameResult Result;
        }

        public static BitboardState ToBoard(in Sample s) => new BitboardState { RedPieces = s.Red, BluePieces = s.Blue, BlockedSquares = s.Blocked };
        public static PlayerColor SideOf(in Sample s) => s.Side == 0 ? PlayerColor.Red : PlayerColor.Blue;

        private static uint[] crcTable;
        public static uint Crc32(byte[] bytes, int offset, int count) {
            if (crcTable == null) {
                var t = new uint[256];
                for (uint i = 0; i < 256; i++) {
                    uint c = i;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
                    t[i] = c;
                }
                crcTable = t;
            }
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < count; i++) crc = crcTable[(crc ^ bytes[offset + i]) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }

        /// <summary>Stable 32-bit hash of a game uid; identical to zlib.crc32(struct.pack('&lt;Q', uid)) used by the trainer.</summary>
        public static uint UidHash(ulong uid) {
            var b = BitConverter.GetBytes(uid);
            if (!BitConverter.IsLittleEndian) Array.Reverse(b);
            return Crc32(b, 0, 8);
        }

        public static bool InValidationSplit(ulong uid, double valFraction) {
            if (valFraction <= 0) return false;
            if (valFraction >= 1) return true;
            return UidHash(uid) / 4294967296.0 < valFraction;
        }
    }

    /// <summary>Writes whole games atomically (under a lock) so games are contiguous even with parallel producers.</summary>
    public sealed class LogV3Writer : IDisposable {
        private readonly object gate = new object();
        private readonly FileStream stream;
        private readonly BinaryWriter writer;
        private readonly HashSet<byte> declaredTeachers = new HashSet<byte>();
        private bool disposed;

        public long Games { get; private set; }
        public long Samples { get; private set; }

        public LogV3Writer(string path) {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            writer = new BinaryWriter(stream);
            writer.Write(LogV3.Magic);
            writer.Write(LogV3.Version);
            writer.Flush();
        }

        public void DeclareTeacher(LogV3.TeacherConfig cfg) {
            if (cfg.Id == LogV3.NoTeacher) throw new ArgumentException("Teacher id 0 is reserved for 'no teacher'.");
            lock (gate) {
                if (!declaredTeachers.Add(cfg.Id)) throw new InvalidOperationException($"Teacher id {cfg.Id} declared twice.");
                WriteTeacher(cfg);
            }
        }

        /// <summary>Declares the teacher unless that id is already declared. Always lands between games.</summary>
        public void DeclareTeacherIfMissing(LogV3.TeacherConfig cfg) {
            if (cfg.Id == LogV3.NoTeacher) throw new ArgumentException("Teacher id 0 is reserved for 'no teacher'.");
            lock (gate) {
                if (declaredTeachers.Add(cfg.Id)) WriteTeacher(cfg);
            }
        }

        private void WriteTeacher(LogV3.TeacherConfig cfg) {
            writer.Write(LogV3.RecTeacherConfig);
            writer.Write(cfg.Id);
            writer.Write((byte)cfg.Kind);
            writer.Write(cfg.Depth);
            writer.Write(cfg.NodeBudget);
            writer.Write(cfg.RootBonusScale);
            writer.Write(cfg.Flags);
            writer.Write(cfg.ModelHash);
            writer.Flush();
        }

        public void WriteGame(LogV3.GameHeader header, IReadOnlyList<LogV3.Sample> samples, LogV3.GameResult result, int samplesPerBlock = 4096) {
            if (header.GameUid != result.GameUid) throw new ArgumentException("Header and result game uid differ.");
            if (header.Board != LogV3.BoardSize) throw new ArgumentException("Only 7x7 boards are supported.");
            foreach (var s in samples) {
                if (s.GameUid != header.GameUid) throw new ArgumentException("Sample game uid differs from the game header.");
                if (s.TeacherId != LogV3.NoTeacher && !declaredTeachers.Contains(s.TeacherId))
                    throw new InvalidOperationException($"Sample references undeclared teacher {s.TeacherId}.");
            }

            lock (gate) {
                if (disposed) throw new ObjectDisposedException(nameof(LogV3Writer));
                writer.Write(LogV3.RecGameHeader);
                writer.Write(header.GameUid);
                writer.Write(header.Seed);
                writer.Write(header.Board);
                writer.Write(header.Generation);
                writer.Write((byte)header.RedMode);
                writer.Write((byte)header.BlueMode);

                for (int start = 0; start < samples.Count; start += samplesPerBlock) {
                    int n = Math.Min(samplesPerBlock, samples.Count - start);
                    var block = new LogV3.Sample[n];
                    for (int i = 0; i < n; i++) block[i] = samples[start + i];
                    WriteBlock(block);
                }

                writer.Write(LogV3.RecGameResult);
                writer.Write(result.GameUid);
                writer.Write(result.ResultRedPov);
                writer.Write(result.TotalPlies);
                writer.Write((byte)result.Termination);
                writer.Flush();
                Games++;
                Samples += samples.Count;
            }
        }

        private void WriteBlock(LogV3.Sample[] block) {
            int raw = block.Length * LogV3.SampleSize;
            var bytes = new byte[raw];
            MemoryMarshal.AsBytes(new ReadOnlySpan<LogV3.Sample>(block)).CopyTo(bytes);
            var comp = new byte[LZ4Codec.MaximumOutputSize(raw)];
            int compSize = LZ4Codec.Encode(bytes, 0, raw, comp, 0, comp.Length);
            if (compSize <= 0) throw new InvalidDataException("LZ4 compression failed.");
            writer.Write(LogV3.RecSampleBlock);
            writer.Write(block.Length);
            writer.Write(raw);
            writer.Write(compSize);
            writer.Write(LogV3.Crc32(comp, 0, compSize));
            writer.Write(comp, 0, compSize);
        }

        public void Dispose() {
            lock (gate) {
                if (disposed) return;
                disposed = true;
                writer.Flush();
                writer.Dispose();
                stream.Dispose();
            }
        }
    }

    /// <summary>Streaming reader with full structural and semantic validation. Any problem throws InvalidDataException.</summary>
    public sealed class LogV3Reader {
        public readonly Dictionary<byte, LogV3.TeacherConfig> Teachers = new Dictionary<byte, LogV3.TeacherConfig>();

        public IEnumerable<LogV3.Game> ReadGames(string path, bool semanticChecks = true) {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 6) throw new InvalidDataException("Log is too small to contain a header.");
            if (reader.ReadUInt32() != LogV3.Magic) throw new InvalidDataException("Missing 'ATLG' header.");
            ushort version = reader.ReadUInt16();
            if (version != LogV3.Version) throw new InvalidDataException($"Unsupported log version {version}; this reader only supports {LogV3.Version}.");

            LogV3.Game current = null;
            var samples = new List<LogV3.Sample>();
            var mask = semanticChecks ? new bool[ActionCodec.ActionCount] : null;

            while (stream.Position < stream.Length) {
                byte rec = reader.ReadByte();
                switch (rec) {
                    case LogV3.RecTeacherConfig: {
                        if (current != null) throw new InvalidDataException("TeacherConfig inside a game.");
                        var t = new LogV3.TeacherConfig {
                            Id = reader.ReadByte(), Kind = (LogV3.TeacherKind)reader.ReadByte(), Depth = reader.ReadByte(),
                            NodeBudget = reader.ReadInt32(), RootBonusScale = reader.ReadSingle(), Flags = reader.ReadByte(),
                            ModelHash = reader.ReadUInt64()
                        };
                        if (t.Id == LogV3.NoTeacher) throw new InvalidDataException("TeacherConfig id 0 is reserved.");
                        if (Teachers.ContainsKey(t.Id)) throw new InvalidDataException($"Teacher {t.Id} declared twice.");
                        Teachers[t.Id] = t;
                        break;
                    }
                    case LogV3.RecGameHeader: {
                        if (current != null) throw new InvalidDataException("GameHeader before the previous game's GameResult.");
                        current = new LogV3.Game {
                            Header = new LogV3.GameHeader {
                                GameUid = reader.ReadUInt64(), Seed = reader.ReadUInt64(), Board = reader.ReadByte(),
                                Generation = reader.ReadUInt16(), RedMode = (LogV3.PlayerMode)reader.ReadByte(), BlueMode = (LogV3.PlayerMode)reader.ReadByte()
                            }
                        };
                        if (current.Header.Board != LogV3.BoardSize) throw new InvalidDataException($"Board size {current.Header.Board} unsupported.");
                        samples.Clear();
                        break;
                    }
                    case LogV3.RecSampleBlock: {
                        if (current == null) throw new InvalidDataException("SampleBlock outside a game.");
                        int count = reader.ReadInt32(), raw = reader.ReadInt32(), compSize = reader.ReadInt32();
                        uint crc = reader.ReadUInt32();
                        if (count < 0 || raw != count * LogV3.SampleSize || compSize < 0 || compSize > stream.Length - stream.Position)
                            throw new InvalidDataException("Corrupt SampleBlock header.");
                        var comp = reader.ReadBytes(compSize);
                        if (comp.Length != compSize) throw new EndOfStreamException("Truncated SampleBlock payload.");
                        if (LogV3.Crc32(comp, 0, comp.Length) != crc) throw new InvalidDataException("SampleBlock CRC32 mismatch.");
                        var bytes = new byte[raw];
                        if (LZ4Codec.Decode(comp, 0, comp.Length, bytes, 0, raw) != raw) throw new InvalidDataException("SampleBlock decompressed to the wrong size.");
                        var block = new LogV3.Sample[count];
                        MemoryMarshal.Cast<byte, LogV3.Sample>(new ReadOnlySpan<byte>(bytes)).CopyTo(block);
                        samples.AddRange(block);
                        break;
                    }
                    case LogV3.RecGameResult: {
                        if (current == null) throw new InvalidDataException("GameResult outside a game.");
                        current.Result = new LogV3.GameResult {
                            GameUid = reader.ReadUInt64(), ResultRedPov = reader.ReadSByte(), TotalPlies = reader.ReadUInt16(),
                            Termination = (LogV3.Termination)reader.ReadByte()
                        };
                        current.Samples = samples.ToArray();
                        if (semanticChecks) Validate(current, mask);
                        var done = current;
                        current = null;
                        yield return done;
                        break;
                    }
                    default:
                        throw new InvalidDataException($"Unknown record type {rec}.");
                }
            }
            if (current != null) throw new EndOfStreamException("File ends inside a game (missing GameResult).");
        }

        private void Validate(LogV3.Game g, bool[] mask) {
            ulong uid = g.Header.GameUid;
            if (g.Result.GameUid != uid) throw new InvalidDataException($"Game {uid}: result uid {g.Result.GameUid} differs.");
            if (g.Result.ResultRedPov < -1 || g.Result.ResultRedPov > 1) throw new InvalidDataException($"Game {uid}: result out of range.");
            if (g.Result.Termination != LogV3.Termination.Terminal && g.Result.Termination != LogV3.Termination.PlyCap)
                throw new InvalidDataException($"Game {uid}: unknown termination {(int)g.Result.Termination}.");

            const ulong boardMask = (1UL << 49) - 1;
            for (int i = 0; i < g.Samples.Length; i++) {
                var s = g.Samples[i];
                string where = $"Game {uid} sample {i}";
                if (s.GameUid != uid) throw new InvalidDataException($"{where}: uid differs from game header.");
                if (s.Side > 1) throw new InvalidDataException($"{where}: side {s.Side}.");
                if (((s.Red | s.Blue | s.Blocked) & ~boardMask) != 0) throw new InvalidDataException($"{where}: bits outside the board.");
                if ((s.Red & s.Blue) != 0 || (s.Red & s.Blocked) != 0 || (s.Blue & s.Blocked) != 0) throw new InvalidDataException($"{where}: overlapping bitboards.");
                if (s.Symmetry > 7) throw new InvalidDataException($"{where}: symmetry {s.Symmetry}.");
                if (s.Ply >= g.Result.TotalPlies && g.Result.TotalPlies > 0) throw new InvalidDataException($"{where}: ply {s.Ply} >= totalPlies {g.Result.TotalPlies}.");
                if ((s.Flags & ~LogV3.KnownFlags) != 0) throw new InvalidDataException($"{where}: unknown flags {s.Flags}.");
                if (s.TeacherScoreValid && !s.TeacherValid) throw new InvalidDataException($"{where}: teacher score flag set without a valid teacher label.");
                if (!s.TeacherScoreValid && s.TeacherScore != 0) throw new InvalidDataException($"{where}: teacher score set but TeacherScoreValid is clear.");

                var board = LogV3.ToBoard(s);
                var side = LogV3.SideOf(s);
                ActionCodec.FillLegalMask(board, side, mask);

                if (s.PlayedAction < 0 || s.PlayedAction >= ActionCodec.ActionCount || !mask[s.PlayedAction])
                    throw new InvalidDataException($"{where}: played action {s.PlayedAction} is not legal.");

                if (s.TeacherValid) {
                    if (s.TeacherId == LogV3.NoTeacher || !Teachers.ContainsKey(s.TeacherId))
                        throw new InvalidDataException($"{where}: valid teacher label without a declared teacher config.");
                    if (s.TeacherAction < 0 || s.TeacherAction >= ActionCodec.ActionCount || !mask[s.TeacherAction])
                        throw new InvalidDataException($"{where}: teacher action {s.TeacherAction} is not legal.");
                } else {
                    if (s.TeacherAction != LogV3.NoAction) throw new InvalidDataException($"{where}: teacher action set but TeacherValid is clear.");
                    if (s.TeacherId != LogV3.NoTeacher) throw new InvalidDataException($"{where}: teacher id set but TeacherValid is clear.");
                }
            }
        }
    }
}
