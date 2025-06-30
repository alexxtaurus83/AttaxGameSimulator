using Microsoft.ML.Data;

// This class defines the schema of input CSV file.
// It uses attributes to tell ML.NET which column to load into which property.
public class BoardStateInput {
    // A special attribute to load all 'F' and 'BS_' columns into a single feature vector.
    // The number of features must match your CSV header exactly.
    // From our last step, this is 98 (BoardState) + 16 (Other) = 114 features.
    [VectorType(163)]
    [LoadColumn(0, 162)]
    public float[] Features { get; set; }

    [LoadColumn(163)]
    public float Label { get; set; }
    
}

// This class defines the output of the model's prediction.
public class BoardStatePrediction {
    // "Score" is the default output column name for regression models.
    [ColumnName("Score")]
    public float PredictedScore { get; set; }
}