using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Attax.Core;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Eval.OnnxRuntime {
    public enum OnnxRuntimeProvider {
        Cpu,
        Cuda
    }

    public class OnnxValueEvaluator : IValueEvaluator, IBatchValueEvaluator, IDisposable {
        private readonly InferenceSession _session;
        private readonly string _inputName;
        private readonly string _outputName;
        private readonly int _boardSize = 7; // Assuming 7x7 board
        private readonly int _inputChannels = 4; // As per plan: Friendly, Enemy, Blocked, Constant

        public OnnxValueEvaluator(string modelPath, OnnxRuntimeProvider provider = OnnxRuntimeProvider.Cpu) {
            try {
                using var options = new SessionOptions();

                if (provider == OnnxRuntimeProvider.Cuda) {
                    try {
                        options.AppendExecutionProvider_CUDA();
                    } catch (Exception ex) {
                        throw new InvalidOperationException(
                            "CUDA execution provider was requested via --ort cuda, but it is unavailable. " +
                            "Build/run with GPU ONNX Runtime (set UseOnnxGpu=true) and ensure CUDA provider native libraries are present.",
                            ex);
                    }
                }

                _session = new InferenceSession(modelPath, options);

                // Cache input and output names
                _inputName = _session.InputMetadata.Keys.First();
                _outputName = _session.OutputMetadata.Keys.First();
            } catch (Exception ex) {
                Console.WriteLine($"Failed to load ONNX model from {modelPath}: {ex.Message}");
                throw;
            }
        }

        public float Evaluate(BitboardState board, PlayerColor sideToMove, byte ruleFlags) {
            var values = EvaluateBatch(new[] { board }, sideToMove, ruleFlags);
            return values.Length > 0 ? values[0] : 0.0f;
        }

        public float[] EvaluateBatch(IReadOnlyList<BitboardState> boards, PlayerColor sideToMove, byte ruleFlags) {
            if (boards == null) throw new ArgumentNullException(nameof(boards));
            if (boards.Count == 0) return Array.Empty<float>();

            try {
                var inputTensor = CreateInputTensor(boards, sideToMove);

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(_inputName, inputTensor)
                };

                using (var results = _session.Run(inputs)) {
                    var output = results.First(r => r.Name == _outputName);
                    var outputTensor = output.AsTensor<float>();
                    var values = new float[boards.Count];
                    for (int i = 0; i < boards.Count; i++) {
                        values[i] = outputTensor.GetValue(i);
                    }
                    return values;
                }
            } catch (Exception ex) {
                Console.WriteLine($"Error during inference: {ex.Message}");
                return new float[boards.Count];
            }
        }

        private DenseTensor<float> CreateInputTensor(IReadOnlyList<BitboardState> boards, PlayerColor sideToMove) {
            int batchSize = boards.Count;
            var tensor = new DenseTensor<float>(new[] { batchSize, _inputChannels, _boardSize, _boardSize });

            for (int batchIndex = 0; batchIndex < batchSize; batchIndex++) {
                var board = boards[batchIndex];
                ulong friendlyPieces;
                ulong enemyPieces;

                if (sideToMove == PlayerColor.Red) {
                    friendlyPieces = board.RedPieces;
                    enemyPieces = board.BluePieces;
                } else {
                    friendlyPieces = board.BluePieces;
                    enemyPieces = board.RedPieces;
                }

                ulong blockedSquares = board.BlockedSquares;

                for (int y = 0; y < _boardSize; y++) {
                    for (int x = 0; x < _boardSize; x++) {
                        int bitIndex = y * _boardSize + x;
                        ulong mask = 1UL << bitIndex;

                        tensor[batchIndex, 0, y, x] = (friendlyPieces & mask) != 0 ? 1.0f : 0.0f;
                        tensor[batchIndex, 1, y, x] = (enemyPieces & mask) != 0 ? 1.0f : 0.0f;
                        tensor[batchIndex, 2, y, x] = (blockedSquares & mask) != 0 ? 1.0f : 0.0f;
                        tensor[batchIndex, 3, y, x] = 1.0f;
                    }
                }
            }

            return tensor;
        }

        public void Dispose() {
            _session?.Dispose();
        }
    }
}
