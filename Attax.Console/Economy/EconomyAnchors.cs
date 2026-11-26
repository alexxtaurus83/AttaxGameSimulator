using System.Text.Json;

namespace Attax.Console.Economy {
    /// <summary>Design targets the calibration converges to. Defaults are the plan's anchors; <c>--anchors &lt;json&gt;</c> overrides any subset.</summary>
    internal sealed class EconomyAnchors {
        /// <summary>Own turn whose opening credit balance seeds the Wall I price on the reference board.</summary>
        public int WallAffordableByOwnTurn { get; set; } = 3;
        /// <summary>Target band of casts per side per game: [min, max].</summary>
        public double[] CastsPerSidePerGame { get; set; } = { 3, 4 };
        /// <summary>Target of expensive casts (every level III, Super Clone, Double Move) per side per game.</summary>
        public double ExpensiveCastsPerSidePerGame { get; set; } = 1;
        /// <summary>Minimum price difference between consecutive levels of one ability (also the rounding step).</summary>
        public int MinLevelStep { get; set; } = 5;
        public bool UnblockL1EqualsWall { get; set; } = true;
        /// <summary>Half-width of the accepted band around each frequency target.</summary>
        public double Tolerance { get; set; } = 0.5;

        public double CastsMid => (CastsPerSidePerGame[0] + CastsPerSidePerGame[1]) / 2.0;
        public double CastsLow => CastsPerSidePerGame[0] - Tolerance;
        public double CastsHigh => CastsPerSidePerGame[1] + Tolerance;
        public double ExpensiveLow => ExpensiveCastsPerSidePerGame - Tolerance;
        public double ExpensiveHigh => ExpensiveCastsPerSidePerGame + Tolerance;

        public static EconomyAnchors Load(string? path, double? tolerance) {
            var anchors = new EconomyAnchors();
            if (!string.IsNullOrWhiteSpace(path)) {
                if (!File.Exists(path)) throw new FileNotFoundException($"Anchors file not found: {path}");
                var loaded = JsonSerializer.Deserialize<EconomyAnchors>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException($"Anchors file {path} is empty.");
                anchors = loaded;
            }
            if (tolerance.HasValue) anchors.Tolerance = tolerance.Value;
            anchors.Validate();
            return anchors;
        }

        public void Validate() {
            if (WallAffordableByOwnTurn < 1) throw new ArgumentException("anchors.wallAffordableByOwnTurn must be >= 1.");
            if (CastsPerSidePerGame == null || CastsPerSidePerGame.Length != 2 || CastsPerSidePerGame[0] <= 0 || CastsPerSidePerGame[1] < CastsPerSidePerGame[0])
                throw new ArgumentException("anchors.castsPerSidePerGame must be [min, max] with 0 < min <= max.");
            if (ExpensiveCastsPerSidePerGame <= 0) throw new ArgumentException("anchors.expensiveCastsPerSidePerGame must be positive.");
            if (MinLevelStep < 1) throw new ArgumentException("anchors.minLevelStep must be >= 1.");
            if (!(Tolerance > 0)) throw new ArgumentException("tolerance must be positive.");
        }
    }
}
