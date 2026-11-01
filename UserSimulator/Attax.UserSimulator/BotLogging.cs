using System;
using System.IO;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Attax.UserSimulator {
    public static class BotLogging {
        public static ILogger Create(BotOptions options) {
            Directory.CreateDirectory(options.LogDir);

            Enum.TryParse<LogEventLevel>(options.ConsoleLevel, true, out var consoleLevel);
            Enum.TryParse<LogEventLevel>(options.FileLevel, true, out var fileLevel);

            string logPath = Path.Combine(options.LogDir, $"{options.Name}-.log");
            string jsonlPath = Path.Combine(options.LogDir, $"{options.Name}-.jsonl");

            var loggerConfig = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .Enrich.WithProperty("Bot", options.Name)
                .Enrich.FromLogContext()
                .WriteTo.Console(
                    restrictedToMinimumLevel: consoleLevel,
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Bot} {GameId} {Message:lj}{NewLine}{Exception}"
                )
                .WriteTo.File(
                    logPath,
                    restrictedToMinimumLevel: fileLevel,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{Bot}] [{PlayerId}] [{GameId}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}"
                )
                .WriteTo.File(
                    new CompactJsonFormatter(),
                    jsonlPath,
                    restrictedToMinimumLevel: fileLevel,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    shared: true
                );

            return loggerConfig.CreateLogger();
        }
    }
}
