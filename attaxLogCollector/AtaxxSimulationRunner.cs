using Assets.Scripts.Base.Ataxx;
using System.Diagnostics;

public class AtaxxSimulationRunner {

    private AILogCoordinator logData;
    private AtaxxAISimulationSession simulation;

    // AI configurations can be set here before running the simulation.
    // These match the settings from your original Unity class.
    private AtaxxAISimulationSession.AIEngineConfig blueAIConfig = new AtaxxAISimulationSession.AIEngineConfig {
        Depth = 3,        
        UseMLEvaluation = false
        // Add other settings as needed
    };

    private AtaxxAISimulationSession.AIEngineConfig redAIConfig = new AtaxxAISimulationSession.AIEngineConfig {
        Depth = 3,        
        UseMLEvaluation = false
        // Add other settings as needed
    };

    private List<SummaryDisplayInfo> cachedSummaryData = new List<SummaryDisplayInfo>();

    // Helper struct to hold prepared data for the CSV file.
    private struct SummaryDisplayInfo {
        public int SequenceNumber;
        public int TurnNumber;
        public AtaxxAIEngine.PlayerColor Player;
        public AtaxxAIEngine.Move MoveCoords;
        public bool IsClone;
        public int FlippedChips;
        public int RedCountBefore;
        public int BlueCountBefore;
        public int TotalPiecesBefore;
        public int CandidateCount;
        public int TimeMs;

        // Helper to format the data into a CSV-ready string array.
        public string[] ToCsvStringArray() {          
            string emptyCountBefore = (AttaxConstants.BaseConst.BoardSize * AttaxConstants.BaseConst.BoardSize - TotalPiecesBefore).ToString();
            return new string[] {
                SequenceNumber.ToString(),
                TurnNumber.ToString(),
                Player.ToString(),                
                $"\"({MoveCoords.FromX},{MoveCoords.FromY}-{MoveCoords.ToX},{MoveCoords.ToY})\"",
                IsClone.ToString(),
                FlippedChips.ToString(),
                TotalPiecesBefore.ToString(),
                (CandidateCount == -1 ? "N/A" : CandidateCount.ToString()),
                (TimeMs == -1 ? "N/A" : TimeMs.ToString())
            };
        }
    }

    /// <summary>
    /// **MAIN METHOD FOR SIMULATION**
    /// This runs the full AI vs AI simulation from start to finish.
    /// It logs progress to the console and saves the results upon completion.
    /// </summary>
    public void RunAiVsAiSimulation(int simulationId, string trainingFilePath, AtaxxLogger ataxxLogger) {
        logData = new AILogCoordinator();
        simulation = new AtaxxAISimulationSession(
            logData, AtaxxAIEngine.PlayerColor.Blue, AtaxxAIEngine.PlayerColor.Red,
            blueAIConfig, redAIConfig
        );

        Stopwatch sw = Stopwatch.StartNew();
        int movesProcessed = 0;
        Exception caughtSimulationException = null;
        
        int consecutiveStuckTurns = 0;
        int lastTurnIndex = -1;
        const int stuckTurnThreshold = 3; // Game is considered stuck if turn index doesn't change for this many cycles.        

        while (simulation != null && !simulation.IsComplete) {            
            if (logData.TurnIndex == lastTurnIndex) {
                consecutiveStuckTurns++;
            } else {
                consecutiveStuckTurns = 0;
            }
            lastTurnIndex = logData.TurnIndex;

            if (consecutiveStuckTurns >= stuckTurnThreshold) {
                ataxxLogger.WriteTolog($"[Sim {simulationId}] STUCK GAME DETECTED. Forcibly terminating simulation to prevent freeze.");
                break; // Exit the while loop
            }
            

            if (!ProcessOneSimulationStep(ref caughtSimulationException)) {
                break;
            }
            movesProcessed++;
          
        }

        sw.Stop();
        if (caughtSimulationException == null) {           
            MLDataGenerator MLDataGenerator = new MLDataGenerator();
            MLDataGenerator.AppendTrainingDataFromGame(logData, trainingFilePath);

        } else {
            throw caughtSimulationException;
        }
    }

