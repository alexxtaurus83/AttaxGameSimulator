using System;
using System.IO;
using Newtonsoft.Json;

namespace Attax.Core {
    public sealed class AttaxConfig {
        [JsonProperty("board")]
        public BoardConfig Board { get; set; } = new BoardConfig();

        [JsonProperty("model")]
        public ModelConfig Model { get; set; } = new ModelConfig();

        [JsonProperty("logFormat")]
        public LogFormatConfig LogFormat { get; set; } = new LogFormatConfig();        
        
        public sealed class BoardConfig {
            [JsonProperty("size")]
            public int Size { get; set; } = AttaxConstants.BaseConst.BoardSize;

            [JsonProperty("bitIndex")]
            public string BitIndex { get; set; } = "row_major_y_times_size_plus_x";
        }

        public sealed class ModelConfig {
            [JsonProperty("inputChannels")]
            public int InputChannels { get; set; } = 4;

            [JsonProperty("channels")]
            public string[] Channels { get; set; } = new[] { "friendly", "enemy", "blocked", "constant" };

            [JsonProperty("onnxInputName")]
            public string OnnxInputName { get; set; } = "input";

            [JsonProperty("onnxOutputName")]
            public string OnnxOutputName { get; set; } = "output";
        }

        public sealed class LogFormatConfig {
            [JsonProperty("magic")]
            public string Magic { get; set; } = "ATLG";

            [JsonProperty("version")]
            public int Version { get; set; } = 1;

            [JsonProperty("sideEncoding")]
            public SideEncodingConfig SideEncoding { get; set; } = new SideEncodingConfig();

            public sealed class SideEncodingConfig {
                [JsonProperty("red")]
                public int Red { get; set; } = 0;

                [JsonProperty("blue")]
                public int Blue { get; set; } = 1;
            }
        }        
    }

    public static class AttaxConfigLoader {
        public static AttaxConfig LoadOrDefault(string path = "attax.config.json") {
            if (!File.Exists(path)) {
                return new AttaxConfig();
            }

            var json = File.ReadAllText(path);
            var config = JsonConvert.DeserializeObject<AttaxConfig>(json) ?? new AttaxConfig();
            Validate(config, path);
            return config;
        }

        public static void Validate(AttaxConfig config, string sourceName = "attax.config.json") {
            if (config.Board == null) throw new InvalidOperationException($"Invalid config '{sourceName}': missing 'board'.");
            if (config.Model == null) throw new InvalidOperationException($"Invalid config '{sourceName}': missing 'model'.");
            if (config.LogFormat == null) throw new InvalidOperationException($"Invalid config '{sourceName}': missing 'logFormat'.");            

            if (config.Board.Size != AttaxConstants.BaseConst.BoardSize) {
                throw new InvalidOperationException(
                    $"Config board.size={config.Board.Size} does not match compiled BoardSize={AttaxConstants.BaseConst.BoardSize}."
                );
            }           

            if (config.LogFormat.SideEncoding == null ||
                config.LogFormat.SideEncoding.Red != 0 ||
                config.LogFormat.SideEncoding.Blue != 1) {
                throw new InvalidOperationException("Config logFormat.sideEncoding must map red=0 and blue=1.");
            }

            if (config.Model.InputChannels != 4) {
                throw new InvalidOperationException("Config model.inputChannels must be 4 for current feature encoder.");
            }

            if (config.LogFormat.Version != 1 || !string.Equals(config.LogFormat.Magic, "ATLG", StringComparison.Ordinal)) {
                throw new InvalidOperationException("Config logFormat must use magic='ATLG' and version=1.");
            }
        }
    }
}
