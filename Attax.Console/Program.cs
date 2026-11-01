using System.Collections.Concurrent;
using System.Security.Cryptography;
using Attax.Core;
using Attax.Model;
using Attax.Data;
using Attax.Eval.OnnxRuntime;
using Attax.Play;
using CommandLine;
using CommandLine.Text;
using static Attax.Core.AtaxxAIEngine;

namespace Attax.Console {
    partial class Program {
        static int Main(string[] args) {
            var parser = new Parser(config => {
                config.HelpWriter = null;
                config.CaseSensitive = false;
                config.CaseInsensitiveEnumValues = true;
            });

            var parseResult = parser.ParseArguments<SelfPlayOptions, RelabelOptions, ArenaOptions, ValidateLogOptions,
                VerifyModelOptions, FixturesOptions, ValidateHashOptions, ParamsDefaultOptions, ParamsCheckOptions, CompareMovesOptions>(args);

            int exitCode = 1;
            parseResult
                .WithParsed<SelfPlayOptions>(o => exitCode = Execute(() => RunSelfPlay(o)))
                .WithParsed<RelabelOptions>(o => exitCode = Execute(() => RunRelabel(o)))
                .WithParsed<ArenaOptions>(o => exitCode = Execute(() => RunArena(o)))
                .WithParsed<ValidateLogOptions>(o => exitCode = Execute(() => RunValidateLog(o)))
                .WithParsed<VerifyModelOptions>(o => exitCode = Execute(() => RunVerifyModel(o)))
                .WithParsed<FixturesOptions>(o => exitCode = Execute(() => RunFixtures(o)))
                .WithParsed<ValidateHashOptions>(o => exitCode = Execute(() => RunValidateHash(o)))
                .WithParsed<ParamsDefaultOptions>(o => exitCode = Execute(() => RunParamsDefault(o)))
                .WithParsed<ParamsCheckOptions>(o => exitCode = Execute(() => RunParamsCheck(o)))
                .WithParsed<CompareMovesOptions>(o => exitCode = Execute(() => RunCompareMoves(o)))
                .WithNotParsed(errors => exitCode = HandleParseErrors(parseResult, errors));
            return exitCode;
        }

        static int Execute(Action action) {
            try {
                action();
                return 0;
            } catch (Exception ex) {
                var root = ex is AggregateException agg ? agg.Flatten().InnerExceptions[0] : ex;
                System.Console.Error.WriteLine($"Error: {root.Message}");
                if (Environment.GetEnvironmentVariable("ATTAX_DEBUG") == "1") System.Console.Error.WriteLine(root.ToString());
                return 1;
            }
        }

        static int HandleParseErrors<T>(ParserResult<T> parseResult, IEnumerable<Error> errors) {
            bool help = errors.Any(e => e.Tag == ErrorType.HelpRequestedError || e.Tag == ErrorType.HelpVerbRequestedError || e.Tag == ErrorType.VersionRequestedError);
            var text = HelpText.AutoBuild(parseResult, h => {
                h.AdditionalNewLineAfterOption = false;
                h.Heading = "Attax.Console";
                h.Copyright = string.Empty;
                return HelpText.DefaultParsingErrorsHandler(parseResult, h);
            }, e => e);
            System.Console.WriteLine(text);
            return help ? 0 : 1;
        }

        // ------------------------------------------------------------------------------------------------
        // Shared infrastructure
        // ------------------------------------------------------------------------------------------------

        /// <summary>One ONNX session per model file, shared by every game thread, with inference metering.</summary>
        internal sealed class OnnxModelSource : IModelSource, IDisposable {
            private readonly int threads;
            private readonly ConcurrentDictionary<string, Lazy<MeteredPolicyValueModel>> cache = new(StringComparer.OrdinalIgnoreCase);

            public OnnxModelSource(int gameParallelism) {
                // Game-level parallelism owns the cores, so pin each inference to one thread.
                threads = gameParallelism > 1 ? 1 : 0;
            }

            public IPolicyValueModel Get(string path) =>
                cache.GetOrAdd(Path.GetFullPath(path), p => new Lazy<MeteredPolicyValueModel>(() =>
                    new MeteredPolicyValueModel(new OnnxPolicyValueModel(p, threads)))).Value;

            public IEnumerable<(string Path, MeteredPolicyValueModel Model)> All =>
                cache.Select(kv => (kv.Key, kv.Value.Value));

            public void Dispose() {
                foreach (var kv in cache) if (kv.Value.IsValueCreated) kv.Value.Value.Dispose();
            }
        }

        internal static OnnxModelSource? CreateModelSource(int parallelism, params PlayerSpec?[] specs) {
            var withModels = specs.Where(s => s != null && s.UsesModel).Select(s => s!).ToList();
            if (withModels.Count == 0) return null;
            System.Console.WriteLine(OnnxPolicyValueModel.GetRuntimeDiagnostics());
            var source = new OnnxModelSource(parallelism);
            // Load (and contract-check) every model up front: a bad model must fail before any game is played.
            foreach (var s in withModels) source.Get(s.ModelPath);
            return source;
        }

        internal static ulong ModelHash(string path) {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            return BitConverter.ToUInt64(sha.ComputeHash(fs), 0);
        }

        internal static LogV3.TeacherConfig MakeTeacherConfig(byte id, PlayerSpec t) => new LogV3.TeacherConfig {
            Id = id,
            Kind = t.Kind == PlayerKind.Value ? LogV3.TeacherKind.ModelValueSearch : LogV3.TeacherKind.HeuristicSearch,
            Depth = (byte)t.Depth,
            NodeBudget = t.Nodes,
            RootBonusScale = t.EffectiveBonus,
            // Self-play and relabel run teachers with defaultTrain: true. Training mode and normal mode give different labels, so the log says which.
            Flags = TeacherModeFlags(t),
            // Model teachers: hash of the model file. Heuristic teachers: hash of their engine parameters (0 = the built-in defaults),
            // so a relabeled log records which parameter set produced each label.
            ModelHash = t.UsesModel ? ModelHash(t.ModelPath) : EngineParamsFile.FingerprintUInt64(t.ResolveParams()),   // teachers are search players, so params exist
        };

