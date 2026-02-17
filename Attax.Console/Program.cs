using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Attax.Core;
using Attax.Eval.OnnxRuntime;
using CommandLine;
using CommandLine.Text;

namespace Attax.Console {
    class Program {
        static int Main(string[] args) {
            var normalizedArgs = NormalizeArgs(args);

            var parser = new Parser(config => {
                config.HelpWriter = null;
                config.CaseSensitive = false;
                config.CaseInsensitiveEnumValues = true;
            });

            var parseResult = parser.ParseArguments<SelfPlayLogGenOptions, ModelArenaOptions, MirrorTestOptions, ValidateLogOptions, ValidateHashOptions>(normalizedArgs);

            int exitCode = 1;
            parseResult
                .WithParsed<SelfPlayLogGenOptions>(options => exitCode = Execute(() => RunSelfPlayLogGen(options)))
                .WithParsed<ModelArenaOptions>(options => exitCode = Execute(() => RunModelArena(options)))
                .WithParsed<MirrorTestOptions>(options => exitCode = Execute(() => RunMirrorTest(options)))
                .WithParsed<ValidateLogOptions>(options => exitCode = Execute(() => RunValidateLog(options)))
                .WithParsed<ValidateHashOptions>(options => exitCode = Execute(() => RunValidateHash(options)))
                .WithNotParsed(errors => exitCode = HandleParseErrors(parseResult, errors));

            return exitCode;
        }

        static int Execute(Action action) {
            try {
                action();
                return 0;
            } catch (Exception ex) {
                System.Console.WriteLine($"Error: {ex.Message}");
                PrintUsage();
                return 1;
            }
        }

        static int HandleParseErrors<T>(ParserResult<T> parseResult, IEnumerable<Error> errors) {
            bool isHelpOrVersion = errors.Any(err =>
                err.Tag == ErrorType.HelpRequestedError
                || err.Tag == ErrorType.HelpVerbRequestedError
                || err.Tag == ErrorType.VersionRequestedError);

            var helpText = HelpText.AutoBuild(parseResult, help => {
                help.AdditionalNewLineAfterOption = false;
                help.Heading = "Attax.Console";
                help.Copyright = string.Empty;
                return HelpText.DefaultParsingErrorsHandler(parseResult, help);
            }, e => e);

            System.Console.WriteLine(helpText);
            return isHelpOrVersion ? 0 : 1;
        }

        static string[] NormalizeArgs(string[] args) {
            if (args.Length == 0) {
                return args;
            }

            var normalized = new List<string>(args);
            if (normalized[0].Equals("--mirrortest", StringComparison.OrdinalIgnoreCase)) {
                normalized[0] = "mirror-test";
            }

            return normalized.ToArray();
        }

        static void PrintUsage() {
            System.Console.WriteLine("Usage:");
            System.Console.WriteLine("  Attax.Console selfplayloggen --games <N> --seed <S> --out <path> --nodeBudget <N> --topK <K> --temp <T> --samples <S> [--aiDepth <1..12>] [--useOrthogonalOnlyCapture true|false] [--useMLRootOnly true|false] [--disableQuiescence true|false] [--epsilonStart <D>] [--epsilonMid <D>] [--epsilonLate <D>] [--epsilonPly1 <N>] [--epsilonPly2 <N>] [--nodesMin <N>] [--nodesMax <N>] [--topKSet <csv>] [--tempSet <csv>] [--profileMode fixed|random] [--weakSideChance <D>] [--weakSideNodesScale <D>] [--symmetryMode none|random|all] [--logGenMode true|false]");
            System.Console.WriteLine("  Attax.Console modelarena --model1 <path|heuristic> --model2 <path|heuristic> --games <N> [--ort cpu|cuda] [--aiDepth <1..12>] [--useOrthogonalOnlyCapture true|false] [--useMLRootOnly true|false] [--disableQuiescence true|false]");
            System.Console.WriteLine("  Attax.Console mirror-test [--model <path|heuristic>] [--ort cpu|cuda] [--side red|blue]");
            System.Console.WriteLine("  Attax.Console validate-log --path <path> [--strict true|false]");
            System.Console.WriteLine("  Attax.Console validate-hash [--games <N>] [--seed <S>] [--moves <N>] [--strict true|false]");
        }


