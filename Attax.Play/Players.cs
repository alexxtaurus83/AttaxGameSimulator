using System;
using System.Diagnostics;
using Attax.Core;
using Attax.Model;

namespace Attax.Play {
    /// <summary>Resolves model=&lt;path&gt; to a shared model. Implementations cache sessions so many games share one.</summary>
    public interface IModelSource : IModelPlayerSource {
        IPolicyValueModel Get(string path);
    }

    public static class ModelPlayerFactory {
        public static IPlayer CreateModelPlayer(IModelSource models, PlayerSpec spec, int seed, bool defaultTrain, bool collectScore) {
            if (spec == null) throw new ArgumentNullException(nameof(spec));
            switch (spec.Kind) {
                case PlayerKind.Random:
                case PlayerKind.Classic:
                    return PlayerFactory.Create(spec, seed, defaultTrain, collectScore);
                case PlayerKind.Policy:
                    return new PolicyPlayer(spec, RequireModel(spec, models), seed);
                case PlayerKind.Value: {
                    var eval = new ModelValueEvaluator(RequireModel(spec, models));
                    // Model search is always train-style (no null-move pruning / quiescence) unless asked otherwise:
                    // both assume heuristic score behaviour, not a learned value.
                    return new SearchPlayer(spec, eval, seed, spec.Train ?? true, true, spec.ResolveParams(), collectScore);
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(spec));
            }
        }

        private static IPolicyValueModel RequireModel(PlayerSpec spec, IModelSource models) {
            if (models == null) throw new InvalidOperationException($"Player '{spec}' needs a model but no model source was provided.");
            return models.Get(spec.ModelPath) ?? throw new InvalidOperationException($"Model '{spec.ModelPath}' could not be loaded.");
        }
    }

    internal sealed class PolicyPlayer : IPlayer {
        private readonly IPolicyValueModel model;
        private readonly Random rng;
        public PlayerSpec Spec { get; }
        public PolicyPlayer(PlayerSpec spec, IPolicyValueModel model, int seed) { Spec = spec; this.model = model; rng = new Random(seed); } // NOSONAR

        public MoveDecision Choose(BitboardState board, PlayerColor side) {
            long t0 = Stopwatch.GetTimestamp();
            var c = PolicyMoveSelector.Select(model, board, side, Spec.Temp, Spec.TopK, rng);
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            return new MoveDecision { HasMove = c.HasMove, Move = c.Move, Action = c.Action, Milliseconds = ms, LegalCount = c.LegalCount };
        }
        public void Dispose() { }
    }
}
