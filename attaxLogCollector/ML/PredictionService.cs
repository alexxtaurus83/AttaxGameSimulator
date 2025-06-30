using Microsoft.ML;
using System;
using System.IO;

public class PredictionService {

    private PredictionEngine<BoardStateInput, BoardStatePrediction> _predictionEngine;
    public bool IsModelLoaded;
    public PredictionService() {
        string modelPath = Path.Combine(Directory.GetCurrentDirectory(), "ataxx_model.zip");
        if (!File.Exists(modelPath)) {
            IsModelLoaded = false;
            Console.WriteLine($"MODEL ERROR: Model file not found at {modelPath}. ML Evaluation will be disabled.");
        }
        try {
            var mlContext = new MLContext();
            ITransformer trainedModel = mlContext.Model.Load(modelPath, out var modelInputSchema);
            _predictionEngine = mlContext.Model.CreatePredictionEngine<BoardStateInput, BoardStatePrediction>(trainedModel);
            IsModelLoaded = _predictionEngine != null;
            if (IsModelLoaded) {
                Console.WriteLine("ML Model loaded successfully.");
            }
        } catch (Exception ex) {
            Console.WriteLine($"MODEL ERROR: Failed to load model. {ex.Message}");
        }
    }

    // This is the public method the AI engine will call.
    public float Predict(BoardStateInput input) {
        // This safety check prevents the crash
        if (!IsModelLoaded) {
            return 0; // Return a neutral score if the model isn't ready
        }
        var prediction = _predictionEngine.Predict(input);
        return prediction.PredictedScore;
    }
    
}