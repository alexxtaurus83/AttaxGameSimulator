using Microsoft.ML;
using Microsoft.ML.Data;

public class ModelTrainer {
    /// <summary>
    /// Loads data, trains a model, evaluates it, and saves it to a file.
    /// </summary>
    /// <param name="trainingDataPath">The file path for ataxx_training_data.csv.</param>
    public void TrainAndSaveModel(string trainingDataPath) {
        if (!File.Exists(trainingDataPath)) {
            Console.WriteLine($"Training data file not found: {trainingDataPath}");
            return;
        }

        // Initialize the ML.NET environment.
        var mlContext = new MLContext(seed: 0);

        // Load the data from the CSV file into an IDataView.
        Console.WriteLine("Loading training data...");
        IDataView allData = mlContext.Data.LoadFromTextFile<BoardStateInput>(
            path: trainingDataPath,
            hasHeader: true,
            separatorChar: ','
        );

        // Split the data into a training set (80%) and a testing set (20%).
        // The model learns from the training set and is evaluated on the unseen testing set.
        DataOperationsCatalog.TrainTestData dataSplit = mlContext.Data.TrainTestSplit(allData, testFraction: 0.2);
        IDataView trainData = dataSplit.TrainSet;
        IDataView testData = dataSplit.TestSet;

        // Define the training pipeline.
        // It consists of the learning algorithm (trainer).
        // The feature engineering (concatenating columns) is already handled by our [VectorType] attribute.
        var pipeline = mlContext.Regression.Trainers.LightGbm(
            labelColumnName: "Label",   // The column to predict
            featureColumnName: "Features" // The column containing all our features
        );

        // Train the model.
        Console.WriteLine("Training the model... (This may take a while)");
        var trainedModel = pipeline.Fit(trainData);
        Console.WriteLine("Model training complete.");

        // Evaluate the model's performance on the test data.
        Console.WriteLine("Evaluating model performance...");
        var predictions = trainedModel.Transform(testData);
        RegressionMetrics metrics = mlContext.Regression.Evaluate(predictions, labelColumnName: "Label", scoreColumnName: "Score");

        // Print the key metrics.
        Console.WriteLine("--- Model Evaluation Metrics ---");
        // R-Squared is a key metric. It tells you how well the model's predictions correlate
        // with the actual outcomes. A value closer to 1.0 is better.
        Console.WriteLine($"R-Squared: {metrics.RSquared:F2} (closer to 1.0 is better)");
        Console.WriteLine($"Mean Absolute Error: {metrics.MeanAbsoluteError:F2}");
        Console.WriteLine($"Root Mean Squared Error: {metrics.RootMeanSquaredError:F2}");
        Console.WriteLine("--------------------------------");


        // Save the trained model to a .zip file.
        string modelSavePath = Path.Combine(Directory.GetCurrentDirectory(), "ataxx_model.zip");
        mlContext.Model.Save(trainedModel, allData.Schema, modelSavePath);

        Console.WriteLine($"Model saved successfully to: {modelSavePath}");
    }
}