    /// <summary>
    /// Executes one full step of the simulation, where each AI makes a move.
    /// </summary>
    private bool ProcessOneSimulationStep(ref Exception outStepException) {
        outStepException = null;
        if (simulation == null || simulation.IsComplete) {
            return false;
        }

        try {
            simulation.StepAndLogMove(); // This method from your class executes a move for each AI
            return true; // Step was successful
        } catch (Exception ex) {
            outStepException = ex;
            return false; // Step failed
        }
    }

    private void PrepareSummaryData() {
        if (logData == null || logData.GlobalLog == null) return;

        cachedSummaryData.Clear();
        int currentMoveSequence = 1;
        // The GlobalLog is ordered by TurnIndex, which acts as the key.
        var orderedTurns = logData.GlobalLog.OrderBy(kvp => kvp.Key); 

        foreach (var turnEntry in orderedTurns) {
            AILogCoordinator.TurnLog turnLog = turnEntry.Value;
            // Process the first player's move in the turn
            if (turnLog.playerMove != null) {
                cachedSummaryData.Add(CreateSummaryDisplayInfoFromMoveDetails(turnLog.playerMove, turnEntry.Key, ref currentMoveSequence));
            }
            // Process the second player's move in the turn
            if (turnLog.opponentMove != null) {
                cachedSummaryData.Add(CreateSummaryDisplayInfoFromMoveDetails(turnLog.opponentMove, turnEntry.Key, ref currentMoveSequence));
            }
        }
    }

    private SummaryDisplayInfo CreateSummaryDisplayInfoFromMoveDetails(AILogCoordinator.MoveDetails moveDetail, int turnNumber, ref int sequenceNumber) {
        // Get counts before the move from the MoveDetails object
        int totalPiecesBefore = moveDetail.redCount + moveDetail.blueCount;
        int candidateCountValue = -1;
        int timeMsValue = -1;

        if (moveDetail.aIMoveDetails != null) {
            if (moveDetail.aIMoveDetails.aiMoveCandidates != null) {
                candidateCountValue = moveDetail.aIMoveDetails.aiMoveCandidates.Count;
            }
            timeMsValue = moveDetail.aIMoveDetails.time;
        }

        var info = new SummaryDisplayInfo {
            SequenceNumber = sequenceNumber,
            TurnNumber = turnNumber,
            Player = moveDetail.PlayerColor,
            MoveCoords = moveDetail.move,
            IsClone = moveDetail.isClone,
            FlippedChips = moveDetail.flippedChips,
            RedCountBefore = moveDetail.redCount,
            BlueCountBefore = moveDetail.blueCount,
            TotalPiecesBefore = totalPiecesBefore,
            CandidateCount = candidateCountValue,
            TimeMs = timeMsValue
        };
        sequenceNumber++;
        return info;
    }

    /// <summary>
    /// Saves the simulation summary to a CSV file in the application's execution directory.
    /// This is adapted from your original SaveSummarytableToText method.
    /// </summary>
    public void SaveSummaryTableToCSV(int simulationId, AtaxxLogger ataxxLogger) {
        PrepareSummaryData();

        if (cachedSummaryData == null || !cachedSummaryData.Any()) {           
            ataxxLogger.WriteTolog($"[Sim {simulationId}] No summary data was generated to save.");
            return;
        }

        string dirPath = Directory.GetCurrentDirectory();
        // Create a unique timestamp PER BATCH RUN, not per simulation, to group files.
        string timeStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        // Add simulationId to the filename to prevent overwrites within the same second.
        string fileName = $"movedetails_summary_sim{simulationId}_{timeStamp}.csv";
        string filePath = Path.Combine(dirPath, fileName);

        try {
            using (StreamWriter writer = new StreamWriter(filePath)) {
                writer.WriteLine("Move Sequence,Turn number,Player,Move,Is clone,Flipped chips,Red plus Blue,Possible moves to check,Time");
                foreach (var summaryItem in cachedSummaryData) {
                    writer.WriteLine(string.Join(",", summaryItem.ToCsvStringArray()));
                }
            }
        } catch (Exception ex) {
            // Re-throw exception so it can be caught and reported by the batch runner
            throw new IOException($"Failed to save summary file for Sim {simulationId}: {ex.Message}", ex);
        }
    }
    

}