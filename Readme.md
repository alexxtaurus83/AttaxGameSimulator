This project was written to support my Unity mobile game "Ataxx". 
This project was written in colabaration with Gemini 2.5 and Ghat GPT.

# Ataxx AI Engine & ML Trainer Project Overview

This project implements a high-performance AI for the game Ataxx, featuring a sophisticated self-improvement pipeline that uses machine learning to enhance its playing strength. The system is designed to run AI vs. AI simulations to generate training data, which is then used to train a machine learning model. This model can be loaded back into the AI to provide a more intelligent evaluation of game states.

## Core Components

The project is logically divided into two main parts: a game-playing engine and a complete machine learning pipeline.

### 1. Ataxx Game Engine & AI

The core of the project is a highly optimized AI designed for competitive play.

* **Bitboard Representation**: The game state is managed using `ulong` bitboards, which store the positions of red, blue, and blocked pieces. This structure allows for exceptionally fast move generation and state manipulation using bitwise logic.
* **Parallel Alpha-Beta Search**: The AI's logic is driven by a parallelized alpha-beta search algorithm to find the optimal move. The implementation uses `Parallel.ForEach` to evaluate potential moves simultaneously, speeding up the decision-making process.
* **Search Enhancements**: The search algorithm is augmented with several advanced techniques to improve performance:
    * **Transposition Tables**: Caches the evaluation scores of previously analyzed game states using a Zobrist hash to avoid re-computing work.
    * **Quiescence Search**: Extends the search depth for volatile capture moves to ensure evaluations are stable and accurate in tactical situations.
    * **Advanced Pruning**: Employs Killer Moves and Null Move Pruning heuristics to significantly reduce the search space, allowing the AI to search deeper into the game tree.
* **Dynamic Heuristic Evaluation**: The AI's default evaluation function dynamically adjusts its priorities based on the game stage.
    * In the early game, it prioritizes expansion and mobility by rewarding clone moves.
    * In the late game, its focus shifts to maximizing material advantage and ensuring piece stability.

### 2. Machine Learning Pipeline

The project features an end-to-end ML pipeline to replace the heuristic evaluation with a data-driven model.

1.  **Data Generation**: The console application runs batches of AI vs. AI simulations. During each game, a detailed log is created that captures every move and the AI's decision-making process.
2.  **Feature Extraction**: After a game concludes, the log is processed to create a dataset. For each move, a 163-point feature vector is extracted, which includes a full representation of the board, piece counts, mobility, stability, and positional scores.
3.  **Model Training**: The project uses the ML.NET framework to train a model on the generated data.
    * The data is loaded from a CSV file and split into an 80% training set and a 20% testing set.
    * A LightGBM (Light Gradient Boosting Machine) regression model is trained to predict the game's outcome from a given feature vector.
    * The trained model is saved to an `ataxx_model.zip` file.
4.  **Prediction**: The `AtaxxAIEngine` can be configured to use the trained model for evaluation.
    * A `PredictionService` loads the saved `ataxx_model.zip` file.
    * When making a decision, the AI sends the feature vector of a board position to the service and uses the model's prediction as its evaluation score.

## Key Files & Their Roles

| File Name | Role |
| --- | --- |
| `Program.cs` | Main entry point for the console application that orchestrates simulations and model training. |
| `AtaxxSimulationRunner.cs` | Manages the execution of a full AI vs. AI game simulation from start to finish. |
| `AtaxxAISimulationSession.cs` | Sets up and runs a single game session between two configured AI engines. |
| `AtaxxAIEngine.cs` | The core AI brain, implementing the parallel search algorithm and evaluation functions. |
| `BitboardState.cs` | Defines the efficient bitboard data structure used to represent the game board. |
| `AILogCoordinator.cs` | Defines the detailed data structures for logging every aspect of a game for later analysis. |
| `AttaxConstants.cs` | Contains all the tunable parameters and weights for the AI's dynamic heuristic evaluation. |
| `MLDataGenerator.cs` | Processes completed game logs to generate a feature-rich CSV file for training. |
| `FeatureExtractor.cs` | Extracts the 163-feature vector from a single game board state. |
| `MLFeatureSet.cs` | Defines the complete schema and structure of all features used for machine learning. |
| `ModelTrainer.cs` | Uses the ML.NET library to train the regression model on the generated CSV data. |
| `PredictionService.cs` | Loads the trained model and provides an interface for the AI engine to get predictions. |
| `BoardStateInput.cs` | Defines the input and output schema for the ML.NET model, mapping CSV columns to feature vectors. |
| `AtaxxLogger.cs` | A simple logging utility that wraps the Serilog library for writing log files. |
| `AtaxxThreadHelper.cs` | A container class to ensure data is handled in a thread-safe manner during the parallel search. |
| `LogAnalyzer.cs` | A placeholder class intended for a future system to filter game logs for quality before training. |