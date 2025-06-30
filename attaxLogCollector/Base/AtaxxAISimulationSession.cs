using System;
using System.Collections.Generic;

namespace Assets.Scripts.Base.Ataxx {
    public class AtaxxAISimulationSession {
        public AtaxxAIEngine opponentAI, humanAI;
        public AILogCoordinator log;
        public AtaxxAIEngine.PlayerColor humanAIcolor, opponentAIcolor; 
        public bool IsComplete;
        private AtaxxLogger ataxxLogger;

        // Helper struct for AI configuration
        public struct AIEngineConfig {
            public bool UseOrthoCapture;
            public int Depth;                                                
            public bool UseMLEvaluation;

            // Static property to get default configuration
            public static AIEngineConfig Default => new AIEngineConfig {
                UseOrthoCapture = false,
                Depth = 3,                                     
                UseMLEvaluation = false 
            };
        }
        
        public AtaxxAISimulationSession(
            AILogCoordinator coordinator, // Using 'coordinator' to match engine
            AtaxxAIEngine.PlayerColor humanColor = AtaxxAIEngine.PlayerColor.Blue,
            AtaxxAIEngine.PlayerColor opponentColor = AtaxxAIEngine.PlayerColor.Red,
            AIEngineConfig? humanAIConfigOverride = null,    // Nullable for easy defaults
            AIEngineConfig? opponentAIConfigOverride = null,
            int? seed = null, // Pass the seed through
            bool randomizeBoard = false // Add a flag to control board setup
        ) {
            this.log = coordinator;
            this.humanAIcolor = humanColor;
            this.opponentAIcolor = opponentColor;
            ataxxLogger = new AtaxxLogger();

            AIEngineConfig humanConfig = humanAIConfigOverride ?? AIEngineConfig.Default;
            AIEngineConfig opponentConfig = opponentAIConfigOverride ?? AIEngineConfig.Default;

            humanAI = new AtaxxAIEngine( // Pass all config parameters
                coordinator: this.log,
                useMLEvaluation: humanConfig.UseMLEvaluation, // Pass the flag
                useOrthogonalOnlyCapture: humanConfig.UseOrthoCapture,
                aiDepth: humanConfig.Depth
            );

            int blockedCellCount = 0; // Number of blocks to generate.                       
            var chipPositions = humanAI.GetInitialChipPositions(randomizeBoard, seed);
            BitboardState initialBoard = CreateBoardWithInitialChips(humanAI, chipPositions);
            if (blockedCellCount > 0) {
                // We need a temporary engine instance just to call the helper method.
                var positionsToBlock = humanAI.GenerateRandomBlockedCellPositions(blockedCellCount, initialBoard, seed);
                // Apply the blocks to our board array before creating the real AIs.
                foreach (var pos in positionsToBlock) {                    
                    initialBoard.BlockedSquares |= (1UL << humanAI.GetBitIndex(pos.x, pos.y));
                }
            }
            humanAI.Board = initialBoard.Clone();            

            opponentAI = new AtaxxAIEngine( // Pass all config parameters
                coordinator: this.log,
                useMLEvaluation: humanConfig.UseMLEvaluation, // Pass the flag
                useOrthogonalOnlyCapture: opponentConfig.UseOrthoCapture,
                aiDepth: opponentConfig.Depth
            );
            opponentAI.Board = initialBoard.Clone();
        }

        /// <summary>
        /// Creates a BitboardState object from a dictionary of initial chip positions.
        /// </summary>
        /// <param name="chipPositions">A dictionary mapping each player to a list of their starting coordinates.</param>
        /// <returns>A new BitboardState object representing the initial board layout.</returns>
        private BitboardState CreateBoardWithInitialChips(AtaxxAIEngine attaxEngine, Dictionary<AtaxxAIEngine.PlayerColor, List<(int x, int y)>> chipPositions) {
            var boardState = new BitboardState();

            if (chipPositions == null) {
                return boardState;
            }

            foreach (var playerPositions in chipPositions) {
                var playerColor = playerPositions.Key;
                ulong playerBitboard = 0UL;

                // Loop through each starting coordinate for the current player
                foreach (var pos in playerPositions.Value) {
                    // Set the bit corresponding to the (x, y) position
                    playerBitboard |= (1UL << attaxEngine.GetBitIndex(pos.x, pos.y));
                }

                // Assign the generated bitboard to the correct property on the new boardState object
                if (playerColor == AtaxxAIEngine.PlayerColor.Red) {
                    boardState.RedPieces = playerBitboard;
                } else if (playerColor == AtaxxAIEngine.PlayerColor.Blue) {
                    boardState.BluePieces = playerBitboard;
                }
            }

            return boardState;
        }
        public void StepAndLogMove() {                        
            try {
                if (IsComplete) return;
                if (!TryDoMove(humanAI, humanAIcolor, isHumanSimulation: true)) { return; }
                opponentAI.Board = humanAI.Board.Clone();                
                if (!TryDoMove(opponentAI, opponentAIcolor, isHumanSimulation: false)) { return; }
                humanAI.Board = opponentAI.Board.Clone();                

            } catch (Exception ex) {
                ataxxLogger.WriteTolog($"Session Exception during AI simulation step. Current log TurnIndex: {log?.TurnIndex}");                
                if (humanAI != null) ataxxLogger.WriteTolog($"Human AI ({humanAIcolor}) possible moves: {humanAI.GetAllValidMoves(humanAI.Board,humanAIcolor)?.Count}");
                if (opponentAI != null && opponentAI.Board != null) ataxxLogger.WriteTolog($"Opponent AI ({opponentAIcolor}) possible moves on its current board: {opponentAI.GetAllValidMoves(opponentAI.Board,opponentAIcolor)?.Count}");
                ataxxLogger.WriteTolog(ex.Message);
                IsComplete = true;
            }
        }

        private bool TryDoMove(AtaxxAIEngine ai, AtaxxAIEngine.PlayerColor playerColor, bool isHumanSimulation = false) {
            var aiValidMoves = ai.GetAllValidMoves(ai.Board,playerColor);
            if (aiValidMoves.Count == 0) {
                ataxxLogger.WriteTolog($"Session: Player {playerColor} has no valid moves. Game Over.");
                IsComplete = true;
                return false;
            }
            var aimove = ai.GetBestMove(playerColor, isHumanSimulation: isHumanSimulation);
            if (!aimove.Equals(default(AtaxxAIEngine.Move))) {
                ai.MakeMove(ai.Board, aimove, playerColor);
            } else {
                ataxxLogger.WriteTolog($"Session CRITICAL: GetBestMove for {playerColor} returned default despite valid moves existing.");
                IsComplete = true;
                return false;
            }
            if (ai.IsGameOver(ai.Board)) { IsComplete = true; return false; }
            IsComplete = false;
            return true;            
        }
    }
}