        static void RunSelfPlayLogGen(SelfPlayLogGenOptions opts) {
            var sharedConfig = AttaxConfigLoader.LoadOrDefault();

            int games = opts.Games;
            int seed = opts.Seed;
            string outPath = opts.Out;
            int nodeBudget = opts.NodeBudget;
            int topK = opts.TopK;
            double temp = opts.Temp;

            int samplesPerGame = opts.SamplesPerGame ?? opts.Samples;

            int aiDepth = opts.AiDepth;
            bool useOrthogonalOnlyCapture = opts.UseOrthogonalOnlyCapture;
            bool debugInit = opts.DebugInit;
            bool useMLRootOnly = opts.UseMLRootOnly;
            bool disableQuiescence = opts.DisableQuiescence;
            bool logGenMode = opts.LogGenMode;

            double epsilonStart = opts.EpsilonStart;
            double epsilonMid = opts.EpsilonMid;
            double epsilonLate = opts.EpsilonLate;
            int epsilonPly1 = opts.EpsilonPly1;
            int epsilonPly2 = opts.EpsilonPly2;
            if (epsilonPly1 > epsilonPly2) {
                throw new ArgumentException("--epsilonPly1 must be <= --epsilonPly2.");
            }

            int nodesMin = opts.NodesMin ?? nodeBudget;
            int nodesMax = opts.NodesMax ?? nodeBudget;
            if (nodesMin > nodesMax) {
                throw new ArgumentException("--nodesMin must be <= --nodesMax.");
            }

            string topKSetRaw = string.IsNullOrWhiteSpace(opts.TopKSet)
                ? topK.ToString(CultureInfo.InvariantCulture)
                : opts.TopKSet;
            string tempSetRaw = string.IsNullOrWhiteSpace(opts.TempSet)
                ? temp.ToString(CultureInfo.InvariantCulture)
                : opts.TempSet;
            var topKSet = ParseCsvInts(topKSetRaw, "topKSet");
            var tempSet = ParseCsvDoubles(tempSetRaw, "tempSet");

            ProfileModeOption profileMode = opts.ProfileMode;
            double weakSideChance = opts.WeakSideChance;
            double weakSideNodesScale = opts.WeakSideNodesScale;
            SymmetryModeOption symmetryMode = opts.SymmetryMode;

            int phaseEarlyMaxPieces = 14;
            int phaseMidMaxPieces = 35;
            var (phaseEarlyQuota, phaseMidQuota, phaseLateQuota) = GetPhaseQuotas(samplesPerGame);

            var gamePlies = new List<int>(Math.Max(1, games));
            var terminalReasonCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int redWins = 0;
            int blueWins = 0;
            int drawGames = 0;
            int totalPasses = 0;

            int randomMovesPlayed = 0;
            int searchedMovesPlayed = 0;
            var randomMovesByPhase = new int[3];
            var searchedMovesByPhase = new int[3];
            double epsilonStrongSum = 0.0;
            double epsilonWeakSum = 0.0;
            int epsilonStrongCount = 0;
            int epsilonWeakCount = 0;

            var profileNodeBudgets = new List<int>(Math.Max(1, games));
            var profileTopKHistogram = new Dictionary<int, int>();
            var profileTempHistogram = new Dictionary<string, int>(StringComparer.Ordinal);
            int weakSideActiveGames = 0;
            int lowEndNodeBudgetHits = 0;

            int underfilledGames = 0;
            int totalRequestedSamples = 0;
            int totalCollectedSamples = 0;
            var collectedSamplesByPhase = new int[3];

            System.Console.WriteLine(
                $"Starting SelfPlayLogGen: Games={games}, Seed={seed}, Out={outPath}, ProfileMode={profileMode}, Nodes=[{nodesMin},{nodesMax}], TopKSet={string.Join(",", topKSet)}, TempSet={string.Join(",", tempSet.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)))}, Epsilon=[{epsilonStart:0.###},{epsilonMid:0.###},{epsilonLate:0.###}]@Ply[{epsilonPly1},{epsilonPly2}], WeakSideChance={weakSideChance:0.###}, WeakSideNodesScale={weakSideNodesScale:0.###}, SymmetryMode={symmetryMode}, SamplesPerGame={samplesPerGame}, AiDepth={aiDepth}, LogGenMode={(logGenMode ? 1 : 0)}, UseOrthogonalOnlyCapture={(useOrthogonalOnlyCapture ? 1 : 0)}, UseMLRootOnly={(useMLRootOnly ? 1 : 0)}, DisableQuiescenceSearch={(disableQuiescence ? 1 : 0)}, DebugInit={(debugInit ? 1 : 0)}");

            using (var logSink = new BinaryTrainingLogSink(outPath)) {
                var rng = new Random(seed);
                long totalPositions = 0;
                var stopwatch = Stopwatch.StartNew();

                for (int i = 0; i < games; i++) {
                    int gameSeed = rng.Next();
                    uint gameId = (uint)i;

                    int profileNodes = profileMode == ProfileModeOption.Random ? rng.Next(nodesMin, nodesMax + 1) : nodeBudget;
                    int profileTopK = profileMode == ProfileModeOption.Random ? topKSet[rng.Next(topKSet.Count)] : topK;
                    double profileTemp = profileMode == ProfileModeOption.Random ? tempSet[rng.Next(tempSet.Count)] : temp;

                    profileNodeBudgets.Add(profileNodes);
                    if (profileNodes == nodesMin) {
                        lowEndNodeBudgetHits++;
                    }

                    if (!profileTopKHistogram.TryAdd(profileTopK, 1)) {
                        profileTopKHistogram[profileTopK]++;
                    }

                    string profileTempKey = profileTemp.ToString("0.###", CultureInfo.InvariantCulture);
                    if (!profileTempHistogram.TryAdd(profileTempKey, 1)) {
                        profileTempHistogram[profileTempKey]++;
                    }

                    bool hasWeakSide = rng.NextDouble() < weakSideChance;
                    if (hasWeakSide) {
                        weakSideActiveGames++;
                    }
                    AtaxxAIEngine.PlayerColor weakSide = rng.Next(2) == 0
                        ? AtaxxAIEngine.PlayerColor.Red
                        : AtaxxAIEngine.PlayerColor.Blue;

                    var evaluator = new HeuristicEvaluator();
                    int redSeed = CombineSeed(gameSeed, 0x13579BDF);
                    int blueSeed = CombineSeed(gameSeed, 0x2468ACE0);

                    var redConfig = new AtaxxAIEngine.AIEngineConfig {
                        UseTimeManagement = false,
                        Seed = redSeed,
                        AiDepth = aiDepth,
                        UseOrthogonalOnlyCapture = useOrthogonalOnlyCapture,
                        TrainingMode = true,
                        UseMLRootOnly = useMLRootOnly,
                        DisableQuiescenceSearch = disableQuiescence,
                        DisableParallelRootSearch = true,
                        LogGenMode = logGenMode
                    };

                    var blueConfig = new AtaxxAIEngine.AIEngineConfig {
                        UseTimeManagement = false,
                        Seed = blueSeed,
                        AiDepth = aiDepth,
                        UseOrthogonalOnlyCapture = useOrthogonalOnlyCapture,
                        TrainingMode = true,
                        UseMLRootOnly = useMLRootOnly,
                        DisableQuiescenceSearch = disableQuiescence,
                        DisableParallelRootSearch = true,
                        LogGenMode = logGenMode
                    };

                    var redEngine = new AtaxxAIEngine(evaluator, null, null, redConfig) { AIPlayerColor = AtaxxAIEngine.PlayerColor.Red, MaxNodes = profileNodes };
                    var blueEngine = new AtaxxAIEngine(evaluator, null, null, blueConfig) { AIPlayerColor = AtaxxAIEngine.PlayerColor.Blue, MaxNodes = profileNodes };

                    var masterBoard = CreateStandardInitialBoard();
                    masterBoard.ZobristHash = redEngine.ComputeZobristHash(masterBoard, AtaxxAIEngine.PlayerColor.Red);

                    if (debugInit) {
                        int redCount0 = AtaxxAIEngine.PopCount(masterBoard.RedPieces);
                        int blueCount0 = AtaxxAIEngine.PopCount(masterBoard.BluePieces);
                        int emptyCount0 = AtaxxAIEngine.PopCount(masterBoard.EmptySquares());
                        bool redHasMoves0 = redEngine.GetAllValidMoves(masterBoard, AtaxxAIEngine.PlayerColor.Red).Count > 0;
                        bool blueHasMoves0 = blueEngine.GetAllValidMoves(masterBoard, AtaxxAIEngine.PlayerColor.Blue).Count > 0;
                        System.Console.WriteLine($"[selfplay-debug-init] game={i + 1}, red={redCount0}, blue={blueCount0}, empty={emptyCount0}, redMovesPly0={redHasMoves0}, blueMovesPly0={blueHasMoves0}");
                    }

                    byte ruleFlags = (byte)(useOrthogonalOnlyCapture ? 0b_0000_0001 : 0);
                    logSink.LogGameStart(gameId, (ulong)gameSeed, ruleFlags, 7);

                    AtaxxAIEngine.PlayerColor sideToMove = AtaxxAIEngine.PlayerColor.Red;
                    int plyCount = 0;
                    bool gameOver = false;
                    int consecutivePasses = 0;
                    int passesThisGame = 0;
                    string terminalReason = "unknown";

                    redEngine.Board = masterBoard;
                    blueEngine.Board = masterBoard;

                    var samplingRng = new Random(CombineSeed(gameSeed, 0x5BD1E995));
                    var sampler = new PhaseAwareSampler(
                        samplesPerGame,
                        phaseEarlyMaxPieces,
                        phaseMidMaxPieces,
                        phaseEarlyQuota,
                        phaseMidQuota,
                        phaseLateQuota,
                        samplingRng);

                    while (!gameOver) {
                        masterBoard.ZobristHash = redEngine.ComputeZobristHash(masterBoard, sideToMove);

                        var currentEngine = sideToMove == AtaxxAIEngine.PlayerColor.Red ? redEngine : blueEngine;
                        var legalMoves = currentEngine.GetAllValidMoves(masterBoard, sideToMove);

                        sampler.Observe((ushort)plyCount, masterBoard, sideToMove, legalMoves.Count);

                        double epsilon = GetScheduledEpsilon(plyCount, epsilonPly1, epsilonPly2, epsilonStart, epsilonMid, epsilonLate);

                        bool sideIsWeak = hasWeakSide && sideToMove == weakSide;
                        if (sideIsWeak) {
                            epsilonWeakSum += epsilon;
                            epsilonWeakCount++;
                        } else {
                            epsilonStrongSum += epsilon;
                            epsilonStrongCount++;
                        }

                        int effectiveNodes = sideIsWeak
                            ? Math.Max(1, (int)Math.Round(profileNodes * weakSideNodesScale, MidpointRounding.AwayFromZero))
                            : profileNodes;

                        currentEngine.MaxNodes = effectiveNodes;

                        int pieceCount = AtaxxAIEngine.PopCount(masterBoard.RedPieces) + AtaxxAIEngine.PopCount(masterBoard.BluePieces);
                        int phaseIndex = GetPhaseIndex(pieceCount, phaseEarlyMaxPieces, phaseMidMaxPieces);

                        AtaxxAIEngine.Move move = default;
                        if (legalMoves.Count > 0) {
                            if (rng.NextDouble() < epsilon) {
                                move = legalMoves[rng.Next(legalMoves.Count)];
                                randomMovesPlayed++;
                                randomMovesByPhase[phaseIndex]++;
                            } else {
                                move = currentEngine.GetBestMove(sideToMove, false, (float)profileTemp, profileTopK);
                                if (move.Equals(default(AtaxxAIEngine.Move))) {
                                    move = legalMoves[rng.Next(legalMoves.Count)];
                                    randomMovesPlayed++;
                                    randomMovesByPhase[phaseIndex]++;
                                } else {
                                    searchedMovesPlayed++;
                                    searchedMovesByPhase[phaseIndex]++;
                                }
                            }
                        }

                        if (move.Equals(default(AtaxxAIEngine.Move))) {
                            consecutivePasses++;
                            passesThisGame++;
                            totalPasses++;
                            sideToMove = AtaxxAIEngine.SwitchPlayer(sideToMove);
                        } else {
                            consecutivePasses = 0;
                            redEngine.MakeMove(masterBoard, move, sideToMove);
                            sideToMove = AtaxxAIEngine.SwitchPlayer(sideToMove);
                        }

                        plyCount++;

                        if (redEngine.IsGameOver(masterBoard)) {
                            gameOver = true;
                            terminalReason = "is-game-over";
                        } else if (consecutivePasses >= 2) {
                            gameOver = true;
                            terminalReason = "double-pass";
                        } else if (plyCount > 400) {
                            gameOver = true;
                            terminalReason = "ply-cap";
                        }
                    }

                    var (redCount, blueCount) = AtaxxAIEngine.GetRedAndBlueCounts(masterBoard, AtaxxAIEngine.PlayerColor.Red);
                    sbyte result = 0;
                    if (redCount > blueCount) result = 1;
                    else if (blueCount > redCount) result = -1;

                    gamePlies.Add(plyCount);
                    if (!terminalReasonCounts.TryAdd(terminalReason, 1)) {
                        terminalReasonCounts[terminalReason]++;
                    }

                    if (result > 0) redWins++;
                    else if (result < 0) blueWins++;
                    else drawGames++;

                    var keptSamples = sampler.GetFinalSamples();
                    int samplesCollectedThisGame = keptSamples.Count;
                    totalRequestedSamples += samplesPerGame;
                    totalCollectedSamples += samplesCollectedThisGame;
                    if (samplesCollectedThisGame < samplesPerGame) {
                        underfilledGames++;
                    }
                    var collectedThisGameByPhase = new int[3];
                    foreach (var sample in keptSamples) {
                        int samplePieceCount = AtaxxAIEngine.PopCount(sample.Board.RedPieces) + AtaxxAIEngine.PopCount(sample.Board.BluePieces);
                        int samplePhaseIndex = GetPhaseIndex(samplePieceCount, phaseEarlyMaxPieces, phaseMidMaxPieces);
                        collectedThisGameByPhase[samplePhaseIndex]++;
                        collectedSamplesByPhase[samplePhaseIndex]++;
                        if (symmetryMode == SymmetryModeOption.None) {
                            logSink.LogPosition(gameId, sample.Ply, sample.Board, sample.SideToMove);
                            totalPositions++;
                        } else if (symmetryMode == SymmetryModeOption.Random) {
                            int symmetry = rng.Next(8);
                            var transformed = ApplySymmetryToBoard(sample.Board, symmetry);
                            logSink.LogPosition(gameId, sample.Ply, transformed, sample.SideToMove);
                            totalPositions++;
                        } else {
                            for (int symmetry = 0; symmetry < 8; symmetry++) {
                                var transformed = ApplySymmetryToBoard(sample.Board, symmetry);
                                logSink.LogPosition(gameId, sample.Ply, transformed, sample.SideToMove);
                                totalPositions++;
                            }
                        }
                    }

                    logSink.LogGameEnd(gameId, result, (ushort)plyCount);

                    if ((i + 1) % 10 == 0) {
                        double elapsed = stopwatch.Elapsed.TotalSeconds;
                        System.Console.WriteLine($"Game {i + 1}/{games} finished. {totalPositions / elapsed:F0} pos/sec. Avg plies: {plyCount}. Time {elapsed} seconds");

                        int gamesCompleted = gamePlies.Count;
                        var sortedPlies = gamePlies.OrderBy(v => v).ToArray();
                        int minPlies = sortedPlies[0];
                        int p10Plies = GetPercentile(sortedPlies, 0.10);
                        int p50Plies = GetPercentile(sortedPlies, 0.50);
                        int p90Plies = GetPercentile(sortedPlies, 0.90);
                        int maxPlies = sortedPlies[sortedPlies.Length - 1];
                        double pctLe20 = 100.0 * gamePlies.Count(v => v <= 20) / gamesCompleted;
                        double pctLe40 = 100.0 * gamePlies.Count(v => v <= 40) / gamesCompleted;

                        int totalMovesPlayed = randomMovesPlayed + searchedMovesPlayed;
                        double randomPct = totalMovesPlayed == 0 ? 0.0 : 100.0 * randomMovesPlayed / totalMovesPlayed;
                        double randomPctEarly = GetRatioPercent(randomMovesByPhase[0], randomMovesByPhase[0] + searchedMovesByPhase[0]);
                        double randomPctMid = GetRatioPercent(randomMovesByPhase[1], randomMovesByPhase[1] + searchedMovesByPhase[1]);
                        double randomPctLate = GetRatioPercent(randomMovesByPhase[2], randomMovesByPhase[2] + searchedMovesByPhase[2]);
                        double avgEpsilonStrong = epsilonStrongCount == 0 ? 0.0 : epsilonStrongSum / epsilonStrongCount;
                        double avgEpsilonWeak = epsilonWeakCount == 0 ? 0.0 : epsilonWeakSum / epsilonWeakCount;

                        double meanNodeBudget = profileNodeBudgets.Count == 0 ? 0.0 : profileNodeBudgets.Average();
                        int medianNodeBudget = profileNodeBudgets.Count == 0
                            ? 0
                            : GetPercentile(profileNodeBudgets.OrderBy(v => v).ToArray(), 0.50);
                        double lowEndHitPct = profileNodeBudgets.Count == 0 ? 0.0 : 100.0 * lowEndNodeBudgetHits / profileNodeBudgets.Count;
                        string topKHist = string.Join(", ", profileTopKHistogram.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));
                        string tempHist = string.Join(", ", profileTempHistogram.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));
                        string terminalHist = string.Join(", ", terminalReasonCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));

                        double underfilledPct = gamesCompleted == 0 ? 0.0 : 100.0 * underfilledGames / gamesCompleted;
                        double avgCollectedSamples = gamesCompleted == 0 ? 0.0 : (double)totalCollectedSamples / gamesCompleted;
                        double collectionRatePct = totalRequestedSamples == 0 ? 0.0 : 100.0 * totalCollectedSamples / totalRequestedSamples;

                        System.Console.WriteLine($"[selfplay-metrics] plies min/p10/p50/p90/max={minPlies}/{p10Plies}/{p50Plies}/{p90Plies}/{maxPlies}, <=20={pctLe20:0.0}%, <=40={pctLe40:0.0}%");
                        System.Console.WriteLine($"[selfplay-metrics] winners red/blue/draw={redWins}/{blueWins}/{drawGames}, terminalReasons={terminalHist}, totalPasses={totalPasses}, lastGamePasses={passesThisGame}");
                        System.Console.WriteLine($"[selfplay-metrics] exploration random={randomMovesPlayed}, searched={searchedMovesPlayed}, random%={randomPct:0.0}%, random% early/mid/late={randomPctEarly:0.0}/{randomPctMid:0.0}/{randomPctLate:0.0}, avgEpsilon strong/weak={avgEpsilonStrong:0.###}/{avgEpsilonWeak:0.###}");
                        System.Console.WriteLine($"[selfplay-metrics] profile nodeBudget mean/median={meanNodeBudget:0.##}/{medianNodeBudget}, lowEndHit%={lowEndHitPct:0.0}%, weakSideActive%={GetRatioPercent(weakSideActiveGames, gamesCompleted):0.0}%, topKHist={topKHist}, tempHist={tempHist}");
                        System.Console.WriteLine($"[selfplay-metrics] sampling lastCollected={samplesCollectedThisGame}/{samplesPerGame} (E/M/L={collectedThisGameByPhase[0]}/{collectedThisGameByPhase[1]}/{collectedThisGameByPhase[2]}), underfilledGames={underfilledGames}/{gamesCompleted} ({underfilledPct:0.0}%), avgCollected={avgCollectedSamples:0.##}, collectionRate={collectionRatePct:0.0}%, totalPhaseCollected E/M/L={collectedSamplesByPhase[0]}/{collectedSamplesByPhase[1]}/{collectedSamplesByPhase[2]}");
                    }
                }
            }
        }

        private readonly struct SampledPosition {
            public readonly ushort Ply;
            public readonly BitboardState Board;
            public readonly AtaxxAIEngine.PlayerColor SideToMove;

            public SampledPosition(ushort ply, BitboardState board, AtaxxAIEngine.PlayerColor sideToMove) {
                Ply = ply;
                Board = board;
                SideToMove = sideToMove;
            }
        }

        static List<int> ParseCsvInts(string raw, string argName) {
            if (string.IsNullOrWhiteSpace(raw)) {
                throw new ArgumentException($"--{argName} cannot be empty.");
            }

            var values = new List<int>();
            foreach (var token in raw.Split(',')) {
                var trimmed = token.Trim();
                if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) {
                    throw new ArgumentException($"Invalid integer in --{argName}: '{trimmed}'.");
                }

                values.Add(value);
            }

            if (values.Count == 0) {
                throw new ArgumentException($"--{argName} must contain at least one value.");
            }

            return values;
        }

        static List<double> ParseCsvDoubles(string raw, string argName) {
            if (string.IsNullOrWhiteSpace(raw)) {
                throw new ArgumentException($"--{argName} cannot be empty.");
            }

            var values = new List<double>();
            foreach (var token in raw.Split(',')) {
                var trimmed = token.Trim();
                if (!double.TryParse(trimmed, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double value)) {
                    throw new ArgumentException($"Invalid double in --{argName}: '{trimmed}'.");
                }

                values.Add(value);
            }

            if (values.Count == 0) {
                throw new ArgumentException($"--{argName} must contain at least one value.");
            }

            return values;
        }

        static int CombineSeed(int baseSeed, int salt) {
            unchecked {
                int mixed = baseSeed;
                mixed ^= salt + (int)0x9E3779B9 + (mixed << 6) + (mixed >> 2);
                return mixed;
            }
        }

        static double GetScheduledEpsilon(int ply, int ply1, int ply2, double epsilonStart, double epsilonMid, double epsilonLate) {
            if (ply <= ply1) return Clamp01(epsilonStart);
            if (ply <= ply2) return Clamp01(epsilonMid);
            return Clamp01(epsilonLate);
        }

        static double Clamp01(double value) {
            if (value < 0.0) return 0.0;
            if (value > 1.0) return 1.0;
            return value;
        }

        static (int early, int mid, int late) GetPhaseQuotas(int samplesPerGame) {
            int total = Math.Max(0, samplesPerGame);
            if (total == 0) return (0, 0, 0);

            int early = (int)Math.Round(total * 0.20, MidpointRounding.AwayFromZero);
            int late = (int)Math.Round(total * 0.20, MidpointRounding.AwayFromZero);
            int mid = total - early - late;

            if (mid < 0) {
                mid = 0;
                early = Math.Min(early, total);
                late = total - early;
            }

            return (early, mid, late);
        }

        static int GetPhaseIndex(int pieceCount, int phaseEarlyMaxPieces, int phaseMidMaxPieces) {
            if (pieceCount <= phaseEarlyMaxPieces) return 0;
            if (pieceCount <= phaseMidMaxPieces) return 1;
            return 2;
        }

        static int GetPercentile(int[] sortedValues, double percentile) {
            if (sortedValues.Length == 0) return 0;
            double p = Math.Clamp(percentile, 0.0, 1.0);
            int index = (int)Math.Round(p * (sortedValues.Length - 1), MidpointRounding.AwayFromZero);
            return sortedValues[index];
        }

        static double GetRatioPercent(int numerator, int denominator) {
            if (denominator <= 0) return 0.0;
            return 100.0 * numerator / denominator;
        }

        static BitboardState ApplySymmetryToBoard(BitboardState board, int symmetryIndex) {
            ulong red = board.RedPieces;
            ulong blue = board.BluePieces;
            ulong blocked = board.BlockedSquares;
            BitboardOps.TransformState(ref red, ref blue, ref blocked, symmetryIndex);

            return new BitboardState {
                RedPieces = red,
                BluePieces = blue,
                BlockedSquares = blocked,
                ZobristHash = 0UL
            };
        }

        private sealed class PhaseAwareSampler {
            private readonly int targetSamples;
            private readonly int earlyMaxPieces;
            private readonly int midMaxPieces;
            private readonly int earlyQuota;
            private readonly int midQuota;
            private readonly int lateQuota;
            private readonly Random rng;

            private readonly List<SampledPosition> early;
            private readonly List<SampledPosition> mid;
            private readonly List<SampledPosition> late;
            private readonly List<SampledPosition> fallback;

            private int earlySeen;
            private int midSeen;
            private int lateSeen;

            public PhaseAwareSampler(
                int samplesPerGame,
                int phaseEarlyMaxPieces,
                int phaseMidMaxPieces,
                int phaseEarlyQuota,
                int phaseMidQuota,
                int phaseLateQuota,
                Random rng) {
                targetSamples = Math.Max(0, samplesPerGame);
                earlyMaxPieces = phaseEarlyMaxPieces;
                midMaxPieces = phaseMidMaxPieces;
                this.rng = rng;

                int quotaTotal = Math.Max(0, phaseEarlyQuota) + Math.Max(0, phaseMidQuota) + Math.Max(0, phaseLateQuota);
                if (quotaTotal != targetSamples) {
                    var auto = GetPhaseQuotas(targetSamples);
                    earlyQuota = auto.early;
                    midQuota = auto.mid;
                    lateQuota = auto.late;
                } else {
                    earlyQuota = Math.Max(0, phaseEarlyQuota);
                    midQuota = Math.Max(0, phaseMidQuota);
                    lateQuota = Math.Max(0, phaseLateQuota);
                }

                early = new List<SampledPosition>(earlyQuota);
                mid = new List<SampledPosition>(midQuota);
                late = new List<SampledPosition>(lateQuota);
                fallback = new List<SampledPosition>(targetSamples);
            }

            public void Observe(ushort ply, BitboardState board, AtaxxAIEngine.PlayerColor sideToMove, int legalMoveCount) {
                if (targetSamples <= 0) {
                    return;
                }

                if (!AcceptByMobility(legalMoveCount)) {
                    return;
                }

                var snapshot = new SampledPosition(ply, board.Clone(), sideToMove);
                fallback.Add(snapshot);

                int pieceCount = AtaxxAIEngine.PopCount(board.RedPieces) + AtaxxAIEngine.PopCount(board.BluePieces);
                if (pieceCount <= earlyMaxPieces) {
                    OfferReservoir(early, earlyQuota, ref earlySeen, snapshot);
                } else if (pieceCount <= midMaxPieces) {
                    OfferReservoir(mid, midQuota, ref midSeen, snapshot);
                } else {
                    OfferReservoir(late, lateQuota, ref lateSeen, snapshot);
                }
            }

            public List<SampledPosition> GetFinalSamples() {
                if (targetSamples <= 0) {
                    return new List<SampledPosition>();
                }

                var selected = new Dictionary<ushort, SampledPosition>();
                AddRange(early, selected);
                AddRange(mid, selected);
                AddRange(late, selected);

                if (selected.Count < targetSamples) {
                    AddRange(mid.OrderBy(p => p.Ply), selected, targetSamples);
                }

                if (selected.Count < targetSamples) {
                    AddRange(fallback.OrderBy(p => p.Ply), selected, targetSamples);
                }

                var result = selected.Values.OrderBy(p => p.Ply).ToList();
                if (result.Count > targetSamples) {
                    result = result.Take(targetSamples).ToList();
                }

                return result;
            }

            private bool AcceptByMobility(int moveCount) {
                const int mobilityLow = 6;
                const int mobilityHigh = 16;
                const double mobilityLowAccept = 0.25;

                double acceptProb;
                if (moveCount >= mobilityHigh) {
                    acceptProb = 1.0;
                } else if (moveCount <= mobilityLow) {
                    acceptProb = mobilityLowAccept;
                } else {
                    double t = (double)(moveCount - mobilityLow) / (mobilityHigh - mobilityLow);
                    acceptProb = mobilityLowAccept + (1.0 - mobilityLowAccept) * t;
                }

                return rng.NextDouble() <= acceptProb;
            }

            private void OfferReservoir(List<SampledPosition> bucket, int capacity, ref int seen, SampledPosition sample) {
                if (capacity <= 0) {
                    return;
                }

                seen++;

                if (bucket.Count < capacity) {
                    bucket.Add(sample);
                    return;
                }

                int pick = rng.Next(seen);
                if (pick < capacity) {
                    bucket[pick] = sample;
                }
            }

            private static void AddRange(IEnumerable<SampledPosition> source, Dictionary<ushort, SampledPosition> destination, int maxSize = int.MaxValue) {
                foreach (var sample in source) {
                    if (destination.Count >= maxSize) {
                        return;
                    }

                    destination[sample.Ply] = sample;
                }
            }
        }

        static void RunModelArena(ModelArenaOptions opts) {
            var sharedConfig = AttaxConfigLoader.LoadOrDefault();

            string model1Path = opts.Model1;
            string model2Path = opts.Model2;
            int games = opts.Games;
            int aiDepth = opts.AiDepth;
            bool useOrthogonalOnlyCapture = opts.UseOrthogonalOnlyCapture;
            bool useMLRootOnly = opts.UseMLRootOnly;
            bool disableQuiescence = opts.DisableQuiescence
                ?? (!model1Path.Equals("heuristic", StringComparison.OrdinalIgnoreCase)
                    || !model2Path.Equals("heuristic", StringComparison.OrdinalIgnoreCase));

            var ortProvider = opts.Ort == OrtOption.Cuda
                ? OnnxRuntimeProvider.Cuda
                : OnnxRuntimeProvider.Cpu;

            System.Console.WriteLine($"Starting ModelArena: {model1Path} vs {model2Path}, Games={games}, ORT={opts.Ort}, UseMLRootOnly={(useMLRootOnly ? 1 : 0)}, DisableQuiescenceSearch={(disableQuiescence ? 1 : 0)}");

            IValueEvaluator CreateEvaluator(string path) {
                if (path.Equals("heuristic", StringComparison.OrdinalIgnoreCase))
                    return new HeuristicEvaluator();
                else
                    return new OnnxValueEvaluator(path, ortProvider);
            }

            var eval1 = CreateEvaluator(model1Path);
            var eval2 = CreateEvaluator(model2Path);

            int wins1 = 0;
            int wins2 = 0;
            int draws = 0;

            // We swap sides halfway through
            for (int i = 0; i < games; i++) {
                bool swapSides = i >= games / 2;
                var p1Color = swapSides ? AtaxxAIEngine.PlayerColor.Blue : AtaxxAIEngine.PlayerColor.Red;
                var p2Color = swapSides ? AtaxxAIEngine.PlayerColor.Red : AtaxxAIEngine.PlayerColor.Blue;

                var p1Eval = swapSides ? eval2 : eval1;
                var p2Eval = swapSides ? eval1 : eval2;

                var config = new AtaxxAIEngine.AIEngineConfig {
                    UseTimeManagement = false,
                    AiDepth = aiDepth,
                    UseOrthogonalOnlyCapture = useOrthogonalOnlyCapture,
                    Seed = i,
                    UseMLRootOnly = useMLRootOnly,
                    DisableQuiescenceSearch = disableQuiescence
                };

                var engine1 = new AtaxxAIEngine(p1Eval, null, null, config) {
                    AIPlayerColor = AtaxxAIEngine.PlayerColor.Red,
                    MaxNodes = 1000
                };
                var engine2 = new AtaxxAIEngine(p2Eval, null, null, config) {
                    AIPlayerColor = AtaxxAIEngine.PlayerColor.Blue,
                    MaxNodes = 1000
                };

                var masterBoard = CreateStandardInitialBoard();
                masterBoard.ZobristHash = engine1.ComputeZobristHash(masterBoard, AtaxxAIEngine.PlayerColor.Red);
                AtaxxAIEngine.PlayerColor sideToMove = AtaxxAIEngine.PlayerColor.Red;
                int consecutivePasses = 0;
                bool gameOver = false;
                int plyCount = 0;
                string endReason = "";
                bool debugArena = games == 1;

                while (!gameOver) {
                    // Keep hash synchronized with current side-to-move (important after pass turns).
                    masterBoard.ZobristHash = engine1.ComputeZobristHash(masterBoard, sideToMove);

                    AtaxxAIEngine.Move move = default;
                    if (sideToMove == AtaxxAIEngine.PlayerColor.Red) {
                        engine1.Board = masterBoard.Clone();
                        move = engine1.GetBestMove(sideToMove);
                    } else {
                        engine2.Board = masterBoard.Clone();
                        move = engine2.GetBestMove(sideToMove);
                    }

                    if (move.Equals(default(AtaxxAIEngine.Move))) {
                        consecutivePasses++;
                        sideToMove = AtaxxAIEngine.SwitchPlayer(sideToMove);
                    } else {
                        consecutivePasses = 0;
                        engine1.MakeMove(masterBoard, move, sideToMove);
                        sideToMove = AtaxxAIEngine.SwitchPlayer(sideToMove);
                    }

                    plyCount++;
                    if (engine1.IsGameOver(masterBoard)) {
                        gameOver = true;
                        endReason = "IsGameOver";
                    } else if (consecutivePasses >= 2) {
                        gameOver = true;
                        endReason = "ConsecutivePasses";
                    } else if (plyCount > 400) {
                        gameOver = true;
                        endReason = "PlyCap";
                    }
                }

                var (redCount, blueCount) = AtaxxAIEngine.GetRedAndBlueCounts(masterBoard, AtaxxAIEngine.PlayerColor.Red);

                // Determine winner relative to Model 1
                // If swapSides is false: Model 1 is Red.
                // If swapSides is true: Model 1 is Blue.

                int scoreDiff = redCount - blueCount; // Red - Blue
                if (swapSides) scoreDiff = -scoreDiff; // Blue - Red (Model 1 perspective)

                if (scoreDiff > 0) wins1++;
                else if (scoreDiff < 0) wins2++;
                else draws++;

                if (debugArena) {
                    System.Console.WriteLine($"[arena-debug] game={i + 1}, endReason={endReason}, plies={plyCount}, consecutivePasses={consecutivePasses}, redCount={redCount}, blueCount={blueCount}, swapSides={swapSides}, scoreDiffFromM1={scoreDiff}");
                }

                if ((i + 1) % 10 == 0) {
                    System.Console.WriteLine($"Game {i + 1}: M1 Wins: {wins1}, M2 Wins: {wins2}, Draws: {draws}");
                }
            }

            System.Console.WriteLine($"Final Results: M1 Wins: {wins1}, M2 Wins: {wins2}, Draws: {draws}");

            if (eval1 is IDisposable d1) d1.Dispose();
            if (eval2 is IDisposable d2) d2.Dispose();
        }

        static void RunMirrorTest(MirrorTestOptions opts) {
            string modelPath = opts.Model;

            var ortProvider = opts.Ort == OrtOption.Cuda
                ? OnnxRuntimeProvider.Cuda
                : OnnxRuntimeProvider.Cpu;

            AtaxxAIEngine.PlayerColor sideToMove = opts.Side == MirrorSideOption.Blue
                ? AtaxxAIEngine.PlayerColor.Blue
                : AtaxxAIEngine.PlayerColor.Red;

            IValueEvaluator evaluator = modelPath.Equals("heuristic", StringComparison.OrdinalIgnoreCase)
                ? new HeuristicEvaluator()
                : new OnnxValueEvaluator(modelPath, ortProvider);

            try {
                var board = CreateDeterministicMirrorTestBoard();
                var mirrored = MirrorHorizontal(board);

                float originalValue = evaluator.Evaluate(board, sideToMove, 0);
                float mirroredValue = evaluator.Evaluate(mirrored, sideToMove, 0);

                bool originalFinite = !(float.IsNaN(originalValue) || float.IsInfinity(originalValue));
                bool mirroredFinite = !(float.IsNaN(mirroredValue) || float.IsInfinity(mirroredValue));
                bool magnitudeBounded = Math.Abs(originalValue) < 1_000_000f && Math.Abs(mirroredValue) < 1_000_000f;
                bool boardActuallyMirrored =
                    board.RedPieces != mirrored.RedPieces ||
                    board.BluePieces != mirrored.BluePieces ||
                    board.BlockedSquares != mirrored.BlockedSquares;

                System.Console.WriteLine("Mirror sanity test");
                System.Console.WriteLine($"  Evaluator: {(modelPath.Equals("heuristic", StringComparison.OrdinalIgnoreCase) ? "heuristic" : "onnx")}, ORT={opts.Ort}, SideToMove={sideToMove}");
                System.Console.WriteLine($"  Original value: {originalValue}");
                System.Console.WriteLine($"  Mirrored value: {mirroredValue}");

                int failed = 0;
                failed += PrintAssertion("original is finite", originalFinite) ? 0 : 1;
                failed += PrintAssertion("mirrored is finite", mirroredFinite) ? 0 : 1;
                failed += PrintAssertion("|value| is bounded (< 1e6)", magnitudeBounded) ? 0 : 1;
                failed += PrintAssertion("board changed by horizontal mirror", boardActuallyMirrored) ? 0 : 1;

                if (!modelPath.Equals("heuristic", StringComparison.OrdinalIgnoreCase)) {
                    bool valuesDiffer = Math.Abs(originalValue - mirroredValue) > 1e-6f;
                    failed += PrintAssertion("asymmetric position yields different outputs (optional sanity)", valuesDiffer) ? 0 : 1;
                }

                System.Console.WriteLine(failed == 0
                    ? "Mirror test result: PASS"
                    : $"Mirror test result: FAIL ({failed} assertion(s) failed)");
            } finally {
                if (evaluator is IDisposable disposable) {
                    disposable.Dispose();
                }
            }
        }

        static void RunValidateLog(ValidateLogOptions opts) {
            string path = opts.Path;
            bool strict = opts.Strict;

            if (!File.Exists(path)) {
                System.Console.WriteLine($"Error: File not found: {path}");
                return;
            }

            System.Console.WriteLine($"Validating log: {path} (Strict={strict})");
            var reader = new BinaryTrainingLogReader(strictGameCompleteness: strict);
            int count = 0;
            int errors = 0;

            try {
                foreach (var record in reader.ReadAndValidate(path)) {
                    count++;
                    if (count % 1000 == 0) {
                        System.Console.Write($"\rProcessed {count} records...");
                    }
                }
                System.Console.WriteLine($"\rFinished. Processed {count} records successfully.");
            } catch (Exception ex) {
                System.Console.WriteLine($"\nValidation failed: {ex.Message}");
                errors++;
            }

            if (errors == 0) {
                System.Console.WriteLine("Log is valid.");
            } else {
                System.Console.WriteLine("Log is INVALID.");
            }
        }

        static bool PrintAssertion(string name, bool condition) {
            System.Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {name}");
            return condition;
        }

        static void RunValidateHash(ValidateHashOptions opts) {
            int games = opts.Games;
            int seed = opts.Seed;
            int maxMoves = opts.Moves;
            bool strict = opts.Strict;

            var rng = new Random(seed);
            var evaluator = new HeuristicEvaluator();
            var engine = new AtaxxAIEngine(evaluator, null, null, new AtaxxAIEngine.AIEngineConfig {
                UseTimeManagement = false,
                AiDepth = 1,
                TrainingMode = false,
                DisableParallelRootSearch = true
            });

            int mismatches = 0;
            int positionsChecked = 0;

            try {
                for (int g = 0; g < games; g++) {
                    var board = CreateStandardInitialBoard();
                    var side = AtaxxAIEngine.PlayerColor.Red;
                    board.ZobristHash = engine.ComputeZobristHash(board, side);

                    for (int ply = 0; ply < maxMoves; ply++) {
                        ulong recomputed = engine.ComputeZobristHash(board, side);
                        positionsChecked++;

                        if (board.ZobristHash != recomputed) {
                            mismatches++;
                            string msg = $"Hash mismatch at game={g}, ply={ply}, side={side}: board={board.ZobristHash}, recomputed={recomputed}";
                            if (strict) throw new InvalidOperationException(msg);
                            System.Console.WriteLine($"WARN: {msg}");
                            board.ZobristHash = recomputed;
                        }

                        var moves = engine.GetAllValidMoves(board, side);
                        if (moves.Count == 0) {
                            side = AtaxxAIEngine.SwitchPlayer(side);
                            board.ZobristHash = engine.ComputeZobristHash(board, side);
                            var otherMoves = engine.GetAllValidMoves(board, side);
                            if (otherMoves.Count == 0) break;
                            continue;
                        }

                        var move = moves[rng.Next(moves.Count)];
                        var undo = engine.MakeMoveFast(board, move, side);

                        ulong afterMoveRecompute = engine.ComputeZobristHash(board, AtaxxAIEngine.SwitchPlayer(side));
                        positionsChecked++;
                        if (board.ZobristHash != afterMoveRecompute) {
                            mismatches++;
                            string msg = $"Post-move mismatch at game={g}, ply={ply}, side={side}: board={board.ZobristHash}, recomputed={afterMoveRecompute}";
                            if (strict) throw new InvalidOperationException(msg);
                            System.Console.WriteLine($"WARN: {msg}");
                            board.ZobristHash = afterMoveRecompute;
                        }

                        engine.UnmakeMove(board, move, side, undo);
                        positionsChecked++;
                        if (board.ZobristHash != undo.PreviousZobristHash) {
                            mismatches++;
                            string msg = $"Unmake mismatch at game={g}, ply={ply}, side={side}: board={board.ZobristHash}, expected={undo.PreviousZobristHash}";
                            if (strict) throw new InvalidOperationException(msg);
                            System.Console.WriteLine($"WARN: {msg}");
                            board.ZobristHash = undo.PreviousZobristHash;
                        }

                        engine.MakeMoveFast(board, move, side);
                        side = AtaxxAIEngine.SwitchPlayer(side);
                    }
                }

                System.Console.WriteLine($"Hash validation complete. Checked={positionsChecked}, mismatches={mismatches}, strict={(strict ? 1 : 0)}");
            } finally {
                engine.Dispose();
            }
        }

        static BitboardState CreateDeterministicMirrorTestBoard() {
            var board = new BitboardState();

            // Deterministic, intentionally asymmetric position.
            SetBoardBit(ref board.RedPieces, 0, 0);
            SetBoardBit(ref board.RedPieces, 2, 1);
            SetBoardBit(ref board.RedPieces, 4, 3);
            SetBoardBit(ref board.RedPieces, 6, 5);

            SetBoardBit(ref board.BluePieces, 6, 0);
            SetBoardBit(ref board.BluePieces, 5, 2);
            SetBoardBit(ref board.BluePieces, 1, 4);
            SetBoardBit(ref board.BluePieces, 3, 6);

            SetBoardBit(ref board.BlockedSquares, 1, 1);
            SetBoardBit(ref board.BlockedSquares, 4, 2);
            SetBoardBit(ref board.BlockedSquares, 0, 5);

            return board;
        }

        static void SetBoardBit(ref ulong bits, int x, int y) {
            int index = (y * AttaxConstants.BaseConst.BoardSize) + x;
            bits |= 1UL << index;
        }

        static BitboardState CreateStandardInitialBoard() {
            var board = new BitboardState();

            SetBoardBit(ref board.RedPieces, 0, 0);
            SetBoardBit(ref board.RedPieces, AttaxConstants.BaseConst.BoardSize - 1, AttaxConstants.BaseConst.BoardSize - 1);

            SetBoardBit(ref board.BluePieces, 0, AttaxConstants.BaseConst.BoardSize - 1);
            SetBoardBit(ref board.BluePieces, AttaxConstants.BaseConst.BoardSize - 1, 0);

            return board;
        }

        static BitboardState MirrorHorizontal(BitboardState board) {
            int size = AttaxConstants.BaseConst.BoardSize;

            ulong MirrorBits(ulong bits) {
                ulong mirrored = 0UL;
                for (int y = 0; y < size; y++) {
                    for (int x = 0; x < size; x++) {
                        int srcIndex = (y * size) + x;
                        ulong srcMask = 1UL << srcIndex;
                        if ((bits & srcMask) == 0) {
                            continue;
                        }

                        int dstX = size - 1 - x;
                        int dstIndex = (y * size) + dstX;
                        mirrored |= 1UL << dstIndex;
                    }
                }

                return mirrored;
            }

            return new BitboardState {
                RedPieces = MirrorBits(board.RedPieces),
                BluePieces = MirrorBits(board.BluePieces),
                BlockedSquares = MirrorBits(board.BlockedSquares),
                ZobristHash = 0UL
            };
        }
    }
}
