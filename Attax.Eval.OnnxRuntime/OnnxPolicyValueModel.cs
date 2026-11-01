using System;
using System.Collections.Generic;
using System.Linq;
using Attax.Core;
using Attax.Model;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Eval.OnnxRuntime {
    /// <summary>
    /// ONNX implementation of the two-headed model contract (see ataxx_common.py, contract "attax-pv-1").
    /// The contract is checked when the model is loaded: exact input/output names, dtypes, shapes and the metadata
    /// stamp written by the trainer. A model that does not match (for example an old single-output value model)
    /// throws immediately instead of being driven with guessed semantics.
    /// Any inference failure throws; nothing is swallowed and no zeros are ever returned.
    /// </summary>
    public sealed class OnnxPolicyValueModel : IPolicyValueModel, IDisposable {
        public const string ContractId = "attax-pv-1";
        public const string InputName = "board";
        public const string PolicyName = "policy_logits";
        public const string ValueName = "value";

        private readonly InferenceSession session;
        // One InferenceSession.Run is thread-safe, so a single model instance can serve all game threads.

        public string Path { get; }
        public IReadOnlyDictionary<string, string> Metadata { get; }

        public static string GetRuntimeDiagnostics() {
            try {
                string ortVersion = typeof(InferenceSession).Assembly.GetName().Version?.ToString() ?? "unknown";
                return $"[onnx] ONNX Runtime v{ortVersion}, CPU execution provider";
            } catch (Exception ex) {
                return $"[onnx] diagnostics unavailable: {ex.Message}";
            }
        }

        /// <summary>
        /// CPU only, on purpose. These models are tiny (7x7 board, 64x5 to 128x8 convolutions) and every call carries one
        /// position, so a GPU's fixed launch/copy cost (about 1.4 ms) dwarfs the 0.35 ms a CPU core needs; measured 6-30x
        /// slower on an RTX 2080 Ti in self-play and arena. A GPU only pays off with large batches, which this code never makes.
        /// </summary>
        /// <param name="intraOpNumThreads">0 leaves the runtime default; 1 pins a single inference to one thread, which is what
        /// you want when many games run in parallel and own the cores.</param>
        public OnnxPolicyValueModel(string modelPath, int intraOpNumThreads = 0) {
            if (string.IsNullOrWhiteSpace(modelPath)) throw new ArgumentException("Model path is empty.", nameof(modelPath));
            if (!System.IO.File.Exists(modelPath)) throw new System.IO.FileNotFoundException($"Model file not found: {modelPath}", modelPath);
            Path = modelPath;

            using var options = new SessionOptions();
            if (intraOpNumThreads > 0) {
                options.IntraOpNumThreads = intraOpNumThreads;
                options.InterOpNumThreads = 1;
                options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            }
            try {
                session = new InferenceSession(modelPath, options);
                Metadata = session.ModelMetadata.CustomMetadataMap;
                ValidateContract(session, Metadata, modelPath);
            } catch {
                session?.Dispose();
                throw;
            }
        }

        private static void ValidateContract(InferenceSession s, IReadOnlyDictionary<string, string> meta, string path) {
            string Fail(string what) => $"Model '{path}' does not satisfy contract {ContractId}: {what}";

            meta.TryGetValue("contract", out string contract);
            if (contract != ContractId)
                throw new InvalidOperationException(Fail($"metadata 'contract' is '{contract ?? "<missing>"}'. Old value-only models are not supported; retrain with train.py."));

            var inputs = s.InputMetadata;
            if (inputs.Count != 1 || !inputs.ContainsKey(InputName))
                throw new InvalidOperationException(Fail($"inputs are [{string.Join(", ", inputs.Keys)}], expected exactly [{InputName}]."));
            var input = inputs[InputName];
            if (input.ElementType != typeof(float) || input.Dimensions.Length != 4 ||
                input.Dimensions[1] != BoardEncoder.Channels || input.Dimensions[2] != 7 || input.Dimensions[3] != 7)
                throw new InvalidOperationException(Fail($"input '{InputName}' must be float [N,{BoardEncoder.Channels},7,7], found {input.ElementType.Name} [{string.Join(",", input.Dimensions)}]."));

            var outputs = s.OutputMetadata;
            if (outputs.Count != 2 || !outputs.ContainsKey(PolicyName) || !outputs.ContainsKey(ValueName))
                throw new InvalidOperationException(Fail($"outputs are [{string.Join(", ", outputs.Keys)}], expected exactly [{PolicyName}, {ValueName}]."));
            var pol = outputs[PolicyName];
            var val = outputs[ValueName];
            if (pol.ElementType != typeof(float) || pol.Dimensions.Length != 2 || pol.Dimensions[1] != ActionCodec.ActionCount)
                throw new InvalidOperationException(Fail($"output '{PolicyName}' must be float [N,{ActionCodec.ActionCount}], found {pol.ElementType.Name} [{string.Join(",", pol.Dimensions)}]."));
            if (val.ElementType != typeof(float) || val.Dimensions.Length != 2 || val.Dimensions[1] != 1)
                throw new InvalidOperationException(Fail($"output '{ValueName}' must be float [N,1], found {val.ElementType.Name} [{string.Join(",", val.Dimensions)}]."));

            if (!meta.TryGetValue("action_count", out string ac) || ac != ActionCodec.ActionCount.ToString())
                throw new InvalidOperationException(Fail($"metadata 'action_count' is '{ac ?? "<missing>"}', expected {ActionCodec.ActionCount}."));
        }

        public PolicyValueOutput Evaluate(IReadOnlyList<BitboardState> boards, PlayerColor sideToMove) {
            if (boards == null) throw new ArgumentNullException(nameof(boards));
            int n = boards.Count;
            if (n == 0) return new PolicyValueOutput(0, Array.Empty<float>(), Array.Empty<float>());

            float[] input = BoardEncoder.EncodeBatch(boards, sideToMove);
            var tensor = new DenseTensor<float>(input, new[] { n, BoardEncoder.Channels, 7, 7 });
            using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(InputName, tensor) });

            float[] logits = null, values = null;
            foreach (var r in results) {
                if (r.Name == PolicyName) logits = r.AsTensor<float>().ToArray();
                else if (r.Name == ValueName) values = r.AsTensor<float>().ToArray();
            }
            if (logits == null || values == null)
                throw new InvalidOperationException("Model run did not return both required outputs.");

            var output = new PolicyValueOutput(n, values, logits); // checks lengths
            output.ThrowIfInvalid();
            return output;
        }

        public void Dispose() => session?.Dispose();
    }
}