        internal static byte TeacherModeFlags(PlayerSpec t) {
            if (!t.IsSearch) return 0;
            var (train, q) = PlayerFactory.EffectiveMode(t, defaultTrain: true);
            return (byte)((train ? 0 : LogV3.TeacherFlagNormalMode) | (q ? LogV3.TeacherFlagQuiescence : 0)
                          | (PlayerFactory.EffectiveRandomTies(t) ? LogV3.TeacherFlagRandomTies : 0));
        }

        /// <param name="randomTies">The teacher's tie-break. false (the default of the --teacherRandomTies option) makes the teacher pick the FIRST of several equally scored root
        /// moves, a consistent rule for the model to learn that a re-search reproduces; true keeps whatever engine-params.json says. An explicit
        /// ties=... in the teacher spec always wins over the option.</param>
        internal static PlayerSpec ParseTeacher(string text, bool randomTies = false) {
            var spec = PlayerSpec.Parse(text);
            if (!spec.IsSearch) throw new ArgumentException($"A teacher must be a search player (classic or value), got '{text}'. Direct policy and random players cannot label positions.");
            if (!randomTies && spec.RandomTies == null) spec = spec.WithRandomTies(false);   // an explicit ties=... in the teacher spec always wins
            return spec.Greedy();   // a label is the argmax, never a sample
        }

        internal struct GameEnd {
            public bool Terminal;
            public int Plies;
            public int ResultRedPov;   // +1 red, -1 blue, 0 draw
            public BitboardState Board;
        }

        /// <summary>
        /// The single game loop used by self-play and the arena. Sides strictly alternate (there are no passes): a side that
        /// cannot move makes the position terminal and is decided by AtaxxAIEngine.GetTerminalResult. Reaching maxPlies
        /// without a terminal position is a ply-cap end whose result falls back to piece counts.
        /// </summary>
        internal static GameEnd RunGame(AtaxxAIEngine referee, BitboardState board, int startPly, int maxPlies,
                                        Func<int, PlayerColor, BitboardState, MoveDecision> chooser,
                                        Action<int, PlayerColor, BitboardState, MoveDecision>? onMove = null) {
            int ply = startPly;
            while (true) {
                if (referee.IsGameOver(board)) break;
                if (ply >= maxPlies) break;
                var side = ply % 2 == 0 ? PlayerColor.Red : PlayerColor.Blue;
                var d = chooser(ply, side, board);
                if (!d.HasMove) throw new InvalidOperationException($"No move returned at ply {ply} although the game is not over.");
                if (!ActionCodec.IsLegal(d.Action, board, side))
                    throw new InvalidOperationException($"Illegal move (action {d.Action}) chosen at ply {ply} for {side}.");
                onMove?.Invoke(ply, side, board, d);
                referee.MakeMove(board, d.Move, side);
                ply++;
            }

            var end = new GameEnd { Plies = ply, Board = board };
            var terminal = referee.GetTerminalResult(board);
            if (terminal != TerminalResult.NotOver) {
                end.Terminal = true;
                end.ResultRedPov = terminal == TerminalResult.RedWins ? 1 : terminal == TerminalResult.BlueWins ? -1 : 0;
            } else {
                var (red, blue) = GetRedAndBlueCounts(board, PlayerColor.Red);
                end.ResultRedPov = red > blue ? 1 : blue > red ? -1 : 0;
            }
            return end;
        }

        internal static AtaxxAIEngine NewReferee() => new AtaxxAIEngine(EngineParamsFile.LoadBase());   // rules only (the search is never called); built like every engine

        internal static MoveDecision RandomDecision(AtaxxAIEngine referee, BitboardState board, PlayerColor side, Random rng) {
            var legal = referee.GetAllValidMoves(board, side);
            if (legal.Count == 0) return new MoveDecision { HasMove = false, Action = ActionCodec.NoAction };
            var m = legal[rng.Next(legal.Count)];
            return new MoveDecision { HasMove = true, Move = m, Action = ActionCodec.Encode(m), LegalCount = legal.Count, IsExploration = true };
        }

        static void RunValidateLog(ValidateLogOptions o) {
            if (!File.Exists(o.Path)) throw new FileNotFoundException($"File not found: {o.Path}");
            var reader = new LogV3Reader();
            long games = 0, samples = 0, teacherValid = 0, plyCap = 0, random = 0;
            var modes = new Dictionary<string, int>();
            foreach (var g in reader.ReadGames(o.Path)) {
                games++;
                samples += g.Samples.Length;
                foreach (var s in g.Samples) { if (s.TeacherValid) teacherValid++; if (s.PlayedRandom) random++; }
                if (g.Result.Termination == LogV3.Termination.PlyCap) plyCap++;
                string key = $"{g.Header.RedMode}/{g.Header.BlueMode}";
                modes[key] = modes.TryGetValue(key, out int c) ? c + 1 : 1;
            }
            System.Console.WriteLine($"Log is valid: {games} games, {samples} samples, {teacherValid} teacher labels, {random} random-played, {plyCap} ply-cap games, teachers declared={reader.Teachers.Count}");
            System.Console.WriteLine("Modes (red/blue): " + string.Join(", ", modes.Select(kv => $"{kv.Key}={kv.Value}")));
        }
    }
}
