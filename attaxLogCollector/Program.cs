using Assets.Scripts.Base.Ataxx;
using Serilog;
using System.Diagnostics;

namespace attaxLogCollector
{
    internal class Program {
        static void Main(string[] args) {
            AtaxxLogger ataxxLogger = new AtaxxLogger();
            ataxxLogger.InitAtaxxLogger();
            ataxxLogger.WriteTolog("====================================");
            ataxxLogger.WriteTolog(" Ataxx AI Batch Simulation Runner ");
            ataxxLogger.WriteTolog("====================================");

            // --- Argument Parsing for Execution Mode ---
            bool generateOnly = false;
            bool trainOnly = false;
            int totalSimulations = 100; // Default number of simulations
            string trainingFilePath = Path.Combine(Directory.GetCurrentDirectory(), "ataxx_training_data.csv");

            if (args.Length > 0) {
                string mode = args[0].ToLower();
                switch (mode) {
                    case "--gen":
                        generateOnly = true;
                        if (args.Length > 1 && int.TryParse(args[1], out int simCount)) {
                            totalSimulations = simCount;
                        }
                        ataxxLogger.WriteTolog($"MODE: Generate Data Only. Target: {totalSimulations} simulations.");
                        break;
                    
                    case "--train":
                        trainOnly = true;
                        ataxxLogger.WriteTolog("MODE: Train Model Only.");
                        break;

                    default:
                        // Legacy mode: argument is just the simulation count
                        if (int.TryParse(args[0], out simCount))
                        {
                            totalSimulations = simCount;
                        }
                        ataxxLogger.WriteTolog($"MODE: Generate Data & Train. Target: {totalSimulations} simulations.");
                        break;
                }
            }
            else {
                ataxxLogger.WriteTolog("MODE: Generate Data & Train (Default). Target: 100 simulations.");
            }
            
            // --- Execution Logic ---
            if (!trainOnly) {
                // This block runs for "Generate Only" and the default "Generate & Train" modes                
                RunSimulations(totalSimulations, trainingFilePath, ataxxLogger);
            }

            if (!generateOnly) {
                // This block runs for "Train Only" and the default "Generate & Train" modes
                ataxxLogger.WriteTolog("Starting model training...");
                ModelTrainer modelTrainer = new ModelTrainer();
                modelTrainer.TrainAndSaveModel(trainingFilePath);
                ataxxLogger.WriteTolog("Model training complete.");
            }

            ataxxLogger.WriteTolog("Execution finished.");
            Console.WriteLine("\nPress any key to exit.");
            Console.ReadKey();
        }

        /// <summary>
        /// Runs the specified number of simulations and appends data to the training file.
        /// </summary>
        private static void RunSimulations(int totalSimulations, string trainingFilePath, AtaxxLogger ataxxLogger) {
            var allErrors = new List<string>();
            ataxxLogger.WriteTolog($"Preparing to run {totalSimulations} simulations sequentially...");
            var totalStopwatch = Stopwatch.StartNew();
            for (int i = 0; i < totalSimulations; i++) {
                int simulationId = i;
                // Update the console status for the current simulation
                string status = $"Running simulation {simulationId + 1} of {totalSimulations}...";
                Console.Write($"\r{status.PadRight(80)}"); // Pad right to clear previous line

                try {
                    var simulationRunner = new AtaxxSimulationRunner();
                    // Pass the training file path to the runner
                    simulationRunner.RunAiVsAiSimulation(simulationId, trainingFilePath, ataxxLogger);
                }
                catch (Exception ex) {
                    string errorMsg = $"[Sim {simulationId}] FAILED: {ex.Message}";
                    allErrors.Add(errorMsg);
                    ataxxLogger.WriteTolog(errorMsg);
                }
            }

            totalStopwatch.Stop();
            Console.WriteLine(); // New line after progress bar
            ataxxLogger.WriteTolog($"\nAll {totalSimulations} simulations completed in {totalStopwatch.Elapsed.TotalSeconds:F2}s.");

            if (allErrors.Any()) {
                Log.Warning("--- Simulation Errors Report ({Count} total) ---", allErrors.Count);
                foreach (var error in allErrors)
                {
                    Log.Error(error);
                }
                Log.Warning("--- End of Report ---");
            }
        }
    }
}
