using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Attax.Core;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Model {
    /// <summary>
    /// The one model contract: a two-headed network that maps a position (seen from the side to move) to
    ///   value  in [-1, 1]  expected outcome for the side to move (+1 win, 0 draw, -1 loss)
    ///   logits [ActionCodec.ActionCount] unmasked policy logits over the ActionCodec action space.
    /// Implementations must throw on any failure. Returning zeros or partial data would silently corrupt
    /// generated training data, so there is no "best effort" path.
    /// </summary>
    public interface IPolicyValueModel {
        PolicyValueOutput Evaluate(IReadOnlyList<BitboardState> boards, PlayerColor sideToMove);
    }

    public sealed class PolicyValueOutput {
        public readonly int Count;
        /// <summary>Count values, side-to-move perspective.</summary>
        public readonly float[] Values;
        /// <summary>Count * ActionCodec.ActionCount logits, row-major (board i starts at i * ActionCount).</summary>
        public readonly float[] Logits;

        public PolicyValueOutput(int count, float[] values, float[] logits) {
            if (values == null || values.Length != count)
                throw new InvalidOperationException($"Model returned {values?.Length ?? -1} values for {count} boards.");
            if (logits == null || logits.Length != count * ActionCodec.ActionCount)
                throw new InvalidOperationException($"Model returned {logits?.Length ?? -1} logits, expected {count * ActionCodec.ActionCount}.");
            Count = count;
            Values = values;
            Logits = logits;
        }

        /// <summary>Throws if any value or logit is not finite, or a value is outside [-1, 1] (small float slack).</summary>
        public void ThrowIfInvalid() {
            for (int i = 0; i < Values.Length; i++) {
                float v = Values[i];
                if (float.IsNaN(v) || float.IsInfinity(v) || v < -1.0001f || v > 1.0001f)
                    throw new InvalidOperationException($"Model value output is invalid: {v} (board {i}). Expected a finite value in [-1, 1].");
            }
            for (int i = 0; i < Logits.Length; i++) {
                float l = Logits[i];
                if (float.IsNaN(l) || float.IsInfinity(l))
                    throw new InvalidOperationException($"Model policy output is not finite at flat index {i} (board {i / ActionCodec.ActionCount}, action {i % ActionCodec.ActionCount}).");
            }
        }
    }

    /// <summary>Thread-safe inference counters around any model, for timing reports.</summary>
    public sealed class MeteredPolicyValueModel : IPolicyValueModel, IDisposable {
        private readonly IPolicyValueModel inner;

        public void Dispose() => (inner as IDisposable)?.Dispose();
        private long calls, positions, ticks;

        public MeteredPolicyValueModel(IPolicyValueModel inner) {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public long Calls => Interlocked.Read(ref calls);
        public long Positions => Interlocked.Read(ref positions);
        public double TotalMilliseconds => Interlocked.Read(ref ticks) * 1000.0 / Stopwatch.Frequency;
        public double MeanMillisecondsPerCall => Calls == 0 ? 0.0 : TotalMilliseconds / Calls;

        public PolicyValueOutput Evaluate(IReadOnlyList<BitboardState> boards, PlayerColor sideToMove) {
            long t0 = Stopwatch.GetTimestamp();
            var result = inner.Evaluate(boards, sideToMove);
            Interlocked.Add(ref ticks, Stopwatch.GetTimestamp() - t0);
            Interlocked.Increment(ref calls);
            Interlocked.Add(ref positions, boards.Count);
            return result;
        }
    }
}
