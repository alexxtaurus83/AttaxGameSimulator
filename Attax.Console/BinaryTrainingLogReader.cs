using System;
using System.Collections.Generic;
using System.IO;

namespace Attax.Console {
    public sealed class BinaryTrainingLogReader {
        private const uint FileMagic = 0x474C5441; // "ATLG"
        private const ushort CurrentFormatVersion = 1;

        private readonly bool _strictGameCompleteness;

        public BinaryTrainingLogReader(bool strictGameCompleteness = false) {
            _strictGameCompleteness = strictGameCompleteness;
        }

        public enum RecordType : byte {
            GameHeader = 1,
            CompressedSampleBlock = 2,
            GameResult = 3
        }

        public readonly struct ValidationRecord {
            public ValidationRecord(RecordType recordType, int index, bool checksumValid) {
                RecordType = recordType;
                Index = index;
                ChecksumValid = checksumValid;
            }

            public RecordType RecordType { get; }
            public int Index { get; }
            public bool ChecksumValid { get; }
        }

        public IEnumerable<ValidationRecord> ReadAndValidate(string path) {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream);

            DetectFormat(reader);
            int index = 0;

            bool inGame = false;
            int sampleBlockCountInGame = 0;

            while (stream.Position < stream.Length) {
                byte recordTypeRaw = reader.ReadByte();
                var recordType = (RecordType)recordTypeRaw;

                switch (recordType) {
                    case RecordType.GameHeader:
                        if (_strictGameCompleteness && inGame) {
                            throw new InvalidDataException("Encountered a new GameHeader before reading GameResult for the previous game.");
                        }

                        reader.ReadUInt32(); // gameId
                        reader.ReadUInt64(); // seed
                        reader.ReadByte();   // ruleFlags
                        reader.ReadByte();   // boardSize

                        if (_strictGameCompleteness) {
                            inGame = true;
                            sampleBlockCountInGame = 0;
                        }

                        yield return new ValidationRecord(recordType, index++, checksumValid: true);
                        break;

                    case RecordType.CompressedSampleBlock: {
                            if (_strictGameCompleteness && !inGame) {
                                throw new InvalidDataException("Encountered CompressedSampleBlock without a preceding GameHeader.");
                            }

                            reader.ReadInt32(); // sampleCount
                            int uncompressedSize = reader.ReadInt32();
                            int compressedSize = reader.ReadInt32();

                            if (uncompressedSize < 0 || compressedSize < 0) {
                                throw new InvalidDataException("Encountered negative block size.");
                            }

                            uint expectedChecksum = reader.ReadUInt32();
                            byte[] compressedPayload = reader.ReadBytes(compressedSize);
                            if (compressedPayload.Length != compressedSize) {
                                throw new EndOfStreamException("Unexpected EOF while reading compressed payload.");
                            }

                            uint actualChecksum = ComputeCrc32(compressedPayload, compressedPayload.Length);
                            bool ok = actualChecksum == expectedChecksum;
                            if (!ok) {
                                throw new InvalidDataException(
                                    $"CompressedSampleBlock checksum mismatch. Expected 0x{expectedChecksum:X8}, got 0x{actualChecksum:X8}.");
                            }

                            if (_strictGameCompleteness) {
                                sampleBlockCountInGame++;
                            }

                            yield return new ValidationRecord(recordType, index++, checksumValid: true);
                            break;
                        }

                    case RecordType.GameResult:
                        if (_strictGameCompleteness && !inGame) {
                            throw new InvalidDataException("Encountered GameResult without a preceding GameHeader.");
                        }

                        if (_strictGameCompleteness && sampleBlockCountInGame < 1) {
                            throw new InvalidDataException("Game completeness violation: each game must contain at least one CompressedSampleBlock before GameResult.");
                        }

                        reader.ReadUInt32(); // gameId
                        reader.ReadSByte();  // resultFromRedPov
                        reader.ReadUInt16(); // totalPlies

                        if (_strictGameCompleteness) {
                            inGame = false;
                            sampleBlockCountInGame = 0;
                        }

                        yield return new ValidationRecord(recordType, index++, checksumValid: true);
                        break;

                    default:
                        throw new InvalidDataException($"Unknown record type: {recordTypeRaw}");
                }
            }

            if (_strictGameCompleteness && inGame) {
                throw new EndOfStreamException("Unexpected EOF while reading game: missing GameResult.");
            }
        }

        private static void DetectFormat(BinaryReader reader) {
            if (reader.BaseStream.Length < sizeof(byte)) {
                throw new InvalidDataException("Training log is empty.");
            }

            if (reader.BaseStream.Length >= sizeof(uint) + sizeof(ushort)) {
                long start = reader.BaseStream.Position;
                uint maybeMagic = reader.ReadUInt32();
                ushort maybeVersion = reader.ReadUInt16();

                if (maybeMagic == FileMagic) {
                    if (maybeVersion != CurrentFormatVersion) {
                        throw new InvalidDataException(
                            $"Unsupported file format version {maybeVersion}. Expected {CurrentFormatVersion}.");
                    }

                    return;
                }

                throw new InvalidDataException(
                    "Training log is missing the 'ATLG' file header. Legacy v0 format is no longer supported.");
            }

            throw new InvalidDataException("Training log is too small to contain a valid header.");
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
    }
}
