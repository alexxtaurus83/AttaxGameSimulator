
using Serilog;
using System.IO;

namespace Attax.Core {
    public class SerilogLogger : ILogSink {
        public void InitAtaxxLogger() {
            Serilog.Log.Logger = new LoggerConfiguration()
              .MinimumLevel.Information()
              .WriteTo.File(Path.Combine("debug.log"))
              .CreateLogger();
        }
        public void LogInformation(string logline) {
            Log.Information(logline);
        }

        public void LogError(string message) {
            Log.Error(message);
        }
    }
}
