using System;
using System.Collections.Generic;
using System.Diagnostics;
using Attax.Core;
using Attax.Model;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Play {
    public struct MoveDecision {
        public bool HasMove;
        public Move Move;
        public int Action;
        public long Nodes;          // search nodes (0 for policy/random)
        public double Milliseconds;
        public bool UsedFallback;   // node budget / time ran out before any iteration finished
        public bool IsExploration;  // a uniformly random move (random player or epsilon exploration), not the player's own choice
        public int Depth;           // deepest completed iteration (search players)
        public int LegalCount;
        /// <summary>True when <see cref="Score"/> is the search value of the position (search players only).</summary>
        public bool HasScore;
        /// <summary>
        /// Search value of the position for the side to move, in engine units (heuristic points x 10,000), WITHOUT the root bonus and
        /// independent of which move was sampled: it is the exact minimax value at the completed depth (verified by TeacherScoreTests).
        /// Decisive terminal results are around +/-1e9; ordinary positions stay within about +/-1e7.
        /// </summary>
        public int Score;
    }

    public interface IPlayer : IDisposable {
        PlayerSpec Spec { get; }
        /// <summary>Picks a move for <paramref name="side"/>. HasMove is false only when the side has no legal move.</summary>
        MoveDecision Choose(BitboardState board, PlayerColor side);
    }

    /// <summary>Resolves model=&lt;path&gt; to a shared model. Implementations cache sessions so many games share one.</summary>
    public interface IModelSource {
        IPolicyValueModel Get(string path);
    }

    public static class PlayerFactory {
        /// <summary>Value units: value outputs span [-1, 1], the engine scales by 10,000.</summary>
        public const float ValueScale = 10000f;

        /// <param name="defaultTrain">Engine TrainingMode for search players that do not say train=... (self-play: true, arena: false).</param>
        /// <param name="collectScore">Search players also report the position value in <see cref="MoveDecision.Score"/> (small cost: root scores are kept).</param>
        public static IPlayer Create(PlayerSpec spec, IModelSource models, int seed, bool defaultTrain, bool collectScore = false) {
            if (spec == null) throw new ArgumentNullException(nameof(spec));
            switch (spec.Kind) {
                case PlayerKind.Random: return new RandomPlayer(spec, seed);
                case PlayerKind.Policy: return new PolicyPlayer(spec, RequireModel(spec, models), seed);
                case PlayerKind.Classic:
                    return new SearchPlayer(spec, null, seed, spec.Train ?? defaultTrain, false, spec.ResolveParams(), collectScore);
                case PlayerKind.Value: {
                    var eval = new ModelValueEvaluator(RequireModel(spec, models));
                    // Model search is always train-style (no null-move pruning / quiescence) unless asked otherwise:
                    // both assume heuristic score behaviour, not a learned value.
                    return new SearchPlayer(spec, eval, seed, spec.Train ?? true, true, spec.ResolveParams(), collectScore);
                }
                default: throw new ArgumentOutOfRangeException(nameof(spec));
            }
        }

        /// <summary>Whether a search player breaks ties between equally scored root moves at random (the engine default) or takes the first.</summary>
        public static bool EffectiveRandomTies(PlayerSpec spec) => spec.RandomTies ?? true;

        /// <summary>The engine mode a search player really runs in: training mode (no null-move, no quiescence) and whether quiescence is on.</summary>
        public static (bool train, bool quiescence) EffectiveMode(PlayerSpec spec, bool defaultTrain) {
            bool model = spec.Kind == PlayerKind.Value;
            bool train = spec.Train ?? (model ? true : defaultTrain);
            bool q = !train && (spec.Quiescence ?? !model);
            return (train, q);
        }

        /// <summary>One line describing how a player will really run (engine mode, quiescence, parameter set) so logs and arena headers are unambiguous.</summary>
        public static string Describe(PlayerSpec spec, bool defaultTrain) {
            if (!spec.IsSearch) return spec.ToString();
            var (train, q) = EffectiveMode(spec, defaultTrain);
            var resolved = spec.ResolveParams();
            string fp = EngineParamsFile.Fingerprint(resolved);
            string p = fp + (fp == EngineParamsFile.Fingerprint(EngineParamsFile.LoadBase()) ? " (= engine-params.json)" : "");
            return $"{spec} => engine mode={(train ? "training (no null-move, no quiescence)" : "normal (null-move pruning on)")}, quiescence={(train ? "n/a" : q ? "on" : "off")}, root ties={(EffectiveRandomTies(spec) ? "random" : "first")}, params={p}";
        }

        private static IPolicyValueModel RequireModel(PlayerSpec spec, IModelSource models) {
            if (models == null) throw new InvalidOperationException($"Player '{spec}' needs a model but no model source was provided.");
            return models.Get(spec.ModelPath) ?? throw new InvalidOperationException($"Model '{spec.ModelPath}' could not be loaded.");
        }
    }

    internal sealed class RandomPlayer : IPlayer {
        private readonly Random rng;
        // Rules only (legal moves); built from the base parameters like every engine (there is no engine without parameters).
        private readonly AtaxxAIEngine helper = new AtaxxAIEngine(EngineParamsFile.LoadBase());
        public PlayerSpec Spec { get; }
        public RandomPlayer(PlayerSpec spec, int seed) { Spec = spec; rng = new Random(seed); }

        public MoveDecision Choose(BitboardState board, PlayerColor side) {
            var legal = helper.GetAllValidMoves(board, side);
            if (legal.Count == 0) return new MoveDecision { HasMove = false, Action = ActionCodec.NoAction };
            var m = legal[rng.Next(legal.Count)];
            return new MoveDecision { HasMove = true, Move = m, Action = ActionCodec.Encode(m), LegalCount = legal.Count, IsExploration = true };
        }
        public void Dispose() => helper.Dispose();
    }

    internal sealed class PolicyPlayer : IPlayer {
        private readonly IPolicyValueModel model;
        private readonly Random rng;
        public PlayerSpec Spec { get; }
        public PolicyPlayer(PlayerSpec spec, IPolicyValueModel model, int seed) { Spec = spec; this.model = model; rng = new Random(seed); }

        public MoveDecision Choose(BitboardState board, PlayerColor side) {
            long t0 = Stopwatch.GetTimestamp();
            var c = PolicyMoveSelector.Select(model, board, side, Spec.Temp, Spec.TopK, rng);
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            return new MoveDecision { HasMove = c.HasMove, Move = c.Move, Action = c.Action, Milliseconds = ms, LegalCount = c.LegalCount };
        }
        public void Dispose() { }
    }

    internal sealed class SearchPlayer : IPlayer {
        private readonly AtaxxAIEngine engine;
        private readonly bool collectScore;
        public PlayerSpec Spec { get; }

        /// <param name="evaluator">null = the heuristic evaluator (built by the engine from <paramref name="engineParams"/>); a model evaluator replaces it.</param>
        public SearchPlayer(PlayerSpec spec, IValueEvaluator evaluator, int seed, bool train, bool model, EngineParams engineParams, bool collectScore = false) {
            Spec = spec;
            var cfg = new AIEngineConfig {
                AiDepth = spec.Depth,
                TrainingMode = train,
                Seed = seed,
                DisableParallelRootSearch = true,   // games run in parallel; serial search is deterministic
                // Capture-only quiescence extension. Default ON for normal-mode (arena) play, which is what the game ships; it has no
                // effect in training mode (self-play), where the engine never uses it. Model search never uses it unless asked.
                DisableQuiescenceSearch = !(spec.Quiescence ?? (!train && !model)),
                DisableRandomRootTies = !PlayerFactory.EffectiveRandomTies(spec),
                UseTimeManagement = spec.TimeMs > 0,
                SearchTimeLimitMs = spec.TimeMs,
                TimeManagedExtraDepth = 4,
                // With a time limit the engine must deepen 1..depth+4 (otherwise training mode jumps straight to the top depth).
                IterativeDeepeningInTraining = spec.IterativeDeepening || spec.TimeMs > 0,
                RootBonusScale = spec.Bonus ?? (model ? 0f : (float?)null),
                SelectionTemperatureUnit = model ? PlayerFactory.ValueScale : (float?)null,
                AspirationWindow = model ? (int)(PlayerFactory.ValueScale * 0.3f) : (int?)null,
                ThrowOnSearchError = true,          // a failing model must stop the run, not become a random move
            };
            engine = new AtaxxAIEngine(engineParams, cfg, evaluator) {
                MaxNodes = spec.Nodes > 0 ? spec.Nodes : (long?)null,
                CollectRootScores = collectScore,
            };
            this.collectScore = collectScore;
        }

        public MoveDecision Choose(BitboardState board, PlayerColor side) {
            var legal = engine.GetAllValidMoves(board, side);
            if (legal.Count == 0) return new MoveDecision { HasMove = false, Action = ActionCodec.NoAction };

            var b = board.Clone();
            b.ZobristHash = engine.ComputeZobristHash(b, side);
            engine.Board = b;
            engine.AIPlayerColor = side;

            long t0 = Stopwatch.GetTimestamp();
            Move m = engine.GetBestMove(side, false, Spec.Temp, Spec.TopK);
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

            int action = ActionCodec.NoAction;
            if (!m.Equals(default(Move))) action = ActionCodec.Encode(m);
            if (action == ActionCodec.NoAction || !ActionCodec.IsLegal(action, board, side))
                throw new InvalidOperationException($"Search player '{Spec}' returned an illegal move ({m.FromX},{m.FromY})->({m.ToX},{m.ToY}).");

            var decision = new MoveDecision {
                HasMove = true, Move = m, Action = action, LegalCount = legal.Count, Milliseconds = ms,
                Nodes = engine.LastSearchStats.Nodes + engine.LastSearchStats.QNodes,
                UsedFallback = engine.LastSearchUsedFallback, Depth = engine.LastCompletedDepth,
            };
            if (collectScore) {
                // The first entry is the best root move of the last completed iteration. Its Strategic part is the search value of the
                // position; it does not depend on the sampled move (temp/topk only change which move is played).
                // A budget-fallback search ignores the budget but still completes depth 1, so the score is still a valid depth-1 value; callers
                // that care check UsedFallback / Depth.
                var roots = engine.LastRootScores;
                if (roots != null && roots.Count > 0) { decision.HasScore = true; decision.Score = roots[0].Strategic; }
            }
            return decision;
        }

        public void Dispose() => engine.Dispose();
    }
}
