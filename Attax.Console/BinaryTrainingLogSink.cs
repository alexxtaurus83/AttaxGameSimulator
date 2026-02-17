using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Attax.Core;
using K4os.Compression.LZ4;

namespace Attax.Console {
    public class BinaryTrainingLogSink : ILogSink, IDisposable {
        private const uint FileMagic = 0x474C5441; // "ATLG"
        private const ushort FormatVersion = 1;

        private readonly string _outputPath;
        private readonly int _bufferSizeSamples;
        private readonly List<PositionSample> _sampleBuffer;
        private readonly object _lock = new object();
        private FileStream _fileStream;
        private BinaryWriter _writer;

        // Current game context
        private uint _currentGameId;
        private ulong _currentSeed;
        private byte _currentRuleFlags;
        private byte _currentBoardSize;
        private bool _gameStarted;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct GameHeader {
            public uint GameId;
            public ulong Seed;
            public byte RuleFlags;
            public byte BoardSize;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct PositionSample {
            public uint GameId;
            public ushort Ply;
            public ulong Red;
            public ulong Blue;
            public ulong Blocked;
            public byte SideToMove; // 0=Red, 1=Blue
            public byte RuleFlags;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct GameResult {
            public uint GameId;
            public sbyte ResultFromRedPOV; // -1/0/+1
            public ushort TotalPlies;
        }

        public BinaryTrainingLogSink(string outputPath, int bufferSizeSamples = 65536) {
            _outputPath = outputPath;
            _bufferSizeSamples = bufferSizeSamples;
            _sampleBuffer = new List<PositionSample>(bufferSizeSamples);

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) {
                Directory.CreateDirectory(dir);
            }

            _fileStream = new FileStream(outputPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            _writer = new BinaryWriter(_fileStream);

            InitializeOrValidateFileHeader();
            _fileStream.Seek(0, SeekOrigin.End);
        }

        public void LogInformation(string message) {
        }

        public void LogError(string message) {
            System.Console.Error.WriteLine($"[BinaryLogSink Error] {message}");
        }

        public void LogGameStart(uint gameId, ulong seed, byte ruleFlags, byte boardSize) {
            lock (_lock) {
                _currentGameId = gameId;
                _currentSeed = seed;
                _currentRuleFlags = ruleFlags;
                _currentBoardSize = boardSize;
                _gameStarted = true;

                WriteGameHeader(new GameHeader {
                    GameId = gameId,
                    Seed = seed,
                    RuleFlags = ruleFlags,
                    BoardSize = boardSize
                });
            }
        }

        public void LogPosition(uint gameId, ushort ply, BitboardState board, AtaxxAIEngine.PlayerColor sideToMove) {
            if (!_gameStarted) return;

            lock (_lock) {
                var sample = new PositionSample {
                    GameId = gameId,
                    Ply = ply,
                    Red = board.RedPieces,
                    Blue = board.BluePieces,
                    Blocked = board.BlockedSquares,
                    SideToMove = (byte)(sideToMove == AtaxxAIEngine.PlayerColor.Red ? 0 : 1),
                    RuleFlags = _currentRuleFlags
                };

                _sampleBuffer.Add(sample);

                if (_sampleBuffer.Count >= _bufferSizeSamples) {
                    FlushBuffer();
                }
            }
        }

        public void LogGameEnd(uint gameId, sbyte resultFromRedPOV, ushort totalPlies) {
            lock (_lock) {
                if (_sampleBuffer.Count > 0) {
                    FlushBuffer();
                }

                WriteGameResult(new GameResult {
                    GameId = gameId,
                    ResultFromRedPOV = resultFromRedPOV,
                    TotalPlies = totalPlies
                });

                _gameStarted = false;
            }
        }

        private void InitializeOrValidateFileHeader() {
            if (_fileStream.Length == 0) {
                _writer.Write(FileMagic);
                _writer.Write(FormatVersion);
                _writer.Flush();
                return;
            }

            if (_fileStream.Length < sizeof(uint) + sizeof(ushort)) {
                throw new InvalidDataException($"Existing log '{_outputPath}' is too small to contain a valid header.");
            }

            _fileStream.Seek(0, SeekOrigin.Begin);
            var reader = new BinaryReader(_fileStream, System.Text.Encoding.UTF8, leaveOpen: true);
            uint magic = reader.ReadUInt32();
            ushort version = reader.ReadUInt16();

            if (magic != FileMagic) {
                throw new InvalidDataException(
                    $"Existing log '{_outputPath}' is in legacy format without integrity header. Use a new output file path.");
            }

            if (version != FormatVersion) {
                throw new InvalidDataException(
                    $"Existing log '{_outputPath}' has unsupported version {version}. Expected {FormatVersion}.");
            }
        }

        private void WriteGameHeader(GameHeader header) {
            _writer.Write((byte)1); // RecordType: GameHeader
            _writer.Write(header.GameId);
            _writer.Write(header.Seed);
            _writer.Write(header.RuleFlags);
            _writer.Write(header.BoardSize);
        }

        private void WriteGameResult(GameResult result) {
            _writer.Write((byte)3); // RecordType: GameResult
            _writer.Write(result.GameId);
            _writer.Write(result.ResultFromRedPOV);
            _writer.Write(result.TotalPlies);
        }

        private void FlushBuffer() {
            if (_sampleBuffer.Count == 0) return;

            int sampleSize = Marshal.SizeOf<PositionSample>();
            int uncompressedSize = _sampleBuffer.Count * sampleSize;
            byte[] rawBytes = new byte[uncompressedSize];

            var srcSpan = CollectionsMarshal.AsSpan(_sampleBuffer);
            var srcBytes = MemoryMarshal.AsBytes(srcSpan);
            srcBytes.CopyTo(rawBytes);

            int maxCompressedSize = LZ4Codec.MaximumOutputSize(uncompressedSize);
            byte[] compressedBytes = new byte[maxCompressedSize];
            int compressedSize = LZ4Codec.Encode(rawBytes, 0, uncompressedSize, compressedBytes, 0, maxCompressedSize);
            if (compressedSize <= 0) {
                throw new InvalidDataException("Failed to LZ4-compress sample block.");
            }

            uint checksum = ComputeCrc32(compressedBytes, compressedSize);

            _writer.Write((byte)2); // RecordType: CompressedSampleBlock
            _writer.Write(_sampleBuffer.Count); // Number of samples
            _writer.Write(uncompressedSize); // Uncompressed size in bytes
            _writer.Write(compressedSize); // Compressed size in bytes
            _writer.Write(checksum); // CRC32 over compressed payload bytes
            _writer.Write(compressedBytes, 0, compressedSize);

            _sampleBuffer.Clear();
            _writer.Flush();
        }

        private static uint ComputeCrc32(byte[] bytes, int count) {
            const uint polynomial = 0xEDB88320u;
            uint crc = 0xFFFFFFFFu;

            for (int i = 0; i < count; i++) {
                crc ^= bytes[i];
                for (int j = 0; j < 8; j++) {
                    uint mask = (uint)-(int)(crc & 1u);
                    crc = (crc >> 1) ^ (polynomial & mask);
                }
            }

            return ~crc;
        }

        public void Dispose() {
            lock (_lock) {
                if (_writer != null) {
                    FlushBuffer();
                    _writer.Close();
                    _writer = null;
                }

                if (_fileStream != null) {
                    _fileStream.Dispose();
                    _fileStream = null;
                }
            }
        }
    }
}
